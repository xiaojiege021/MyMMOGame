#!/usr/bin/env bash
# tools/env/service-harness.sh —— 一次性起服 harness 的公共函数库（source 使用）。
#
# 为什么有这一份：P1-5c7 补「重启角色服」证据时连撞两个环境坑，且每张卡都会重踩：
#   1. 等网关的 `^listen=` 配置日志太早（Kestrel 还没绑定），login 直接连接被拒，
#      看着像鉴权坏了。→ harness_wait_ready 等 `Now listening on` / `ready name=`。
#   2. `lsof -ti:62000` 不只列监听者，连「连到 62000 的客户端」也列出来，
#      杀角色服时把网关一起 TERM 了。→ harness_kill_port 只按 `-sTCP:LISTEN` 定位。
#   3. 改配置重启后网关旧 gRPC 子通道没换，只读请求一直 502。→ harness_wait_http 轮询。
#   4. 失败退出后 dotnet 进程残留在端口上，下一次 harness 起不来、还以为是代码问题。
#      → harness_init 装 EXIT 清理，harness_cleanup 按 pid + 端口收干净。
#
# 用法（harness 脚本里只写「起哪几个服务 + 跑哪个 check + 传哪些变量」）：
#
#   source "$ROOT/tools/env/service-harness.sh"
#   harness_init "${TMPDIR:-/tmp}/p15c8"
#   harness_start "$HARNESS_LOG/character.log" "$DOTNET" "$CHARACTER_DLL" --config config/character.json
#   harness_track_port 62000
#   harness_wait_ready "$HARNESS_LOG/character.log" "角色服"
#   TOKEN="$(harness_login 61000 "$ACCOUNT" "$PASSWORD")"
#   harness_wait_http "http://127.0.0.1:61000/health" 200
#
# 约定：
#   - macOS 自带 bash 3.2 也能跑：不用关联数组、不用 ${var,,}、不用 local -n。
#   - 只做进程 / 端口 / HTTP / rsync 这些与业务无关的事，不读配置、不判玩法。
#   - 不在这里 set -euo pipefail：source 会改调用方的 shell 选项，由调用方自己决定。
#   - 超时默认值按「本机起服 + 笔记本真服」两档留余量，够慢机器用。

: "${HARNESS_LOG:=${TMPDIR:-/tmp}}"
HARNESS_PIDS=""
HARNESS_PORTS=""

# harness_init [日志目录]：建目录、清空跟踪表、装 EXIT/INT/TERM 清理。
# 注意：会覆盖调用方已有的 EXIT trap；需要自己的 trap 就在 init 之后重设。
harness_init() {
  HARNESS_LOG="${1:-$HARNESS_LOG}"
  mkdir -p "$HARNESS_LOG"
  HARNESS_PIDS=""
  HARNESS_PORTS=""
  trap 'harness_cleanup' EXIT INT TERM
  printf 'harness: 日志目录 %s\n' "$HARNESS_LOG"
}

# harness_track_pid <pid>：登记一个进程，退出时按 TERM → KILL 收掉。
harness_track_pid() {
  HARNESS_PIDS="${HARNESS_PIDS} $1"
}

# harness_track_port <port>：登记一个端口，退出时按「监听态」收掉其上的进程。
harness_track_port() {
  HARNESS_PORTS="${HARNESS_PORTS} $1"
}

# harness_start <日志> <命令> [参数...]：后台起一个服务，回显 pid。
harness_start() {
  local log="$1"
  shift
  "$@" >"$log" 2>&1 &
  local pid=$!
  harness_track_pid "$pid"
  printf '%s' "$pid"
}

# harness_stop_pid <pid>：先 TERM 等 3 秒，还在就 KILL。
harness_stop_pid() {
  local pid="$1" i
  [ -n "$pid" ] || return 0
  kill -0 "$pid" 2>/dev/null || return 0
  kill -TERM "$pid" 2>/dev/null || true
  for i in $(seq 1 30); do
    kill -0 "$pid" 2>/dev/null || return 0
    sleep 0.1
  done
  kill -KILL "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
}

# harness_listen_pids <port>：只列「监听该端口」的 pid。
# 用 lsof -sTCP:LISTEN，不用 -ti:port（后者连客户端一起列，会误杀上游）。
harness_listen_pids() {
  local port="$1"
  if command -v lsof >/dev/null 2>&1; then
    lsof -nP -tiTCP:"$port" -sTCP:LISTEN 2>/dev/null || true
  elif command -v ss >/dev/null 2>&1; then
    ss -ltnp 2>/dev/null | awk -v p=":$port" '$4 ~ p"$" {print}' \
      | grep -oE 'pid=[0-9]+' | cut -d= -f2 | sort -u
  fi
}

