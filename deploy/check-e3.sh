#!/usr/bin/env bash
# E3 核对：systemd 拉起空进程，改代码走换进程。
#
# 在笔记本上、仓库根目录执行：
#   sudo bash deploy/check-e3.sh            # 默认核 Gateway
#   sudo bash deploy/check-e3.sh Battle     # 换一个进程
#
# 只做四件事：装 mmo@.service、发布一次空进程、重启一次、换一次二进制再起来。
# 不读写数据库，不改仓库文件；发布产物写到 /srv/mmo/app/<实例名>/。

set -uo pipefail

INSTANCE=${1:-Gateway}
UNIT="mmo@${INSTANCE}.service"
APP_ROOT=${APP_ROOT:-/srv/mmo/app}
ENV_FILE=${ENV_FILE:-/srv/mmo/gamedb.env}
REPO=$(cd "$(dirname "$0")/.." && pwd)
TEMPLATE="$REPO/deploy/systemd/mmo@.service"
UNIT_PATH="/etc/systemd/system/mmo@.service"
APP_DIR="$APP_ROOT/$INSTANCE"
DLL="$APP_DIR/$INSTANCE.dll"

PASS=0
FAIL=0
STAGE=""

say()  { printf '\n%s\n' "$1"; }
ok()   { PASS=$((PASS+1)); printf '  PASS  %s\n' "$1"; }
bad()  { FAIL=$((FAIL+1)); printf '  FAIL  %s\n' "$1"; }
info() { printf '        %s\n' "$1"; }

cleanup() { [ -n "$STAGE" ] && rm -rf "$STAGE" "$STAGE".v2 "$STAGE".log "$STAGE".v2.log; }
trap cleanup EXIT

active()     { [ "$(systemctl is-active "$UNIT" 2>/dev/null)" = "active" ]; }
main_pid()   { systemctl show -p MainPID --value "$UNIT" 2>/dev/null | tr -d ' '; }
alive()      { [ -n "${1:-}" ] && [ "${1:-0}" -gt 0 ] && kill -0 "$1" 2>/dev/null; }
proc_count() { local n; n=$(pgrep -fc "dotnet .*/${INSTANCE}\.dll" 2>/dev/null); printf '%s' "${n:-0}"; }
journal()    { journalctl -u "$UNIT" --since "$1" -o cat --no-pager 2>/dev/null; }
# journald 是异步落盘的，刚起来的进程可能晚几百毫秒才出现，所以这里等一会儿再看
journal_has(){ local since=$1 pat=$2 i; for i in $(seq 1 20); do journal "$since" | grep -q -- "$pat" && return 0; sleep 0.25; done; return 1; }
wait_active(){ local i; for i in $(seq 1 40); do active && return 0; sleep 0.25; done; return 1; }
wait_gone()  { local p=${1:-0}; [ "$p" -gt 0 ] || return 1; local i; for i in $(seq 1 40); do alive "$p" || return 0; sleep 0.25; done; return 1; }
hash_of()    { sha256sum "$1" 2>/dev/null | cut -c1-12; }

# 逐个文件复制成新 inode，避免覆盖正在被映射的程序集
sync_tree() {
  local src=$1 dst=$2
  mkdir -p "$dst"
  ( cd "$src" && find . -type d -print0 ) | while IFS= read -r -d '' d; do mkdir -p "$dst/$d"; done
  ( cd "$src" && find . -type f -print0 ) | while IFS= read -r -d '' f; do
    cp -f "$src/$f" "$dst/$f.new" && mv -f "$dst/$f.new" "$dst/$f"
  done
}

publish_to() { # $1 输出目录，$2... 额外属性
  local out=$1; shift
  dotnet publish "$REPO/src/$INSTANCE/$INSTANCE.csproj" -c Release -o "$out" \
    --nologo -v q "$@" >"$out.log" 2>&1
}

say "E3 · $UNIT · $(date '+%Y-%m-%d %H:%M:%S')"

# ---------- 0 前置 ----------
say "0 · 前置"
if [ "$(id -u)" -ne 0 ]; then
  bad "需要 root 才能装单元和调 systemctl。用：sudo bash deploy/check-e3.sh $INSTANCE"
  exit 1
fi
ok "以 root 运行（$(whoami)@$(hostname)）"

if command -v systemctl >/dev/null 2>&1; then
  ok "systemd 可用：$(systemctl --version | head -1)"
