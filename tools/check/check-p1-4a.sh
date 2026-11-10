#!/usr/bin/env bash
# P1-4a 核对：强化三档和失败掉 1 级。
#
# 完成标准（开发架构第 14 节第 35 行 + 第 11 节 P1-4 + 第 13 节 P1-4a + 方案 9.2）：
#   低级石点 +7 被拒绝；失败后强化等级只少 1；最高 +12，不碎、不清零、没有失败累计必成；
#   三档材料不可跨级代替；概率与消耗只在 config/enhance.json；请求带不进等级与成功率；
#   扣材料、扣金币、改等级在角色服的一笔事务里。
#
# 两种模式：
#   bash tools/check/check-p1-4a.sh                          服务端段：编译、静态、连库连服（要有 gamedb）
#   MMO_P14A_MODE=client bash tools/check/check-p1-4a.sh    客户端段：静态 + Unity 编辑器自检（要有 Unity）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
MODE="${MMO_P14A_MODE:-server}"

CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"

CHAR_PORT=${MMO_P14A_CHAR_PORT:-62112}
GATEWAY_PORT=${MMO_P14A_GATEWAY_PORT:-61113}
WORLD_PORT=${MMO_P14A_WORLD_PORT:-63097}
ACCOUNT="p14a_user"
OTHER="p14a_other"
TEST_PASSWORD="p14a-Pw-7c19"
CHAR_NAMES=("p14a_a" "p14a_other")

PASS=0
FAIL=0
TMP="$(mktemp -d)"
CHARACTER_PID=""
GATEWAY_PID=""
CHARACTER_LOG=""
HAVE_DB=""
TOKEN=""
OTHER_TOKEN=""
NEW_ID=""
ID=""
OTHER_ID=""

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
    for name in "${CHAR_NAMES[@]}"; do
      "$DOTNET" "$CHARACTER_DLL" --delete-character "$name" --config "$TMP/character.json" >/dev/null 2>&1 || true
    done
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$OTHER" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

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

login_token() {
  curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/login" \
    -H 'Content-Type: application/json' \
    -d "{\"account\":\"$1\",\"password\":\"${TEST_PASSWORD}\"}" \
    | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("token",""))
except Exception: print("")'
}

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

enhance() {
  local token="$1" char="$2" stone="$3" extra="${4:-}"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character/enhance?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"slot\":1,\"stone_id\":\"${stone}\"${extra}}"
}

add_item() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/inventory/add?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$2,\"slot\":$3,\"item_id\":\"$4\",\"count\":$5}"
}

add_gold() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/currency/add?token=${TOKEN}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${ID},\"kind\":\"gold\",\"delta\":$1}"
}

# 不看回包，直接从库里读回：等级 金币 低级石 中级石 高级石
enhance_state() {
  "$DOTNET" "$CHARACTER_DLL" --enhance-level "$ID" 1 --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^enhance_state=ok .* level=\([0-9-]*\) gold=\([0-9-]*\) low=\([0-9-]*\) mid=\([0-9-]*\) high=\([0-9-]*\)/\1 \2 \3 \4 \5/p'
}

