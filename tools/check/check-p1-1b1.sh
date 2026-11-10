#!/usr/bin/env bash
# P1-1b1 核对：创建角色。
#
# 完成标准（开发架构第 14 节第 20 行）：种族只存外观和出身。职业和分支能写入。
# 主线场次、立仙或立魔为空。
#
# 做这几段：
#   1. 编译网关与角色服
#   2. 静态断言：角色表没有任何属性列；种族表没有属性字段；主线与分路两列可空；
#      角色的写入只在角色服（网关不写）；分支必须校验归属
#   3. 建两张表、写一个测试账号，起角色服与网关
#   4. 四种出身各建一个号，职业与分支组合写进去
#   5. 查库：种族存编号、外观是 JSON、主线与分路为空、职业分支与请求一致
#   6. 未知种族 / 未知职业 / 分支与职业不匹配 / 空名字 / 重名 都被拒
#   7. 不带令牌建号 401，且不碰库
#
# 用法：bash tools/check/check-p1-1b1.sh
# 连库段要在有 gamedb 的机器上跑（笔记本）；缺凭据时明确失败。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"

CHAR_PORT=${MMO_P11B1_CHAR_PORT:-62091}
GATEWAY_PORT=${MMO_P11B1_GATEWAY_PORT:-61092}
ACCOUNT="p11b1_user"
TEST_PASSWORD="p11b1-Pw-52d8"

PASS=0
FAIL=0
TMP="$(mktemp -d)"
CHARACTER_PID=""
GATEWAY_PID=""
HAVE_DB=""

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

