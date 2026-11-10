#!/usr/bin/env bash
# P1-1b5 核对：同一事务全成或全退，发奖事务号不可复用。
#
# 完成标准（开发架构第 14 节第 24 行）：
#   两笔会扣成负数的操作只有一笔成功。同一个事务号再提交，不发第二次。
#
# 做这几段：
#   1. 编译网关与角色服
#   2. 静态断言：事务号是主键；发奖在数据库事务里；抢号用 on conflict do nothing；
#      失败往外抛让事务回滚；扣款带余额条件
#   3. 建五张表、两个账号，起角色服与网关
#   4. 正常一笔：扣金币 + 加物品一起成功
#   5. 同一个事务号再提交：duplicate，金币与物品都不变，事务号表里仍一行
#   6. 一笔扣款失败：整笔回退，同笔里的物品也没进
#   7. 并发两笔各扣 2000（余额 2700）：只有一笔成功，余额不会变负
#   8. 别人的角色 404、空事务号 400、无令牌 401
#
# 用法：bash tools/check-p1-1b5.sh
# 连库段要在有 gamedb 的机器上跑（笔记本）；缺凭据时明确失败。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"

CHAR_PORT=${MMO_P11B5_CHAR_PORT:-62099}
GATEWAY_PORT=${MMO_P11B5_GATEWAY_PORT:-61100}
ACCOUNT="p11b5_user"
OTHER="p11b5_other"
TEST_PASSWORD="p11b5-Pw-61a9"
CHAR_NAME="p11b5_hero"

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

cleanup() {
  stop_pid "$GATEWAY_PID"
  stop_pid "$CHARACTER_PID"
  if [ -n "$HAVE_DB" ]; then
    "$DOTNET" "$CHARACTER_DLL" --delete-character "$CHAR_NAME" --config "$TMP/character.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$OTHER" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

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

split_response() {
  BODY="${1%$'\n'*}"
  CODE="${1##*$'\n'}"
}

json_field() {
  printf '%s' "$1" | python3 -c 'import json,sys
try:
    v = json.load(sys.stdin).get("'"$2"'", "")
    print(v)
except Exception:
    print("")'
}

grant() {
  # $1=令牌（空串不带） $2=角色 $3=事务号 $4=entries JSON 数组
  local token="$1" url="http://127.0.0.1:${GATEWAY_PORT}/grant"
  [ -n "$token" ] && url="${url}?token=${token}"
  curl -s -m 15 -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2},\"txn_id\":\"${3}\",\"entries\":${4}}"
}

balance_of() {
  curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/currency?token=$1&character_id=$2" \
    | python3 -c 'import json,sys
try:
    data = json.load(sys.stdin)
except Exception:
    print(""); raise SystemExit
for b in data.get("balances", []):
    if b.get("kind") == "'"$3"'":
        print(b.get("amount"))
        raise SystemExit
print("")'
}

slots_count() {
  curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/inventory?token=$1&character_id=$2" \
    | python3 -c 'import json,sys
try:
    print(len(json.load(sys.stdin).get("slots", [])))
except Exception:
    print("-1")'
}

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-1b5 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Gateway/Gateway.csproj" -c Release --nologo -v q >"$TMP/build-gw.log" 2>&1 \
  && "$DOTNET" build "$ROOT/src/Character/Character.csproj" -c Release --nologo -v q >"$TMP/build-char.log" 2>&1; then
  ok "网关与角色服编译通过"
else
  bad "编译失败"
  grep -E "error " "$TMP/build-gw.log" "$TMP/build-char.log" | head -5 | detail
  echo
  echo "P1-1b5 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
SQL="$ROOT/db/character/transaction.sql"
REPO="$ROOT/src/Character/GrantRepository.cs"
SVC="$ROOT/src/Character/CharacterGrpcService.cs"

if grep -q 'txn_id' "$SQL" && grep -qi 'primary key' "$SQL"; then
  ok "事务号是主键，同一个号只可能落一行"
else
  bad "事务号不是主键"
fi

if grep -q 'BeginTransactionAsync' "$REPO" && grep -q 'CommitAsync' "$REPO"; then
  ok "整笔在一个数据库事务里，提交才生效"
else
  bad "发奖没有走数据库事务"
fi