else
  bad "没有 systemctl，这台机器不是 systemd 环境"
  exit 1
fi

DOTNET=$(command -v dotnet || true)
if [ -n "$DOTNET" ] && "$DOTNET" --version >/dev/null 2>&1; then
  ok "dotnet：$DOTNET（$("$DOTNET" --version)）"
else
  bad "找不到 dotnet，先按 E1 装 SDK 10.0.403"
  exit 1
fi

if [ -f "$REPO/src/$INSTANCE/$INSTANCE.csproj" ]; then
  ok "工程存在：src/$INSTANCE/$INSTANCE.csproj"
else
  bad "找不到 src/$INSTANCE/$INSTANCE.csproj，实例名要和工程名一致"
  exit 1
fi

if [ -f "$ENV_FILE" ]; then
  ok "环境文件在位：$ENV_FILE（$(stat -c '%a' "$ENV_FILE")）"
else
  info "环境文件不在：$ENV_FILE（空进程不读库，不影响本行核对）"
fi

# ---------- 1 单元 ----------
say "1 · 单元"
RENDERED=$(sed "s|@DOTNET@|$DOTNET|g" "$TEMPLATE")
if [ -f "$UNIT_PATH" ] && [ "$RENDERED" = "$(cat "$UNIT_PATH")" ]; then
  ok "单元已是最新：$UNIT_PATH"
else
  printf '%s\n' "$RENDERED" >"$UNIT_PATH"
  systemctl daemon-reload
  ok "已写入并重载：$UNIT_PATH"
fi
if systemctl cat "$UNIT" >/dev/null 2>&1; then
  ok "systemd 认得 $UNIT"
else
  bad "systemd 不认识 $UNIT"
  exit 1
fi

# ---------- 2 发布并启动 ----------
say "2 · 发布空进程并拉起"
STAGE=$(mktemp -d)
if publish_to "$STAGE"; then
  sync_tree "$STAGE" "$APP_DIR"
  ok "发布完成：$APP_DIR"
else
  bad "dotnet publish 失败"
  tail -5 "$STAGE.log"
  exit 1
fi
H1=$(hash_of "$DLL")
info "程序集 sha256：$H1"

systemctl reset-failed "$UNIT" 2>/dev/null || true
T0=$(date '+%Y-%m-%d %H:%M:%S')
systemctl start "$UNIT"
if wait_active; then ok "启动后状态 active"; else bad "启动后不是 active（$(systemctl is-active "$UNIT")）"; fi

PID1=$(main_pid)
if alive "$PID1"; then ok "MainPID 存活：$PID1"; else bad "MainPID 不可用：${PID1:-空}"; fi
if [ "$(proc_count)" -eq 1 ]; then ok "同名进程只有 1 个"; else bad "同名进程有 $(proc_count) 个，换进程会留下孤儿"; fi
if journal_has "$T0" "ready name=$INSTANCE pid=$PID1"; then
  ok "进程自报就绪：ready name=$INSTANCE pid=$PID1"
else
  bad "日志里没有 ready name=$INSTANCE pid=$PID1"
fi

# ---------- 3 重启：旧退出，新起来 ----------
say "3 · 重启：旧进程退出，新进程起来"
T1=$(date '+%Y-%m-%d %H:%M:%S')
systemctl restart "$UNIT"
if wait_active; then ok "重启后状态 active"; else bad "重启后不是 active"; fi
PID2=$(main_pid)
if [ "$PID2" != "$PID1" ]; then ok "MainPID 已更换：$PID1 → $PID2"; else bad "MainPID 没变，仍是 $PID1"; fi
if wait_gone "$PID1"; then ok "旧进程 $PID1 已退出"; else bad "旧进程 $PID1 仍在"; fi
if alive "$PID2"; then ok "新进程存活：$PID2"; else bad "新进程不存活"; fi
if [ "$(proc_count)" -eq 1 ]; then ok "重启后仍只有 1 个进程"; else bad "重启后同名进程有 $(proc_count) 个"; fi
if journal_has "$T1" "stopped name=$INSTANCE pid=$PID1"; then
  ok "旧进程收到 SIGTERM 后正常退出，不是被 SIGKILL"
else
  bad "旧进程没有正常退出的日志"
fi
if journal_has "$T1" "ready name=$INSTANCE pid=$PID2"; then
  ok "新进程自报就绪：pid=$PID2"
else
  bad "新进程没有就绪日志"
fi