expect_state() {
  local want="$1 $2 $3 $4 $5" label="$6" got
  got="$(enhance_state)"
  if [ "$got" = "$want" ]; then ok "$label"; else bad "$label：state=[$got] 期望=[$want]"; fi
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

finish() {
  echo
  echo "P1-4a（${MODE}）核对：$PASS 项通过，$FAIL 项未通过"
  [ "$FAIL" = "0" ] || exit 1
  exit 0
}

# ---------------------------------------------------------------- 客户端段
if [ "$MODE" = "client" ]; then
  echo "== C1. 配置、proto 与客户端静态断言 =="
  if ROOT="$ROOT" python3 - >"$TMP/client-static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/enhance.json', encoding='utf-8'))
items = {i['id'] for i in json.load(open(root + '/config/items.json', encoding='utf-8'))['items']}
assert '数值待定' in cfg['note'], '概率与消耗要注明待定'
assert cfg['max_level'] == 12, cfg['max_level']
assert [t['id'] for t in cfg['tiers']] == ['low', 'mid', 'high']
assert [(t['from_level'], t['to_level']) for t in cfg['tiers']] == [(1, 6), (7, 9), (10, 12)]
assert len({t['stone_id'] for t in cfg['tiers']}) == 3
for t in cfg['tiers']:
    assert t['stone_id'] in items
    assert 0.0 <= t['success_rate'] <= 1.0
    assert t['gold_cost'] > 0 and t['stone_cost'] > 0
proto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
req = re.search(r'message EnhanceRequest \{(.*?)\}', proto, re.S).group(1)
fields = set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool)\s+(\w+)\s*=', req))
assert fields == {'account', 'character_id', 'slot', 'stone_id', 'txn_id'}, fields
assert re.search(r'rpc Enhance\(EnhanceRequest\) returns \(EnhanceResponse\)', proto)
client = open(root + '/client/MMOGame/Assets/Net/CharacterEnhance.cs', encoding='utf-8').read()
assert 'RequestBody(ulong characterId, uint slot, string stoneId)' in client
assert 'EnhanceTable' not in client and 'JsonUtility' not in client and 'Random' not in client
assert 'success_rate' not in client
repo = open(root + '/src/Character/EnhanceRepository.cs', encoding='utf-8').read()
assert 'BeginTransactionAsync' in repo and 'Math.Max(0, levelBefore - 1)' in repo
assert 'wrong_stone' in repo and 'at_cap' in repo
print('client_static=ok tiers=3 max_level=12')
PY
  then ok "强化三档只在配置、请求带不进数值、失败掉 1 级只在角色服"
  else bad "客户端静态断言失败"; cat "$TMP/client-static.out" | detail
  fi

  echo "== C2. Unity 编辑器自检：请求与回包拆装 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    finish
  fi
  if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod P14aCheck.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity P14aCheck.Run 退出码 0"
  else
    bad "Unity P14aCheck.Run 失败"
    tail -25 "$TMP/unity.log" | detail
  fi
  for key in config proto wire boundary; do
    if grep -q "p14a_${key}=ok" "$TMP/unity.log"; then
      ok "编辑器自检 p14a_${key}=ok"
    else
      bad "编辑器自检 p14a_${key} 没过"
      grep "p14a_${key}" "$TMP/unity.log" | tail -2 | detail
    fi
  done
  finish
fi

# ---------------------------------------------------------------- 服务端段
echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then bad "找不到 dotnet"; finish; fi
BUILD_OK=1
for proj in src/Shared/Shared.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj src/World/World.csproj src/Battle/Battle.csproj; do
  if ! "$DOTNET" build "$ROOT/$proj" -c Release --nologo -v q >"$TMP/build-$(basename "$proj" .csproj).log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E "error " "$TMP/build-$(basename "$proj" .csproj).log" | head -4 | detail
  fi
done
if [ "$BUILD_OK" = "1" ]; then ok "五个服务端工程编译通过"; else finish; fi

echo "== 2. 静态断言（服务端） =="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/enhance.json', encoding='utf-8'))
items = {i['id'] for i in json.load(open(root + '/config/items.json', encoding='utf-8'))['items']}
currencies = {c['id'] for c in json.load(open(root + '/config/currency.json', encoding='utf-8'))['currencies']}
assert cfg['max_level'] == 12
assert cfg['gold_kind'] in currencies
assert '数值待定' in cfg['note']
assert [t['id'] for t in cfg['tiers']] == ['low', 'mid', 'high']
assert [(t['from_level'], t['to_level']) for t in cfg['tiers']] == [(1, 6), (7, 9), (10, 12)]
assert len({t['stone_id'] for t in cfg['tiers']}) == 3
assert all(t['stone_id'] in items for t in cfg['tiers'])
proto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
req = re.search(r'message EnhanceRequest \{(.*?)\}', proto, re.S).group(1)
resp = re.search(r'message EnhanceResponse \{(.*?)\}', proto, re.S).group(1)
fields = set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool)\s+(\w+)\s*=', req))
assert fields == {'account', 'character_id', 'slot', 'stone_id', 'txn_id'}, fields
for name in ('ok', 'reason', 'level', 'max_level', 'success', 'duplicate'):
    assert re.search(r'\b' + name + r'\s*=', resp), name