if grep -q 'on conflict (txn_id) do nothing' "$REPO" && grep -q 'duplicate' "$REPO"; then
  ok "抢号用 on conflict do nothing，撞号就整笔不做"
else
  bad "没有幂等挡板"
fi

if grep -q 'throw new GrantException' "$REPO"; then
  ok "业务不满足时抛异常，事务随之回滚（全成或全退）"
else
  bad "失败时不回滚"
fi

if grep -qF 'and amount + $3 >= 0' "$REPO"; then
  ok "事务里的扣款也带余额条件，并发只有一笔能成"
else
  bad "事务里的扣款没有余额条件"
fi

echo "== 3. 环境与启动 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env"
  echo
  echo "P1-1b5 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
HAVE_DB=1
ok "库凭据在位：$( [ -n "${MMO_GAMEDB:-}" ] && echo "env:MMO_GAMEDB" || echo "file:/srv/mmo/gamedb.env" )"

cat > "$TMP/character.json" <<JSON
{
  "schema": "character-config/1",
  "listen": "http://127.0.0.1:${CHAR_PORT}",
  "db_pool_max": 6,
  "bag_slots": 40
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

if ! "$DOTNET" "$GATEWAY_DLL" --migrate --config "$TMP/gateway.json" \
      --sql "$ROOT/db/character/account.sql" >"$TMP/migrate-account.log" 2>&1; then
  bad "账号表建不出来"
  tail -5 "$TMP/migrate-account.log" | detail
  echo
  echo "P1-1b5 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

for sql in character.sql inventory.sql currency.sql transaction.sql; do
  "$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" \
    --sql "$ROOT/db/character/$sql" >/dev/null 2>&1 || true
done

# 五张表逐张确认存在：迁移是幂等的，建不出来就说明前面哪一步错了。
MIGRATE_OK=1
for sql in character.sql inventory.sql currency.sql transaction.sql; do
  if ! "$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" \
        --sql "$ROOT/db/character/$sql" >"$TMP/migrate-$sql.log" 2>&1; then
    MIGRATE_OK=0
    tail -3 "$TMP/migrate-$sql.log" | detail
  fi
done
if [ "$MIGRATE_OK" = "1" ]; then
  ok "账号、角色、背包、货币、发奖事务五张表都在位"
else
  bad "有表建不出来"
  echo
  echo "P1-1b5 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

for acct in "$ACCOUNT" "$OTHER"; do
  printf '%s\n' "$TEST_PASSWORD" \
    | "$DOTNET" "$GATEWAY_DLL" --seed-account "$acct" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
done
ok "两个测试账号已写入"

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
  ok "角色服在 127.0.0.1:${CHAR_PORT} 上"
else
  bad "角色服没起来"
  tail -8 "$TMP/character.log" | detail
  echo
  echo "P1-1b5 核对：$PASS 项通过，$FAIL 项未通过"
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
  ok "网关在 127.0.0.1:${GATEWAY_PORT} 上"
else
  bad "网关没起来"
  tail -8 "$TMP/gateway.log" | detail
  echo
  echo "P1-1b5 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then
  ok "两个账号都能登录"
else
  bad "登录没拿到令牌"
  echo
  echo "P1-1b5 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

# 上一次核对被中断时可能留下同名角色，先清掉，保证这次从干净状态起。
"$DOTNET" "$CHARACTER_DLL" --delete-character "$CHAR_NAME" --config "$TMP/character.json" >/dev/null 2>&1 || true

CREATE="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/character?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"${CHAR_NAME}\",\"race\":\"chen\",\"class_id\":\"tank\"}")"
split_response "$CREATE"
CHAR_ID="$(json_field "$BODY" character_id)"
if [ "$CODE" = "200" ] && [ -n "$CHAR_ID" ]; then
  ok "测试角色建好：id=${CHAR_ID}"
else
  bad "测试角色没建出来（HTTP ${CODE}）"
  echo
  echo "P1-1b5 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

# 先给 2700 金币（走 /currency/add，P1-1b4 的口子）。
curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/currency/add?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${CHAR_ID},\"kind\":\"gold\",\"delta\":2700}" >/dev/null
GOLD="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
if [ "$GOLD" = "2700" ]; then
  ok "起始金币 2700"