# ---------- 4 改代码：换二进制，再换进程 ----------
say "4 · 改代码：换二进制，再换进程"
systemctl stop "$UNIT"
if wait_gone "$PID2"; then ok "停服后 $PID2 已退出"; else bad "停服后 $PID2 仍在"; fi

STAGE2="$STAGE.v2"
mkdir -p "$STAGE2"
if publish_to "$STAGE2" -p:Version="0.0.$RANDOM"; then
  sync_tree "$STAGE2" "$APP_DIR"
  ok "换了一份新构建：$(date '+%H:%M:%S')"
else
  bad "第二次 publish 失败"
  tail -5 "$STAGE2.log"
fi
H2=$(hash_of "$DLL")
if [ "$H1" != "$H2" ]; then ok "程序集确实变了：$H1 → $H2"; else bad "程序集没变，换进程无从核对"; fi

T2=$(date '+%Y-%m-%d %H:%M:%S')
systemctl start "$UNIT"
if wait_active; then ok "换二进制后能起来"; else bad "换二进制后起不来（$(systemctl is-active "$UNIT")）"; fi
PID3=$(main_pid)
if [ "$PID3" != "$PID2" ]; then ok "新进程号：$PID3"; else bad "进程号没变：$PID3"; fi
if [ "$(proc_count)" -eq 1 ]; then ok "只有 1 个进程在跑"; else bad "同名进程有 $(proc_count) 个"; fi
if [ -r "/proc/$PID3/maps" ] && grep -q "$DLL" "/proc/$PID3/maps" 2>/dev/null; then
  if grep -q "$DLL (deleted)" "/proc/$PID3/maps" 2>/dev/null; then
    bad "进程映射的仍是旧文件（(deleted)），等于没换"
  else
    ok "进程映射的是这一份新程序集：$DLL"
  fi
else
  info "读不到 /proc/$PID3/maps，跳过映射核对"
fi
if journal_has "$T2" "ready name=$INSTANCE pid=$PID3"; then
  ok "新进程自报就绪：pid=$PID3"
else
  bad "新进程没有就绪日志"
fi

# ---------- 5 优雅停止 ----------
say "5 · 停止：正常退出，不留进程"
T3=$(date '+%Y-%m-%d %H:%M:%S')
systemctl stop "$UNIT"
if wait_gone "$PID3"; then ok "停服后 $PID3 已退出"; else bad "停服后 $PID3 仍在"; fi
ST=$(systemctl is-active "$UNIT" 2>/dev/null)
if [ "$ST" = "inactive" ]; then ok "单元状态：inactive"; else bad "单元状态：$ST"; fi
CODE=$(systemctl show -p ExecMainStatus --value "$UNIT" 2>/dev/null | tr -d ' ')
if [ "$CODE" = "0" ]; then ok "退出码 0（SIGTERM 被接住）"; else bad "退出码 ${CODE:-空}，不是正常退出"; fi
if [ "$(main_pid)" = "0" ]; then ok "MainPID 已清零"; else bad "MainPID 仍是 $(main_pid)"; fi
if [ "$(proc_count)" -eq 0 ]; then ok "没有残留进程"; else bad "还有 $(proc_count) 个进程残留"; fi
if journal_has "$T3" "stopped name=$INSTANCE pid=$PID3"; then
  ok "日志记下了停止：stopped name=$INSTANCE pid=$PID3"
else
  info "停止日志缺失（有的发行版会截断最后一次输出）"
fi

# ---------- 6 不靠进程内重载 ----------
say "6 · 不靠进程内重载"
RELOAD=$(systemctl show -p ExecReload --value "$UNIT" 2>/dev/null | tr -d ' ')
if [ -z "$RELOAD" ]; then ok "单元没有 ExecReload"; else bad "单元带 ExecReload：$RELOAD"; fi
if grep -Eq '^[[:space:]]*ExecReload[[:space:]]*=' "$TEMPLATE"; then bad "模板里写了 ExecReload"; else ok "模板里不写 ExecReload"; fi

# ---------- 结果 ----------
say "结果"
printf '  %d 项通过，%d 项未通过\n' "$PASS" "$FAIL"
if [ "$FAIL" -eq 0 ]; then
  printf '  E3 核对通过：重启后旧进程已退出，新进程能起来，改代码走换进程。\n'
  exit 0
fi
printf '  E3 未通过：按上面 FAIL 行逐条处理，处理完重跑本脚本。\n'
exit 1
