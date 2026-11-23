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
#   split_response <resp>                   拆成全局 BODY / CODE
#   login_token <account>                   走网关换令牌（须先定义 GATEWAY_PORT / TEST_PASSWORD）
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

# split_response <curl 响应>：拆成全局 BODY / CODE。
#   curl -w '\n%{http_code}' 的输出从右边拆；调用方直接读 $BODY / $CODE。
split_response() {
  BODY="${1%$'\n'*}"
  CODE="${1##*$'\n'}"
}

# login_token <账号>：走网关换令牌，打印 token（失败打印空串，不判红）。
#   调用方须先定义 GATEWAY_PORT 与 TEST_PASSWORD。
login_token() {
  curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/login" \
    -H 'Content-Type: application/json' \
    -d "{\"account\":\"$1\",\"password\":\"${TEST_PASSWORD}\"}" \
    | python3 -c 'import json,sys
try:
    print(json.load(sys.stdin).get("token",""))
except Exception:
    print("")'
}

# create_char <token> <name>：走网关建角色，编号放进全局 NEW_ID。
#   成功（HTTP 200 且拿到 character_id）返回 0；失败返回 1。
#   调用方须先定义 GATEWAY_PORT。
create_char() {
  local resp
  resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"$2\",\"race\":\"mai\",\"class_id\":\"warrior\"}")"
  [ "$(code_of "$resp")" = "200" ] || return 1
  NEW_ID="$(json_field "$(body_of "$resp")" character_id)"
  [ -n "$NEW_ID" ]
}

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
#       打印「id category」，挑第一条 delivery.mode 为 none 的商品；没有则打印空。
#       不要写死某个编号：后面的卡会陆续接上发货。K1 起发货方式写在 delivery.mode 上。
#   db_cli <args...>
#       跑角色服只读入口；调用方须先定义 DOTNET 与 CHARACTER_DLL。
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# split_parts <源码路径> / src_agg <源码路径> —— 拆分卡（14.8 D2a–d 起）之后，
#   一个类型可能由「主文件 + 分项目录里的 partial 分片」组成。核对脚本仍按老路径
#   传主文件：split_parts 展开成一份文件清单，src_agg 把它们拼成一份临时文本并
#   打印该路径。断言文本一个字不改，改的只是「怎么读」。
#   - 主文件没有同名分项目录时，原样返回，行为与拆分前一致；
#   - 临时文件写在 $TMP（脚本一般已定义）或 $TMPDIR，不进仓库、不进 git。
#   - shell 段：SVC="$(src_agg src/Character/CharacterGrpcService.cs)"
#   - python 段：export 过的 AGG_* 变量指向同一份聚合文本。
# ---------------------------------------------------------------------------
split_parts() {
  local path="$1" dir=""
  case "$path" in
    */CharacterGrpcService.cs) dir="${path%/CharacterGrpcService.cs}/Grpc" ;;
    */RealmTable.cs)           dir="${path%/RealmTable.cs}/Realm" ;;
    */CharacterProcess.cs)     dir="${path%/CharacterProcess.cs}/Startup" ;;
    */Battle/Config.cs)        dir="${path%/Config.cs}/ConfigTables" ;;
    */Battle/Encounter.cs)     dir="${path%/Encounter.cs}/Encounter" ;;
    */Battle/Effects.cs)       dir="${path%/Effects.cs}/Effects" ;;
    */Battle/Server.cs)        dir="${path%/Server.cs}/Server" ;;
    */Gateway/CharacterApi.cs) dir="${path%/CharacterApi.cs}/Api/Character" ;;
    */Gateway/SectApi.cs)      dir="${path%/SectApi.cs}/Api/Sect" ;;
    */Gateway/WorldApi.cs)     dir="${path%/WorldApi.cs}/Api/World" ;;
    */Social/SectRepository.cs)  dir="${path%/SectRepository.cs}/Sect" ;;
    */Social/SocialGrpcService.cs) dir="${path%/SocialGrpcService.cs}/Grpc" ;;
    */World/WorldGrpcService.cs) dir="${path%/WorldGrpcService.cs}/Grpc" ;;
    */Social/Program.cs)       dir="${path%/Program.cs}/Cli" ;;
    */World/Program.cs)        dir="${path%/Program.cs}/Cli" ;;
    # D13 客户端：Ui 三个大文件 + 战斗 HUD / 战斗会话 + 表世界拆装层。
    # 同一张表还有两份：tools/check/lib_read.py 的 PARTS（python 段）与
    # client/MMOGame/Assets/Editor/SplitRead.cs（Unity 编辑器自检）。改一处要同步改三处。
    */HotUpdate/Ui/UiShell.cs)      dir="${path%/UiShell.cs}/Shell" ;;
    */HotUpdate/Ui/UiSocialFlow.cs) dir="${path%/UiSocialFlow.cs}/Flow" ;;
    */HotUpdate/Ui/UiWorldFlow.cs)  dir="${path%/UiWorldFlow.cs}/World" ;;
    */HotUpdate/BattleHud.cs)       dir="${path%/BattleHud.cs}/Hud" ;;
    */HotUpdate/BattleClient.cs)    dir="${path%/BattleClient.cs}/Session" ;;
    */Net/WorldEntry.cs)            dir="${path%/WorldEntry.cs}/WorldEntry" ;;
    *) printf '%s\n' "$path"; return ;;
  esac
  printf '%s\n' "$path"
  local f
  for f in "$dir"/*.cs; do [ -f "$f" ] && printf '%s\n' "$f"; done
}

src_agg() {
  local path="$1" parts out f n
  [ -f "$path" ] || path="${ROOT:-.}/$path"
  parts="$(split_parts "$path")"
  n="$(printf '%s\n' "$parts" | wc -l | tr -d ' ')"
  if [ "$n" -le 1 ]; then printf '%s' "$path"; return; fi
  out="${TMP:-${TMPDIR:-/tmp}}/agg-$(basename "$path")"
  : > "$out"
  for f in $parts; do cat "$f" >> "$out"; printf '\n' >> "$out"; done
  printf '%s' "$out"
}

assert_file_contains() {
  local file="$1" pattern="$2" msg="$3"
  file="$(src_agg "$file")"
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

# assert_interface <file> <regex> <msg>
#   钉「接口 / 字段 / 入口存在」，不钉「已接 / 未接」（D5 口径）。
#   文件不存在或正则不命中都算失败；比 `git diff --quiet` 稳：后一张卡
#   改别的配置 / proto 不会把这张卡打红。
assert_interface() {
  local file="$1" pattern="$2" msg="$3"
  file="$(src_agg "$file")"
  if [ -f "$file" ] && grep -qE -- "$pattern" "$file"; then
    ok "$msg"
  else
    bad "${msg}（找不到 ${pattern}）"
  fi
}

# assert_floor <actual> <min> <msg>
#   会随卡增长的集合只写下限（D5 口径）：新卡追加条目，老卡不用改。
assert_floor() {
  local actual="$1" min="$2" msg="$3"
  case "$actual" in
    ''|*[!0-9]*) bad "${msg}（实际值不是数字：${actual}）"; return ;;
  esac
  if [ "$actual" -ge "$min" ]; then
    ok "${msg}（${actual} ≥ ${min}）"
  else
    bad "${msg}（${actual} < ${min}）"
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
g = next((i for i in d.get("items", [])
          if (i.get("delivery") or {}).get("mode", "none") == "none"), None)
print((g.get("id", "") + " " + g.get("category", "")) if g else "")' "${1:-config/shop.json}"
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