else
  bad "起始金币不对：${GOLD}"
fi

echo "== 4. 正常一笔：扣款与发物一起成功 =="
TXN1="p11b5-txn-1"
RESP="$(grant "$TOKEN" "$CHAR_ID" "$TXN1" '[{"currency":{"kind":"gold","delta":-300}},{"item":{"slot":1,"item_id":"ore_iron","count":5}}]')"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" duplicate)" = "False" ]; then
  ok "发奖成功：扣 300 金币 + 铁矿 5 个进 1 号格"
else
  bad "发奖失败（HTTP ${CODE}）：$(printf '%s' "$BODY" | head -c 120)"
fi

GOLD="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
SLOTS="$(slots_count "$TOKEN" "$CHAR_ID")"
if [ "$GOLD" = "2400" ] && [ "$SLOTS" = "1" ]; then
  ok "库里对得上：金币 2400，背包 1 格"
else
  bad "库里不对：金币 ${GOLD}，背包 ${SLOTS} 格"
fi

echo "== 5. 同一个事务号再提交 =="
RESP="$(grant "$TOKEN" "$CHAR_ID" "$TXN1" '[{"currency":{"kind":"gold","delta":-300}},{"item":{"slot":1,"item_id":"ore_iron","count":5}}]')"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" duplicate)" = "True" ]; then
  ok "重复提交：duplicate=true，不发第二次"
else
  bad "重复提交没被挡住（HTTP ${CODE}）：$(printf '%s' "$BODY" | head -c 120)"
fi

GOLD="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
ORE="$(curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/inventory?token=${TOKEN}&character_id=${CHAR_ID}" \
  | python3 -c 'import json,sys
try:
    data = json.load(sys.stdin)
except Exception:
    print("-1"); raise SystemExit
print(sum(s.get("count", 0) for s in data.get("slots", []) if s.get("item_id") == "ore_iron"))')"
if [ "$GOLD" = "2400" ] && [ "$ORE" = "5" ]; then
  ok "重复提交后金币仍 2400、铁矿仍 5 个"
else
  bad "重复提交改了数据：金币 ${GOLD}，铁矿 ${ORE}"
fi

TXN_ROWS="$("$DOTNET" "$CHARACTER_DLL" --txn-count "$TXN1" --config "$TMP/character.json" 2>&1 | grep -m1 '^txn_count=')"
if [ "$TXN_ROWS" = "txn_count=1 txn=$TXN1" ]; then
  ok "事务号表里这个号只有一行"
else
  bad "事务号表里的行数不对：${TXN_ROWS}"
fi

echo "== 6. 一笔里扣款失败：整笔回退 =="
GOLD_BEFORE="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
SLOTS_BEFORE="$(slots_count "$TOKEN" "$CHAR_ID")"
TXN2="p11b5-txn-2"
RESP="$(grant "$TOKEN" "$CHAR_ID" "$TXN2" '[{"currency":{"kind":"gold","delta":-999999}},{"item":{"slot":2,"item_id":"herb_ling","count":3}}]')"
split_response "$RESP"
ERR="$(json_field "$BODY" error)"
if [ "$CODE" = "412" ] && [ "$ERR" = "insufficient" ]; then
  ok "扣 999999 被拒：412 insufficient"
else
  bad "扣款失败没被拒：HTTP ${CODE} ${ERR}"
fi

GOLD_AFTER="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
SLOTS_AFTER="$(slots_count "$TOKEN" "$CHAR_ID")"
if [ "$GOLD_AFTER" = "$GOLD_BEFORE" ] && [ "$SLOTS_AFTER" = "$SLOTS_BEFORE" ]; then
  ok "整笔回退：金币仍 ${GOLD_AFTER}，背包仍 ${SLOTS_AFTER} 格（同笔的灵草没进）"
else
  bad "没回退干净：金币 ${GOLD_BEFORE}→${GOLD_AFTER}，背包 ${SLOTS_BEFORE}→${SLOTS_AFTER}"
fi

# 失败的那笔不能把事务号占住：换个号还能发，同号再发也应该能成（因为上一笔整笔退了）。
RESP="$(grant "$TOKEN" "$CHAR_ID" "$TXN2" '[{"currency":{"kind":"gold","delta":-100}}]')"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" duplicate)" = "False" ]; then
  ok "失败那笔没占号：同一个号换成能成的变更可以发"