sql = open(root + '/db/character/enhance.sql', encoding='utf-8').read()
assert 'CHECK (level BETWEEN 0 AND 12)' in sql
assert 'PRIMARY KEY (character_id, slot)' in sql
assert 'txn_id       text        PRIMARY KEY' in sql
assert 'REFERENCES "角色".inventory (character_id, slot)' in sql
table = open(root + '/src/Character/EnhanceTable.cs', encoding='utf-8').read()
assert 'WrittenMaxLevel = 12' in table
assert 'WrittenTierIds' in table and 'WrittenBounds' in table
assert 'success_rate 必须在 0 到 1 之间' in table
repo = open(root + '/src/Character/EnhanceRepository.cs', encoding='utf-8').read()
assert 'BeginTransactionAsync' in repo
assert 'for update of i' in repo
assert 'wrong_stone' in repo and 'at_cap' in repo and 'gold_insufficient' in repo
assert 'Math.Max(0, levelBefore - 1)' in repo
assert 'Random.Shared.NextDouble()' in repo
assert '没有失败累计必成' in repo
svc = open(root + '/src/Character/CharacterGrpcService.cs', encoding='utf-8').read()
assert 'EnhanceOutcome outcome' in svc and 'enhances.ApplyAsync' in svc
process = open(root + '/src/Character/CharacterProcess.cs', encoding='utf-8').read()
assert 'EnhanceTable.Load(config.EnhanceTable' in process
assert 'AddSingleton(enhances)' in process
api = open(root + '/src/Gateway/CharacterApi.cs', encoding='utf-8').read()
assert 'public sealed record EnhanceBody(ulong? CharacterId, uint? Slot, string? StoneId)' in api
assert 'MapPost("/character/enhance"' in api
assert 'OwnsAsync(client, account' in api
assert 'Guid.NewGuid()' in api
assert 'Npgsql' not in api
# 客户端 Net 层的检查放在客户端段，笔记本连服段不需要整个 Unity 工程。
# 概率数字只出现在配置里，服务端代码不写第二套
for path in ('src/Character/EnhanceTable.cs', 'src/Character/EnhanceRepository.cs',
             'src/Character/CharacterGrpcService.cs', 'src/Gateway/CharacterApi.cs'):
    body = open(root + '/' + path, encoding='utf-8').read()
    assert '0.8' not in body and 'success_rate =' not in body, path