# 测试角色与本轮用到的账号都不留在库里。
cleanup() {
  stop_pid "$GATEWAY_PID"
  stop_pid "$CHARACTER_PID"
  if [ -n "$HAVE_DB" ]; then
    for name in p11b1_chen p11b1_mai p11b1_lan p11b1_yuan p11b1_dup; do
      "$DOTNET" "$CHARACTER_DLL" --delete-character "$name" --config "$TMP/character.json" >/dev/null 2>&1 || true
    done
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" \
      --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

post_character() {
  # $1=令牌（空串表示不带） $2=JSON 体
  local token="$1" body="$2" url="http://127.0.0.1:${GATEWAY_PORT}/character"
  [ -n "$token" ] && url="${url}?token=${token}"
  curl -s -m 10 -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' -d "$body"
}

split_response() {
  BODY="${1%$'\n'*}"
  CODE="${1##*$'\n'}"
}

json_field() {
  printf '%s' "$1" | python3 -c 'import json,sys
try:
    print(json.load(sys.stdin).get("'"$2"'", ""))
except Exception:
    print("")'
}

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-1b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Gateway/Gateway.csproj" -c Release --nologo -v q >"$TMP/build-gw.log" 2>&1 \
  && "$DOTNET" build "$ROOT/src/Character/Character.csproj" -c Release --nologo -v q >"$TMP/build-char.log" 2>&1; then
  ok "网关与角色服编译通过"
else
  bad "编译失败"
  grep -E "error " "$TMP/build-gw.log" "$TMP/build-char.log" | head -5 | detail
  echo
  echo "P1-1b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
SQL="$ROOT/db/character/character.sql"
RACES="$ROOT/config/races.json"
BRANCHES="$ROOT/config/branches.json"

# 正则用 [[:space:]]，别用 \s：macOS 的 BSD grep -E 不认 \s，
# 写出来看着在查，其实永远无命中（假通过）。
if grep -qiE '^[[:space:]]*(strength|agility|spirit|vitality|attack|hp|defense|crit)[[:space:]]' "$SQL"; then
  bad "角色表里有属性列，种族会被做成数值差"
else
  ok "角色表没有任何属性列"
fi

if grep -qiE '"(strength|agility|spirit|vitality|attack|hp|defense)"' "$RACES"; then
  bad "种族表里有属性字段"
else
  ok "种族表只有名字、来源、外观与对白键"
fi

if grep -qE '^[[:space:]]*(main_stage|alignment)[[:space:]]+(integer|text)(,|$)' "$SQL" \
   && ! grep -qE '^[[:space:]]*(main_stage|alignment)[[:space:]]+(integer|text)[[:space:]]+NOT NULL' "$SQL"; then
  ok "主线场次与立仙/立魔两列可空，创建时不填"
else
  bad "主线或分路列被写成了必填"
fi

if grep -qi 'unique' "$SQL" && grep -q 'account' "$SQL"; then
  ok "同一账号下名字唯一"
else
  bad "名字唯一约束缺失"
fi

# 角色的写入只能在角色服。网关那层不许出现角色表的 insert。
if grep -rqiE 'insert into "角色"\.character' "$ROOT/src/Gateway" 2>/dev/null; then
  bad "网关直接写了角色表，角色的唯一写入被绕过"
else
  ok "网关不写角色表，只转请求"
fi

if grep -q 'insert into "角色".character' "$ROOT/src/Character/CharacterRepository.cs"; then
  ok "角色的写入点在角色服"
else
  bad "找不到角色服的写入点"
fi

if grep -q 'BelongsTo' "$ROOT/src/Character/CharacterGrpcService.cs"; then
  ok "分支要校验属于该职业"
else
  bad "分支没有校验归属"
fi

if grep -q 'GrpcChannel.ForAddress' "$ROOT/src/Gateway/CharacterApi.cs"; then
  ok "网关用 gRPC 转给角色服（服与服按技术架构第 2 节）"
else
  bad "网关没走 gRPC"
fi

echo "== 3. 环境与启动 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env"
  echo
  echo "P1-1b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
HAVE_DB=1
ok "库凭据在位：$( [ -n "${MMO_GAMEDB:-}" ] && echo "env:MMO_GAMEDB" || echo "file:/srv/mmo/gamedb.env" )"

cat > "$TMP/character.json" <<JSON
{
  "schema": "character-config/1",
  "listen": "http://127.0.0.1:${CHAR_PORT}",
  "db_pool_max": 4
}
JSON

cat > "$TMP/gateway.json" <<JSON
{
  "schema": "gateway-config/1",
  "listen": "http://127.0.0.1:${GATEWAY_PORT}",
  "token_ttl_s": 3600,
  "pbkdf2_iterations": 10000,
  "character_upstream": "http://127.0.0.1:${CHAR_PORT}"
}
JSON

# 账号表由网关那一行建；角色表由角色服建。
if ! "$DOTNET" "$GATEWAY_DLL" --migrate --config "$TMP/gateway.json" \
      --sql "$ROOT/db/character/account.sql" >"$TMP/migrate-account.log" 2>&1; then
  bad "账号表建不出来"
  tail -5 "$TMP/migrate-account.log" | detail
  echo
  echo "P1-1b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" \
      --sql "$SQL" >"$TMP/migrate-character.log" 2>&1; then
  ok "角色表已就位（$(grep -m1 '^migrate=' "$TMP/migrate-character.log")）"
else
  bad "角色表建不出来"
  tail -5 "$TMP/migrate-character.log" | detail
  echo
  echo "P1-1b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if printf '%s\n' "$TEST_PASSWORD" \
   | "$DOTNET" "$GATEWAY_DLL" --seed-account "$ACCOUNT" --config "$TMP/gateway.json" 2>&1 \
   | grep -q '^seed=ok'; then
  ok "测试账号已写入：${ACCOUNT}"
else
  bad "测试账号写入失败"
fi

: > "$TMP/character.log"
"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >>"$TMP/character.log" 2>&1 &
CHARACTER_PID=$!

CHAR_READY=0
for _ in $(seq 1 100); do
  if grep -q '^ready name=Character' "$TMP/character.log" 2>/dev/null; then
    CHAR_READY=1
    break
  fi
  if ! kill -0 "$CHARACTER_PID" 2>/dev/null; then
    break
  fi
  sleep 0.1
done

if [ "$CHAR_READY" = "1" ]; then
  ok "角色服在 127.0.0.1:${CHAR_PORT} 上（$(grep -m1 '^races=' "$TMP/character.log")）"
else
  bad "角色服没起来"
  tail -8 "$TMP/character.log" | detail
  echo
  echo "P1-1b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

: > "$TMP/gateway.log"
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >>"$TMP/gateway.log" 2>&1 &
GATEWAY_PID=$!

GATEWAY_READY=0
for _ in $(seq 1 150); do
  if grep -q '^ready name=Gateway' "$TMP/gateway.log" 2>/dev/null; then
    if curl -s -m 2 "http://127.0.0.1:${GATEWAY_PORT}/health" | grep -q '"status":"ok"'; then
      GATEWAY_READY=1
      break
    fi
  fi
  if ! kill -0 "$GATEWAY_PID" 2>/dev/null; then
    break
  fi
  sleep 0.2
done

if [ "$GATEWAY_READY" = "1" ]; then
  ok "网关在 127.0.0.1:${GATEWAY_PORT} 上，角色服上游 $(grep -m1 '^character_upstream=' "$TMP/gateway.log" | cut -d= -f2-)"
else
  bad "网关没起来"
  tail -8 "$TMP/gateway.log" | detail
  echo
  echo "P1-1b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

TOKEN="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/login" \
  -H 'Content-Type: application/json' \
  -d "{\"account\":\"${ACCOUNT}\",\"password\":\"${TEST_PASSWORD}\"}" \
  | python3 -c 'import json,sys
try:
    print(json.load(sys.stdin).get("token",""))
except Exception:
    print("")')"

if [ -n "$TOKEN" ]; then
  ok "登录拿到令牌：长度 ${#TOKEN}"
else
  bad "登录没拿到令牌"
  tail -5 "$TMP/gateway.log" | detail
  echo
  echo "P1-1b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 4. 四种出身都建得出来 =="
create_ok() {
  # $1=名字 $2=种族 $3=职业 $4=分支 $5=期望种族回显 $6=期望分支回显
  local name="$1" race="$2" cls="$3" branch="$4" want_race="$5" want_branch="$6"
  local payload
  if [ -n "$branch" ]; then
    payload="{\"name\":\"${name}\",\"race\":\"${race}\",\"class_id\":\"${cls}\",\"branch\":\"${branch}\",\"appearance\":\"{\\\"tone\\\":\\\"pale\\\"}\"}"
  else
    payload="{\"name\":\"${name}\",\"race\":\"${race}\",\"class_id\":\"${cls}\",\"appearance\":\"{\\\"tone\\\":\\\"wind\\\"}\"}"
  fi

  local resp
  resp="$(post_character "$TOKEN" "$payload")"
  split_response "$resp"

  if [ "$CODE" != "200" ]; then
    bad "${name} 没建成（HTTP ${CODE}）：$(printf '%s' "$BODY" | head -c 100)"
    return
  fi

  local got_race got_class got_branch main_empty align_empty cid
  got_race="$(json_field "$BODY" race)"
  got_class="$(json_field "$BODY" class)"
  got_branch="$(json_field "$BODY" branch)"
  main_empty="$(json_field "$BODY" main_stage_empty)"
  align_empty="$(json_field "$BODY" alignment_empty)"
  cid="$(json_field "$BODY" character_id)"

  if [ "$got_race" = "$want_race" ] && [ "$got_class" = "$cls" ] && [ "$got_branch" = "$want_branch" ] \
     && [ "$main_empty" = "True" ] && [ "$align_empty" = "True" ] && [ -n "$cid" ]; then
    ok "${name}：种族 ${got_race}，职业 ${got_class}，分支 ${got_branch:-空}，主线与分路都空"
  else
    bad "${name} 回显不对：race=${got_race} class=${got_class} branch=${got_branch} main_empty=${main_empty} align_empty=${align_empty}"
  fi
}

create_ok p11b1_chen chen warrior pojun chen pojun
create_ok p11b1_mai mai mage "" mai ""
create_ok p11b1_lan lan archer chuanyang lan chuanyang
create_ok p11b1_yuan yuan priest shengxi yuan shengxi

echo "== 5. 查库：落库的就是这些 =="
report_of() {
  "$DOTNET" "$CHARACTER_DLL" --character-report "$1" --config "$TMP/character.json" 2>&1 \
    | grep -m1 '^report='
}

R_CHEN="$(report_of p11b1_chen)"
R_MAI="$(report_of p11b1_mai)"
R_LAN="$(report_of p11b1_lan)"
R_YUAN="$(report_of p11b1_yuan)"

printf '  %s\n' "$R_CHEN"

if printf '%s' "$R_CHEN" | grep -q 'race=chen' && printf '%s' "$R_MAI" | grep -q 'race=mai' \
   && printf '%s' "$R_LAN" | grep -q 'race=lan' && printf '%s' "$R_YUAN" | grep -q 'race=yuan'; then
  ok "种族按编号落库：chen / mai / lan / yuan"
else
  bad "种族落库不对"
  printf '%s\n%s\n%s\n%s\n' "$R_CHEN" "$R_MAI" "$R_LAN" "$R_YUAN" | detail
fi

if printf '%s' "$R_CHEN" | grep -q 'class=warrior' && printf '%s' "$R_CHEN" | grep -q 'branch=pojun'; then
  ok "职业与分支写进去了：warrior / pojun"
else
  bad "职业或分支没写对：${R_CHEN}"
fi

if printf '%s' "$R_MAI" | grep -q 'branch= '; then
  ok "不传分支时分支列为空"
else
  bad "不传分支却写了值：${R_MAI}"
fi

if printf '%s' "$R_CHEN" | grep -q 'main_stage_null=True' && printf '%s' "$R_CHEN" | grep -q 'alignment_null=True'; then
  ok "主线场次与立仙/立魔都是 NULL"
else
  bad "主线或分路不是空的：${R_CHEN}"
fi

if printf '%s' "$R_CHEN" | grep -qE 'appearance_len=[1-9]'; then
  ok "外观按 JSON 存下来了"
else
  bad "外观没落库：${R_CHEN}"
fi

if printf '%s' "$R_CHEN" | grep -q 'level=1' && printf '%s' "$R_CHEN" | grep -q 'realm=炼体'; then
  ok "新角色从 1 级炼体开始"
else
  bad "初始等级或境界不对：${R_CHEN}"
fi

echo "== 6. 该拒的都拒了 =="
BEFORE="$("$DOTNET" "$CHARACTER_DLL" --character-stats --config "$TMP/character.json" 2>&1 | grep -m1 '^characters=')"

reject_case() {
  # $1=说明 $2=期望 HTTP $3=JSON 体
  local label="$1" want="$2" body="$3" resp
  resp="$(post_character "$TOKEN" "$body")"
  split_response "$resp"
  if [ "$CODE" = "$want" ]; then
    ok "${label}：HTTP ${CODE}（$(json_field "$BODY" error)）"
  else
    bad "${label}：期望 ${want}，实际 ${CODE}"
  fi
}

reject_case "未知种族" 400 '{"name":"p11b1_x1","race":"dragon","class_id":"warrior"}'
reject_case "未知职业" 400 '{"name":"p11b1_x2","race":"chen","class_id":"bard"}'
reject_case "分支与职业不匹配" 400 '{"name":"p11b1_x3","race":"chen","class_id":"warrior","branch":"shengxi"}'
reject_case "空名字" 400 '{"name":"","race":"chen","class_id":"warrior"}'
reject_case "重名" 409 '{"name":"p11b1_chen","race":"chen","class_id":"warrior"}'

# 不带令牌：401，而且库里的角色数不能变。
RESP="$(post_character "" '{"name":"p11b1_nokey","race":"chen","class_id":"warrior"}')"
split_response "$RESP"
if [ "$CODE" = "401" ]; then
  ok "不带令牌建号：HTTP 401"
else
  bad "不带令牌也能建号：HTTP ${CODE}"
fi

AFTER="$("$DOTNET" "$CHARACTER_DLL" --character-stats --config "$TMP/character.json" 2>&1 | grep -m1 '^characters=')"
if [ "$BEFORE" = "$AFTER" ]; then
  ok "被拒的这几次没有多出角色：${AFTER}"
else
  bad "被拒的请求也写了库：之前 ${BEFORE}，之后 ${AFTER}"
fi

if grep -q "$TEST_PASSWORD" "$TMP/gateway.log" 2>/dev/null; then
  bad "网关日志里出现了口令"
else
  ok "网关日志里没有口令"
fi

echo
echo "P1-1b1 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