else
  bad "失败那笔把号占住了：HTTP ${CODE} $(printf '%s' "$BODY" | head -c 100)"
fi

echo "== 7. 并发两笔各扣 2000 =="
# 先把金币补回到 2700，方便判断：两笔各扣 2000，只有一笔能成，剩 700。
NOW="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
ADD=$(( 2700 - NOW ))
if [ "$ADD" -ne 0 ]; then
  curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/currency/add?token=${TOKEN}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${CHAR_ID},\"kind\":\"gold\",\"delta\":${ADD}}" >/dev/null
fi
GOLD="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
printf '       并发前金币 %s\n' "$GOLD"

( grant "$TOKEN" "$CHAR_ID" "p11b5-race-a" '[{"currency":{"kind":"gold","delta":-2000}}]' > "$TMP/race-a.txt" 2>&1 ) &
RACE_A=$!
( grant "$TOKEN" "$CHAR_ID" "p11b5-race-b" '[{"currency":{"kind":"gold","delta":-2000}}]' > "$TMP/race-b.txt" 2>&1 ) &
RACE_B=$!
# 只等这两笔，不要裸 wait：角色服和网关也是本 shell 的后台作业，
# 裸 wait 会一直等它们退出（它们要到脚本收尾才停），在 Windows 上表现为整个核对卡死。
wait "$RACE_A" "$RACE_B"

CODE_A="$(tail -1 "$TMP/race-a.txt")"
CODE_B="$(tail -1 "$TMP/race-b.txt")"
printf '       并发结果：%s / %s\n' "$CODE_A" "$CODE_B"

if { [ "$CODE_A" = "200" ] && [ "$CODE_B" = "412" ]; } \
   || { [ "$CODE_A" = "412" ] && [ "$CODE_B" = "200" ]; }; then
  ok "并发两笔扣 2000：只有一笔成功，另一笔 insufficient"
else
  bad "并发结果不是「一成一败」：${CODE_A} / ${CODE_B}"
  head -3 "$TMP/race-a.txt" | detail
  head -3 "$TMP/race-b.txt" | detail
fi

GOLD_AFTER="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
if [ "$GOLD_AFTER" = "700" ]; then
  ok "并发之后金币 2700 - 2000 = 700，没有扣成负数"
else
  bad "并发之后金币是 ${GOLD_AFTER}，应为 700"
fi

echo "== 8. 该拒的都拒了 =="
RESP="$(grant "$OTHER_TOKEN" "$CHAR_ID" "p11b5-txn-other" '[{"currency":{"kind":"gold","delta":100}}]')"
split_response "$RESP"
if [ "$CODE" = "404" ] && [ "$(json_field "$BODY" error)" = "character_not_found" ]; then
  ok "别人的角色：404 character_not_found"
else
  bad "别人的角色也能发奖：HTTP ${CODE}"
fi

RESP="$(grant "$TOKEN" "$CHAR_ID" "" '[{"currency":{"kind":"gold","delta":100}}]')"
split_response "$RESP"
if [ "$CODE" = "400" ] && [ "$(json_field "$BODY" error)" = "txn_id_invalid" ]; then
  ok "空事务号：400 txn_id_invalid"
else
  bad "空事务号没被拒：HTTP ${CODE} $(json_field "$BODY" error)"
fi

RESP="$(grant "" "$CHAR_ID" "p11b5-txn-nokey" '[{"currency":{"kind":"gold","delta":100}}]')"
split_response "$RESP"
if [ "$CODE" = "401" ]; then
  ok "不带令牌：HTTP 401"
else
  bad "不带令牌也能发奖：HTTP ${CODE}"
fi

GOLD_FINAL="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
if [ "$GOLD_FINAL" = "700" ]; then
  ok "被拒的这几次没有改动金币：仍是 700"
else
  bad "被拒的请求改动了金币：${GOLD_FINAL}"
fi

if grep -q "$TEST_PASSWORD" "$TMP/gateway.log" 2>/dev/null; then
  bad "网关日志里出现了口令"
else
  ok "网关日志里没有口令"
fi

echo
echo "P1-1b5 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
