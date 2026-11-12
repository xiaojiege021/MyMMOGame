#!/usr/bin/env bash
# tools/check/lib-common.sh —— 核对脚本公共函数库。
#
# 用途：把各张 check-<卡号>.sh 里逐字重复的辅助函数集中到这一份，
#       新卡直接 `source "$ROOT/tools/check/lib-common.sh"` 复用，不用再复制粘贴。
#
# 约定（开发架构第 15 节）：
#   - tools/ 顶层只有 check/ env/ gen/ probe/ 四类目录，本库放在 check/ 目录内，
#     不叫 check-<卡号>.sh，所以不会被 check-all.sh 总控误当成一张卡。
#   - 本库只提供「无副作用」的辅助函数，不定义端口/账号/配置路径这些每卡不同的变量，
#     那些由调用方脚本自己定义。source 本库前请先定义好 PASS/FAIL（默认 0）。
#
# 已提供的函数（签名与既有脚本逐字一致，行为不变）：
#   ok <msg> / bad <msg> / detail          计数与输出
#   stop_pid <pid>                          收进程
#   body_of <resp> / code_of <resp>         curl 响应拆码
#   json_field <json> <key>                 取一个 JSON 字段
#   expect_json <body> key=val ...          断言若干 key=val（数值/布尔/字符串兼容）
#   wait_ready <log> <pattern> <label>      等服务起来，超时打 FAIL 并贴日志尾
#   wait_line <log> <pattern>               等服务日志出现某行，超时只返回 1
#   finish <标题>                           输出汇总并据 FAIL 定退出码
#
# 说明：wait_ready / wait_line 会调用 bad / detail，因此必须先 source 本库；
#       它们不依赖任何调用方变量（除 PASS/FAIL 计数）。

# 计数变量若调用方未定义则给默认值，避免 source 时未初始化。
: "${PASS:=0}"
: "${FAIL:=0}"

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
detail() { sed 's/^/       /'; }

stop_pid() {
  local pid="$1"
  [ -n "$pid" ] || return 0
  if kill -0 "$pid" 2>/dev/null; then
    kill -TERM "$pid" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
}

body_of() { printf '%s' "${1%$'\n'*}"; }
code_of() { printf '%s' "${1##*$'\n'}"; }

json_field() {
  printf '%s' "$1" | python3 -c 'import json,sys
try:
    v = json.load(sys.stdin).get("'"$2"'", "")
    if isinstance(v, bool): v = str(v).lower()
    print(v)
except Exception:
    print("")'
}

expect_json() {
  local body="$1"
  shift
  printf '%s' "$body" | EXPECT="$*" python3 -c '
import json, os, sys
try:
    d = json.load(sys.stdin)
except Exception:
    print("bad_json"); sys.exit(1)
for pair in os.environ["EXPECT"].split():
    key, want = pair.split("=", 1)
    got = d.get(key, "")
    if isinstance(got, bool): got = "true" if got else "false"
    try:
        if float(got) == float(want): continue
    except (TypeError, ValueError):
        pass
    if str(got) == want: continue
    print("mismatch %s got=%r want=%r" % (key, got, want))
    sys.exit(1)
print("ok")'
}

wait_ready() {
  local log="$1" pattern="$2" label="$3"
  for _ in $(seq 1 120); do
    if grep -q "$pattern" "$log" 2>/dev/null; then return 0; fi
    sleep 0.1
  done
  bad "$label 没起来"; tail -12 "$log" | detail
  return 1
}

wait_line() {
  local log="$1" pattern="$2"
  for _ in $(seq 1 40); do
    if grep -q "$pattern" "$log" 2>/dev/null; then return 0; fi
    sleep 0.1
  done
  return 1
}

# ---------------------------------------------------------------------------
# 以下四个是 P1-5c8 起新卡直接复用的高频 helper（P1-5c7 之前每张卡各抄一份）。
# 调用方脚本若自己定义了同名函数并写在 source 之后，以脚本的为准。
#
#   assert_file_contains <file> <regex> <msg>
#       静态断言：文件里有匹配行就 ok，没有就 bad。
#   assert_bad_config <output> <msg>
#       只读入口遇到坏配置必须打 config_error= 并以非 0 退出；
#       output 可以是命令输出，也可以是 "退出码 + 输出"（只看 config_error= 行）。
#   pick_pending_shop_item [shop.json]
#       打印「id kind」，挑第一条 delivery_item_id 为空的商品；没有则打印空。
#       不要写死某个编号：后面的卡会陆续接上发货。
#   db_cli <args...>
#       跑角色服只读入口；调用方须先定义 DOTNET 与 CHARACTER_DLL。
# ---------------------------------------------------------------------------

assert_file_contains() {
  local file="$1" pattern="$2" msg="$3"
  if [ -f "$file" ] && grep -qE -- "$pattern" "$file"; then
    ok "$msg"
  else
    bad "$msg"
  fi
}

assert_bad_config() {
  local out="$1" msg="$2"
  if printf '%s' "$out" | grep -q '^config_error='; then
    ok "$msg"
  else
    bad "$msg"
  fi
}

pick_pending_shop_item() {
  python3 -c 'import json, sys
path = sys.argv[1] if len(sys.argv) > 1 else "config/shop.json"
try:
    d = json.load(open(path, encoding="utf-8"))
except Exception:
    print("")
    raise SystemExit
g = next((i for i in d.get("items", []) if not i.get("delivery_item_id")), None)
print((g.get("id", "") + " " + g.get("kind", "")) if g else "")' "${1:-config/shop.json}"
}

db_cli() {
  : "${DOTNET:?db_cli 需要先定义 DOTNET}"
  : "${CHARACTER_DLL:?db_cli 需要先定义 CHARACTER_DLL}"
  "$DOTNET" "$CHARACTER_DLL" "$@" 2>&1
}

# finish <标题>：输出汇总行，据 FAIL 定退出码。标题形如 "P1-4a（server）"。
finish() {
  local title="${1:-check}"
  echo
  echo "${title} 核对：$PASS 项通过，$FAIL 项未通过"
  [ "$FAIL" = "0" ] || exit 1
  exit 0
}