print('static_code=ok tiers=3 max_level=12 gate_server_side=1')
PY
then ok "配置正本、契约字段、库层 CHECK、进程边界与事务链都落在指定文件里"
else bad "服务端静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 3. 强化表：缺字段与越界都起不来 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
base = json.load(open(root + '/config/enhance.json', encoding='utf-8'))
def dump(name, obj):
    json.dump(obj, open(tmp + '/' + name, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
w = copy.deepcopy(base); w.pop('max_level'); dump('bad-no-max.json', w)
w = copy.deepcopy(base); w['max_level'] = 20; dump('bad-max.json', w)
w = copy.deepcopy(base); w.pop('gold_kind'); dump('bad-no-gold-kind.json', w)
w = copy.deepcopy(base); w['gold_kind'] = '圆宝'; dump('bad-gold-kind.json', w)
w = copy.deepcopy(base); w['tiers'][0]['to_level'] = 7; dump('bad-bounds.json', w)
w = copy.deepcopy(base); w['tiers'][1]['stone_id'] = 'stone_missing'; dump('bad-stone.json', w)
w = copy.deepcopy(base); w['tiers'][2]['success_rate'] = 1.5; dump('bad-rate-high.json', w)
w = copy.deepcopy(base); w['tiers'][0]['success_rate'] = -0.1; dump('bad-rate-low.json', w)
w = copy.deepcopy(base); w['tiers'].pop(); dump('bad-two-tiers.json', w)
w = copy.deepcopy(base); w['tiers'][0]['stone_cost'] = 0; dump('bad-stone-cost.json', w)
w = copy.deepcopy(base); w['tiers'][2]['stone_id'] = w['tiers'][0]['stone_id']; dump('bad-same-stone.json', w)
PY
config_reject() {
  local label="$1" file="$2" expect="$3" out
  out="$("$DOTNET" "$CHARACTER_DLL" --enhance-state --enhance-config "$TMP/$file" --config config/character.json 2>&1)"
  if printf '%s' "$out" | grep -qF "$expect"; then ok "$label"; else bad "$label：${out:0:160}"; fi
}
config_reject "缺 max_level 起不来" bad-no-max.json "config_error=enhance.json 缺 max_level"
config_reject "把 +12 改成 +20 起不来" bad-max.json "config_error=enhance.json 的 max_level 必须是方案写定的 12"
config_reject "缺 gold_kind 起不来" bad-no-gold-kind.json "config_error=enhance.json 缺 gold_kind"
config_reject "扣不存在的币种起不来" bad-gold-kind.json "config_error=enhance.json 的 gold_kind 不在币种表里"
config_reject "改动方案写定的三档分界起不来" bad-bounds.json "config_error=enhance.json 的 low 等级分界必须照方案 9.2 写定"
config_reject "材料不在 items.json 起不来" bad-stone.json "config_error=enhance.json 的 mid 材料不在 items.json 里"
config_reject "成功率大于 1 起不来" bad-rate-high.json "config_error=enhance.json 的 high success_rate 必须在 0 到 1 之间"
config_reject "成功率为负起不来" bad-rate-low.json "config_error=enhance.json 的 low success_rate 必须在 0 到 1 之间"
config_reject "少一档起不来" bad-two-tiers.json "config_error=enhance.json 必须正好是方案 9.2 写定的三档"
config_reject "石头消耗为 0 起不来" bad-stone-cost.json "config_error=enhance.json 的 low stone_cost 必须大于 0"
config_reject "三档共用同一种石头起不来" bad-same-stone.json "config_error=enhance.json 三档不能共用同一种强化石"
if "$DOTNET" "$CHARACTER_DLL" --enhance-state --config config/character.json 2>&1 \
    | grep -qF 'enhance_table_state=ok version=1 max_level=12 gold_kind=gold tiers=low:1-6:stone_enhance_low,mid:7-9:stone_enhance_mid,high:10-12:stone_enhance_high rates=0.8,0.5,0.2'; then
  ok "只读入口与启动共用同一套校验，好配置读出来的三档与概率正确"
else
  bad "好配置的只读入口输出不对"
fi

if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 gamedb 凭据，连服段跑不了（这一段要在笔记本上跑）"
  finish
fi

echo "== 4. 建表、播种与临时配置 =="
MIGRATE_OK=1
for sql in account.sql character.sql dungeon_entry.sql inventory.sql currency.sql transaction.sql exp_settlement.sql enhance.sql; do
  if [ "$sql" = "account.sql" ]; then
    if ! "$DOTNET" "$GATEWAY_DLL" --migrate --sql "db/character/$sql" --config config/gateway.json >"$TMP/migrate-$sql.log" 2>&1; then
      MIGRATE_OK=0; bad "建表失败：$sql"; tail -8 "$TMP/migrate-$sql.log" | detail
    fi
  elif ! "$DOTNET" "$CHARACTER_DLL" --migrate --sql "db/character/$sql" --config config/character.json >"$TMP/migrate-$sql.log" 2>&1; then
    MIGRATE_OK=0; bad "建表失败：$sql"; tail -8 "$TMP/migrate-$sql.log" | detail
  fi
done
[ "$MIGRATE_OK" = "1" ] && ok "角色 schema 六张表就位（enhance 在 inventory 之后建）"
[ "$MIGRATE_OK" = "1" ] || finish
HAVE_DB=1
for acct in "$ACCOUNT" "$OTHER"; do
  printf '%s\n' "$TEST_PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$acct" --config config/gateway.json >>"$TMP/seed.log" 2>&1
done
ok "两个测试账号已播种"

ROOT="$ROOT" TMP="$TMP" CHAR_PORT="$CHAR_PORT" GATEWAY_PORT="$GATEWAY_PORT" WORLD_PORT="$WORLD_PORT" python3 - <<'PY'
import json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
e = json.load(open(root + '/config/enhance.json', encoding='utf-8'))
for t in e['tiers']: t['success_rate'] = 1.0
json.dump(e, open(tmp + '/enhance.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
c = {
    'schema': 'character-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'db_pool_max': 6, 'bag_slots': 40,
    'dungeon_table': 'config/dungeons.json', 'exp_table': 'config/exp.json',
    'enhance_table': tmp + '/enhance.json',
}
json.dump(c, open(tmp + '/character.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
a = {
    'schema': 'gateway-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['GATEWAY_PORT'],
    'token_ttl_s': 7200, 'pbkdf2_iterations': 210000,
    'battle_upstream': 'ws://127.0.0.1:60001/battle',
    'character_upstream': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'world_upstream': 'http://127.0.0.1:' + os.environ['WORLD_PORT'],
}
json.dump(a, open(tmp + '/gateway.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY

echo "== 5. 起角色服与网关 =="
CHARACTER_LOG="$TMP/character.log"
"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$CHARACTER_LOG" 2>&1 & CHARACTER_PID=$!
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >"$TMP/gateway.log" 2>&1 & GATEWAY_PID=$!
if wait_ready "$CHARACTER_LOG" '^ready name=Character' "角色服" \
   && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关" \
   && wait_line "$CHARACTER_LOG" 'rates=1,1,1'; then
  ok "角色服与网关起来，强化表按临时配置读成全成功"
else
  bad "进程没起来或强化表没读到临时配置"; tail -12 "$CHARACTER_LOG" | detail; finish
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then ok "两个账号都拿到令牌"; else bad "登录失败"; finish; fi
if create_char "$TOKEN" "p14a_a" && [ -n "$NEW_ID" ]; then ID="$NEW_ID"; ok "建好主角色 id=$ID"; else bad "建主角色失败"; finish; fi
if create_char "$OTHER_TOKEN" "p14a_other" && [ -n "$NEW_ID" ]; then OTHER_ID="$NEW_ID"; ok "建好别人的角色 id=$OTHER_ID"; else bad "建别人的角色失败"; finish; fi

echo "== 6. 鉴权与归属复核 =="
resp="$(enhance "" "$ID" stone_enhance_low)"
[ "$(code_of "$resp")" = "401" ] && ok "没有令牌 401" || bad "没有令牌没被挡：$(code_of "$resp")"
resp="$(enhance forged "$ID" stone_enhance_low)"
[ "$(code_of "$resp")" = "401" ] && ok "伪造令牌 401" || bad "伪造令牌没被挡：$(code_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/character/enhance?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"slot\":1}")"
[ "$(code_of "$resp")" = "400" ] && ok "缺 stone_id 400" || bad "缺石头没被挡：$(code_of "$resp")"
resp="$(enhance "$TOKEN" "$OTHER_ID" stone_enhance_low)"
[ "$(code_of "$resp")" = "404" ] && ok "别人的角色 404（网关复核归属）" || bad "别人的角色没被挡：$(code_of "$resp")"

echo "== 7. 发放装备、三档石头与金币 =="
SEED_OK=1
for spec in "1 sword_trial 1" "2 stone_enhance_low 60" "3 stone_enhance_mid 60" "4 stone_enhance_high 60"; do
  set -- $spec
  r="$(add_item "$TOKEN" "$ID" "$1" "$2" "$3")"
  [ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "发 $2 失败：$(body_of "$r")"; }
done
r="$(add_gold 100000)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "发金币失败：$(body_of "$r")"; }
r="$(add_item "$OTHER_TOKEN" "$OTHER_ID" 1 sword_trial 1)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "给别人的角色发装备失败"; }
[ "$SEED_OK" = "1" ] && ok "装备、三档石头各 60 个、100000 金币到位"
[ "$SEED_OK" = "1" ] || finish
expect_state 0 100000 60 60 60 "库里初始状态：+0，金币与三档石头都没动"

echo "== 8. 三档材料与 +12 上限 =="
LOW_OK=1
for lv in 1 2 3 4 5 6; do
  resp="$(enhance "$TOKEN" "$ID" stone_enhance_low)"
  BODY="$(body_of "$resp")"
  if [ "$(code_of "$resp")" = "200" ] \
     && [ "$(expect_json "$BODY" ok=true success=true level_before=$((lv - 1)) level=$lv max_level=12 tier=low required_stone=stone_enhance_low stone_cost=1 gold_cost=100)" = "ok" ]; then
    :
  else
    LOW_OK=0; bad "低级石第 $lv 手没按预期成功：$(code_of "$resp") $BODY"
  fi
done
[ "$LOW_OK" = "1" ] && ok "低级石连点 6 手，+1 到 +6 都按配置成功"
expect_state 6 99400 54 60 60 "到 +6：扣了 6 个低级石与 600 金币"

resp="$(enhance "$TOKEN" "$ID" stone_enhance_low)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=false reason=wrong_stone level=0 level_before=0 success=false stone_cost=0 gold_cost=0 gold=0 max_level=12)" = "ok" ]; then
  ok "低级石点 +7 被拒绝：200 + ok=false + wrong_stone，未生效数值字段都是 0"
else
  bad "低级石点 +7 没被挡：$(code_of "$resp") $BODY"
fi
expect_state 6 99400 54 60 60 "被拒后库里等级、金币、石头一格没动（整笔回滚）"

MID_OK=1
for lv in 7 8 9; do
  resp="$(enhance "$TOKEN" "$ID" stone_enhance_mid)"
  BODY="$(body_of "$resp")"
  if [ "$(code_of "$resp")" = "200" ] \
     && [ "$(expect_json "$BODY" ok=true success=true level_before=$((lv - 1)) level=$lv max_level=12 tier=mid required_stone=stone_enhance_mid stone_cost=1 gold_cost=200)" = "ok" ]; then :
  else MID_OK=0; bad "中级石第 $lv 手没按预期成功：$BODY"; fi
done
[ "$MID_OK" = "1" ] && ok "中级石点 +7 到 +9，低级石代替不了"
resp="$(enhance "$TOKEN" "$ID" stone_enhance_mid)"
BODY="$(body_of "$resp")"
[ "$(code_of "$resp")" = "200" ] && [ "$(expect_json "$BODY" ok=false reason=wrong_stone level=0)" = "ok" ] \
  && ok "中级石点 +10 被拒绝" || bad "中级石点 +10 没被挡：$BODY"

HIGH_OK=1
for lv in 10 11 12; do
  resp="$(enhance "$TOKEN" "$ID" stone_enhance_high)"
  BODY="$(body_of "$resp")"
  if [ "$(code_of "$resp")" = "200" ] \
     && [ "$(expect_json "$BODY" ok=true success=true level_before=$((lv - 1)) level=$lv max_level=12 tier=high required_stone=stone_enhance_high stone_cost=2 gold_cost=400)" = "ok" ]; then :
  else HIGH_OK=0; bad "高级石第 $lv 手没按预期成功：$BODY"; fi
done
[ "$HIGH_OK" = "1" ] && ok "高级石点 +10 到 +12，每次按配置扣 2 个石头与 400 金币"
expect_state 12 97600 54 57 54 "到 +12：金币 97600、低级 54、中级 57、高级 54"
resp="$(enhance "$TOKEN" "$ID" stone_enhance_high)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] && [ "$(expect_json "$BODY" ok=false reason=at_cap level=0 success=false)" = "ok" ]; then
  ok "+12 再点被拒绝：at_cap，最高就是 +12"
else
  bad "+12 没封顶：$BODY"
fi
expect_state 12 97600 54 57 54 "封顶被拒后库里状态不变"

echo "== 9. 改配置重启，失败只掉 1 级 =="
# 只是把等级构造回 +11，好让「+12 那一档」能再点一次；等级仍由角色服的库表持有。
"$DOTNET" "$CHARACTER_DLL" --raw-enhance-write "$ID" 1 11 --config "$TMP/character.json" >/dev/null 2>&1
restart_character() {
  local rates="$1" log="$2"
  ROOT="$ROOT" TMP="$TMP" RATES="$rates" python3 - <<'PY'
import json, os
p = os.environ['TMP'] + '/enhance.json'
e = json.load(open(p, encoding='utf-8'))
for t, r in zip(e['tiers'], [float(x) for x in os.environ['RATES'].split(',')]):
    t['success_rate'] = r
json.dump(e, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
  stop_pid "$CHARACTER_PID"
  CHARACTER_LOG="$log"
  "$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$CHARACTER_LOG" 2>&1 & CHARACTER_PID=$!
  wait_ready "$CHARACTER_LOG" '^ready name=Character' "角色服（重启）"
}
if restart_character "0,0,0" "$TMP/character2.log" && wait_line "$CHARACTER_LOG" 'rates=0,0,0'; then
  ok "改概率后重启角色服即生效：三档全 0（只改配置，没改代码）"
else
  bad "重启后没读到新概率"; grep 'enhance_table_version' "$CHARACTER_LOG" | detail; finish
fi
FAIL_OK=1
for pair in "11 10" "10 9" "9 8"; do
  set -- $pair
  resp="$(enhance "$TOKEN" "$ID" stone_enhance_high)"
  BODY="$(body_of "$resp")"
  if [ "$(code_of "$resp")" = "200" ] \
     && [ "$(expect_json "$BODY" ok=true success=false level_before=$1 level=$2 max_level=12 tier=high stone_cost=2 gold_cost=400)" = "ok" ]; then :
  else FAIL_OK=0; bad "$1 -> $2 没按预期只掉 1 级：$BODY"; fi
  expect_state "$2" "$((97600 - (11 - $2) * 400))" 54 57 "$((54 - (11 - $2) * 2))" "库里 +$2：连续失败也只掉 1 级，没有必成"
done
[ "$FAIL_OK" = "1" ] && ok "连续三次失败：+11 → +10 → +9 → +8，没有清零、没有必成"
r="$(curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/inventory?token=${TOKEN}&character_id=${ID}")"
printf '%s' "$r" | python3 -c 'import json,sys
slots=json.load(sys.stdin)["slots"]
assert any(s["slot"]==1 and s["item_id"]=="sword_trial" for s in slots), slots' \
  && ok "失败后装备还在，不碎、不掉阶" || bad "失败后装备不见了"

echo "== 10. 金币不够整笔回滚、伪造字段与老规则 =="
GOLD_BEFORE="$(enhance_state | awk '{print $2}')"
# 当前 +8，目标 +9 属于中级档（200 金币）：要用这一档的石头，并把余额压到 100，
# 否则会先撞 wrong_stone，轮不到金币检查。
add_gold "-$((GOLD_BEFORE - 100))" >/dev/null 2>&1
resp="$(enhance "$TOKEN" "$ID" stone_enhance_mid)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] && [ "$(expect_json "$BODY" ok=false reason=gold_insufficient level=0)" = "ok" ]; then
  ok "金币不够：200 + ok=false + gold_insufficient"
else
  bad "金币不够没被挡：$BODY"
fi
expect_state 8 100 54 57 48 "钱不够时石头与等级都没变，扣费与改等级是同一笔事务"
add_gold "$((GOLD_BEFORE - 100))" >/dev/null 2>&1
resp="$(enhance "$OTHER_TOKEN" "$OTHER_ID" stone_enhance_low)"
BODY="$(body_of "$resp")"
[ "$(code_of "$resp")" = "200" ] && [ "$(expect_json "$BODY" ok=false reason=stone_insufficient)" = "ok" ] \
  && ok "没有强化石的角色被拒：stone_insufficient" || bad "缺石头没被挡：$BODY"

if restart_character "1,1,1" "$TMP/character3.log" && wait_line "$CHARACTER_LOG" 'rates=1,1,1'; then
  ok "再把概率改回全 1 并重启，同样即时生效"
else
  bad "第二次重启没读到新概率"; finish
fi
resp="$(enhance "$TOKEN" "$ID" stone_enhance_mid)"
BODY="$(body_of "$resp")"
[ "$(code_of "$resp")" = "200" ] && [ "$(expect_json "$BODY" ok=true success=true level_before=8 level=9 max_level=12 tier=mid)" = "ok" ] \
  && ok "改配置后同一角色再点一次就成功：+8 → +9" || bad "改配置后强化没生效：$BODY"

FAKE=',"level":12,"success_rate":1,"success":true,"gold":999999,"txn_id":"fake","account":"p14a_other","max_level":99,"stone_cost":0,"gold_cost":0'
resp="$(enhance "$TOKEN" "$ID" stone_enhance_high "$FAKE")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true success=true level_before=9 level=10 max_level=12 stone_cost=2 gold_cost=400 tier=high)" = "ok" ]; then
  ok "请求体塞等级 / 成功率 / 结果 / 余额 / 事务号 / 账号全无效，仍按服务端配置走"
else
  bad "伪造字段影响了结果：$BODY"
fi

LAST_TXN="$(grep -o 'txn=enhance:[0-9a-f]*' "$TMP/gateway.log" | tail -1 | cut -d= -f2)"
TXN_ROWS="$("$DOTNET" "$CHARACTER_DLL" --enhance-txn-count "$LAST_TXN" --config "$TMP/character.json" 2>/dev/null | sed -n 's/^enhance_txn_count=\([0-9]*\) .*/\1/p')"
[ "$TXN_ROWS" = "1" ] && ok "这一笔的服务端事务号在库里恰好一行" || bad "事务号行数不对：txn=$LAST_TXN rows=$TXN_ROWS"

RAW_OUT="$("$DOTNET" "$CHARACTER_DLL" --raw-enhance-write "$ID" 1 13 --config "$TMP/character.json" 2>&1)"
printf '%s' "$RAW_OUT" | grep -q 'raw_enhance=rejected' \
  && ok "库层 CHECK 挡住 +13" || bad "库层没挡 +13：$RAW_OUT"
RAW_OUT="$("$DOTNET" "$CHARACTER_DLL" --raw-enhance-write "$ID" 1 -1 --config "$TMP/character.json" 2>&1)"
printf '%s' "$RAW_OUT" | grep -q 'raw_enhance=rejected' \
  && ok "库层 CHECK 挡住负等级" || bad "库层没挡负等级：$RAW_OUT"
expect_state 10 "$((GOLD_BEFORE - 600))" 54 56 46 "越界直写被拒后，库里等级仍是 +10，材料按配置扣"

# 老规则不回退：绑定、进本、经验结算、发奖幂等、余额不为负
r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/inventory/can-trade?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"slot\":1}")"
[ "$(json_field "$r" tradable)" = "false" ] && [ "$(json_field "$r" reason)" = "bound_item" ] \
  && ok "P1-1b3 不回退：拾取即绑的装备仍不可交易" || bad "绑定判定回退了：$r"
r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"dungeon_id\":\"main\",\"party_size\":1}")"
[ "$(json_field "$r" allowed)" = "true" ] && ok "P1-2a 不回退：主线单人仍能进本" || bad "进本回退了：$r"
r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/exp/settle?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"txn_id\":\"p14a-exp-1\",\"source\":\"task\",\"party_size\":1}")"
[ "$(json_field "$r" level_cap)" = "100" ] && ok "P1-2d 不回退：经验结算仍按 level_cap=100" || bad "经验结算回退了：$r"
for _ in 1 2; do
  r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/grant?token=${TOKEN}" \
    -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"txn_id\":\"p14a-grant-1\",\"entries\":[{\"currency\":{\"kind\":\"gold\",\"delta\":1}}]}")"
done
[ "$(json_field "$r" duplicate)" = "true" ] && ok "P1-1b5 不回退：同一发奖事务号仍只发一次" || bad "发奖幂等回退了：$r"
r="$(add_gold -999999999)"
[ "$(code_of "$r")" != "200" ] && ok "P1-1b4 不回退：金币减成负数被拒" || bad "余额能写成负数：$(body_of "$r")"

finish