# harness_kill_port <port>：按监听态收掉端口上的进程（先 TERM 后 KILL）。
harness_kill_port() {
  local port="$1" pids pid i
  pids="$(harness_listen_pids "$port")"
  [ -n "$pids" ] || return 0
  for pid in $pids; do kill -TERM "$pid" 2>/dev/null || true; done
  for i in $(seq 1 30); do
    [ -z "$(harness_listen_pids "$port")" ] && return 0
    sleep 0.1
  done
  for pid in $pids; do kill -KILL "$pid" 2>/dev/null || true; done
}

# harness_cleanup：收掉所有登记过的进程与端口。EXIT trap 调用，也可手工调。
harness_cleanup() {
  local pid port
  for pid in $HARNESS_PIDS; do harness_stop_pid "$pid"; done
  for port in $HARNESS_PORTS; do harness_kill_port "$port"; done
  HARNESS_PIDS=""
  HARNESS_PORTS=""
}

# harness_wait_port <port> [标签] [次数] [间隔秒]：等到有人监听该端口。
# 默认 120 次 × 0.5 秒 = 60 秒。
harness_wait_port() {
  local port="$1" label="${2:-端口 $1}" tries="${3:-120}" interval="${4:-0.5}" i
  for i in $(seq 1 "$tries"); do
    [ -n "$(harness_listen_pids "$port")" ] && return 0
    sleep "$interval"
  done
  printf 'harness: %s（%s）一直没在监听\n' "$label" "$port" >&2
  return 1
}

# harness_wait_ready <日志> [标签] [次数]：等 Kestrel 真正绑定。
# 认 `Now listening on`（Kestrel）或 `ready name=`（本项目进程自打的就绪行）；
# 不认 `listen=`：那行是读配置时打的，那时端口还没绑上。
harness_wait_ready() {
  local log="$1" label="${2:-服务}" tries="${3:-240}" i
  for i in $(seq 1 "$tries"); do
    if grep -qE 'Now listening on|ready name=' "$log" 2>/dev/null; then
      return 0
    fi
    sleep 0.25
  done
  printf 'harness: %s 没等到 Now listening on / ready name=\n' "$label" >&2
  tail -20 "$log" >&2 2>/dev/null || true
  return 1
}

# harness_wait_http <url> [期望码] [次数] [间隔秒]：轮询只读请求。
# 主要给「改配置重启后网关旧 gRPC 子通道没换、只读一直 502」用；默认等 200。
harness_wait_http() {
  local url="$1" want="${2:-200}" tries="${3:-120}" interval="${4:-0.5}" i code=""
  for i in $(seq 1 "$tries"); do
    code="$(curl -sS --noproxy '*' -m 2 -o /dev/null -w '%{http_code}' "$url" 2>/dev/null || true)"
    [ "$code" = "$want" ] && return 0
    sleep "$interval"
  done
  printf 'harness: %s 一直回不到 %s（最后 %s）\n' "$url" "$want" "${code:-无响应}" >&2
  return 1
}

# harness_login <网关端口> <账号> <口令> [次数]：POST /login 换令牌，失败重试。
# 回显令牌；拿不到返回 1。重试是为了盖住「端口通了但 Kestrel 还没完全就绪」的窗口。
harness_login() {
  local port="$1" account="$2" password="$3" tries="${4:-60}" i token=""
  for i in $(seq 1 "$tries"); do
    token="$(curl -sS --noproxy '*' -m 3 -X POST "http://127.0.0.1:${port}/login" \
      -H 'Content-Type: application/json' \
      -d "{\"account\":\"${account}\",\"password\":\"${password}\"}" 2>/dev/null \
      | python3 -c 'import json, sys
try:
    print(json.load(sys.stdin).get("token", ""))
except Exception:
    print("")' || true)"
    if [ -n "$token" ]; then
      printf '%s' "$token"
      return 0
    fi
    sleep 0.5
  done
  printf 'harness: 登录 %s 拿不到令牌（重试 %s 次）\n' "$account" "$tries" >&2
  return 1
}

# harness_rsync <本地目录> <主机> <远端目录> [额外 rsync 参数...]：
# 整目录同步到笔记本，去掉 .git/bin/obj/Library/var 这些不该传的。
harness_rsync() {
  local src="$1" host="$2" dest="$3"
  shift 3
  rsync -az --delete \
    --exclude '.git/' \
    --exclude 'bin/' \
    --exclude 'obj/' \
    --exclude 'client/MMOGame/Library/' \
    --exclude 'var/' \
    "$@" "$src" "$host:$dest"
}
