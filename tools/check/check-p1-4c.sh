#!/usr/bin/env bash
# P1-4c 核对：强化转移（方案 9.3）。
#
# 完成标准（开发架构第 14 节第 37 行 + 第 11 节 P1-4 + 第 13 节 P1-4c + 方案 9.3）：
#   来源 +3、目标 +5 被拒绝；成功后来源变为 +0、目标覆盖为来源原等级（不是相加）；
#   来源不高于目标 / 同一格 / 空格 / 不是装备都拒绝且一笔不动；不限部位；
#   绑定规则在同一笔事务里完成（来源绑定时目标变为绑定，非绑转非绑仍可交易）；
#   转移必定成功、不掷点；消耗金币（费用随转出等级上升）与一枚转移石；
#   改配置重启生效；伪造字段无效；P1-4a / P1-4b 老规则不回退。
#
# 两种模式：
#   bash tools/check/check-p1-4c.sh                          服务端段：编译、静态、连库连服（要有 gamedb）
#   MMO_P14C_MODE=client bash tools/check/check-p1-4c.sh    客户端段：静态 + Unity 编辑器自检（要有 Unity）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
# 公共函数库：ok/bad/detail/stop_pid/body_of/code_of/json_field/expect_json/
# wait_ready/wait_line/finish，签名与既有核对脚本一致，别再复制一份。
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
MODE="${MMO_P14C_MODE:-server}"

CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"

CHAR_PORT=${MMO_P14C_CHAR_PORT:-62116}
GATEWAY_PORT=${MMO_P14C_GATEWAY_PORT:-61117}
WORLD_PORT=${MMO_P14C_WORLD_PORT:-63098}
ACCOUNT="p14c_user"
OTHER="p14c_other"
TEST_PASSWORD="p14c-Pw-8d20"
CHAR_NAMES=("p14c_a" "p14c_other" "p14c_nostone")

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
NOSTONE_ID=""

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

transfer() {
  local token="$1" char="$2" from="$3" to="$4" extra="${5:-}"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character/transfer?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"from_slot\":${from},\"to_slot\":${to}${extra}}"
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

can_trade() {
  curl -s -m 10 -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/inventory/can-trade?token=${TOKEN}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${ID},\"slot\":$1}"
}

# 不看回包，从库里独立读回：来源等级 目标等级 来源绑定 目标绑定 金币 转移石
state_line() {
  "$DOTNET" "$CHARACTER_DLL" --transfer-level "$1" "$2" "$3" \
    --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^transfer_state=ok .* from_level=\([0-9-]*\) to_level=\([0-9-]*\) from_bound=\([A-Za-z]*\) to_bound=\([A-Za-z]*\) gold=\([0-9-]*\) stone=\([0-9-]*\)/\1 \2 \3 \4 \5 \6/p'
}

# $1=角色 $2=来源格 $3=目标格，后面 6 列是期望值，「-」表示这一列不检查。
expect_state() {
  local char="$1" from="$2" to="$3" label="${10:-}" got want i
  shift 3
  got="$(state_line "$char" "$from" "$to")"
  want="$1 $2 $3 $4 $5 $6"
  local -a g w
  read -ra g <<<"$got"
  read -ra w <<<"$want"
  for i in 0 1 2 3 4 5; do
    if [ "${w[$i]:--}" != "-" ] && [ "${g[$i]:-}" != "${w[$i]}" ]; then
      bad "${label}：state=[$got] 期望=[$want]"
      return
    fi
  done
  ok "$label"
}

# 直写强化等级造并发现场（P1-4a 已有的只给核对用入口）。
level() { "$DOTNET" "$CHARACTER_DLL" --raw-enhance-write "$1" "$2" "$3" \
  --config "$TMP/character.json" >/dev/null 2>&1; }

# 只改配置重启角色服：改强化概率或转移费用，不动代码。
restart_character() {
  local rates="${1:-}" fee="${2:-}" log="$3"
  ROOT="$ROOT" TMP="$TMP" RATES="$rates" FEE="$fee" python3 - <<'PY'
import json, os
tmp, rates, fee = os.environ['TMP'], os.environ['RATES'], os.environ['FEE']
if rates:
    p = tmp + '/enhance.json'
    e = json.load(open(p, encoding='utf-8'))
    for t, r in zip(e['tiers'], [float(x) for x in rates.split(',')]):
        t['success_rate'] = r
    json.dump(e, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
if fee:
    p = tmp + '/transfer.json'
    e = json.load(open(p, encoding='utf-8'))
    e['gold_cost_by_level'][0] = int(fee)
    json.dump(e, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
  stop_pid "$CHARACTER_PID"
  CHARACTER_LOG="$log"
  "$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$CHARACTER_LOG" 2>&1 & CHARACTER_PID=$!
  wait_ready "$CHARACTER_LOG" '^ready name=Character' "角色服（重启）"
}

TITLE="P1-4c（${MODE}）"

# ---------------------------------------------------------------- 客户端段
if [ "$MODE" = "client" ]; then
  echo "== C1. 转移配置、契约与客户端静态断言 =="
  if ROOT="$ROOT" python3 - >"$TMP/client-static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/transfer.json', encoding='utf-8'))
items = {i['id']: i for i in json.load(open(root + '/config/items.json', encoding='utf-8'))['items']}
costs = cfg['gold_cost_by_level']
assert len(costs) == 12 and all(costs[i] > costs[i-1] > 0 for i in range(1, 12))
assert cfg['stone_cost'] == 1 and cfg['stone_item_id'] == 'stone_transfer'
assert items['stone_transfer']['kind'] == 'material' and items['stone_transfer']['bind'] == 'none'
assert items['sword_trade']['kind'] == 'equipment' and items['sword_trade']['bind'] == 'none'
assert '数值待定' in cfg['note']
assert 'success_rate' not in cfg
proto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
req = re.search(r'message TransferEnhanceRequest \{(.*?)\}', proto, re.S).group(1)
resp = re.search(r'message TransferEnhanceResponse \{(.*?)\}', proto, re.S).group(1)
fields = set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*=', req))
assert fields == {'account', 'character_id', 'from_slot', 'to_slot', 'txn_id'}, fields
for name in ('from_level', 'to_level', 'target_bound_before', 'target_bound',
             'gold_cost', 'stone_item', 'duplicate'):
    assert re.search(r'\b' + name + r'\s*=', resp), name
client = open(root + '/client/MMOGame/Assets/Net/CharacterTransfer.cs', encoding='utf-8').read()
assert 'RequestBody(ulong characterId, uint fromSlot, uint toSlot)' in client
assert 'from_slot' in client and 'to_slot' in client
assert 'TransferTable' not in client and 'Random' not in client and 'JsonUtility' not in client
assert 'transfer.json' not in client and 'stone_transfer' not in client
repo = open(root + '/src/Character/TransferRepository.cs', encoding='utf-8').read()
assert 'Random' not in repo and 'BeginTransactionAsync' in repo
assert 'if (fromLevelBefore <= toLevelBefore)' in repo
assert 'bound = true, bound_at = now()' in repo
print('client_static=ok cost_rising=1 no_random=1')
PY
  then ok "费用递增且只在配置、请求只有两格、转移不掷点"
  else bad "客户端静态断言失败"; cat "$TMP/client-static.out" | detail
  fi

  echo "== C2. Unity 编辑器自检：转移请求与回包拆装 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    finish "$TITLE"
  fi
  if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod P14cCheck.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity P14cCheck.Run 退出码 0"
  else
    bad "Unity P14cCheck.Run 失败"
    tail -25 "$TMP/unity.log" | detail
  fi
  for key in config proto wire boundary; do
    if grep -q "p14c_${key}=ok" "$TMP/unity.log"; then
      ok "编辑器自检 p14c_${key}=ok"
    else
      bad "编辑器自检 p14c_${key} 没过"
      grep "p14c_${key}" "$TMP/unity.log" | tail -2 | detail
    fi
  done
  finish "$TITLE"
fi

# ---------------------------------------------------------------- 服务端段
echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then bad "找不到 dotnet"; finish "$TITLE"; fi
BUILD_OK=1
for proj in src/Shared/Shared.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj src/World/World.csproj src/Battle/Battle.csproj; do
  if ! "$DOTNET" build "$ROOT/$proj" -c Release --nologo -v q >"$TMP/build-$(basename "$proj" .csproj).log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E "error " "$TMP/build-$(basename "$proj" .csproj).log" | head -4 | detail
  fi
done
if [ "$BUILD_OK" = "1" ]; then ok "五个服务端工程编译通过"; else finish "$TITLE"; fi

echo "== 2. 静态断言（服务端） =="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/transfer.json', encoding='utf-8'))
items = {i['id']: i for i in json.load(open(root + '/config/items.json', encoding='utf-8'))['items']}
enh = json.load(open(root + '/config/enhance.json', encoding='utf-8'))
assert len(cfg['gold_cost_by_level']) == enh['max_level'] == 12
assert all(cfg['gold_cost_by_level'][i] > cfg['gold_cost_by_level'][i-1] > 0
           for i in range(1, 12))
assert cfg['stone_cost'] == 1 and cfg['stone_item_id'] not in {t['stone_id'] for t in enh['tiers']}
assert cfg['gold_kind'] == enh['gold_kind'] == 'gold'
assert items['stone_transfer']['kind'] == 'material'
assert items['sword_trade']['kind'] == 'equipment' and items['sword_trade']['bind'] == 'none'
assert items['sword_trial']['bind'] == 'on_pickup'
proto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
req = re.search(r'message TransferEnhanceRequest \{(.*?)\}', proto, re.S).group(1)
fields = set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*=', req))
assert fields == {'account', 'character_id', 'from_slot', 'to_slot', 'txn_id'}, fields
assert re.search(r'rpc TransferEnhance\(TransferEnhanceRequest\) returns \(TransferEnhanceResponse\)', proto)
sql = open(root + '/db/character/transfer.sql', encoding='utf-8').read()
assert 'txn_id       text        PRIMARY KEY' in sql
assert 'CREATE TABLE IF NOT EXISTS "角色".transfer_txn' in sql
table = open(root + '/src/Character/TransferTable.cs', encoding='utf-8').read()
assert '费用随转出的强化等级上升' in table and '必须是方案写定的 1' in table
assert 'gold_cost_by_level' in table and 'currencies.Find(goldKind)' in table
repo = open(root + '/src/Character/TransferRepository.cs', encoding='utf-8').read()
assert 'BeginTransactionAsync' in repo and 'order by slot for update' in repo
assert 'if (fromLevelBefore <= toLevelBefore)' in repo
assert 'source_not_higher' in repo and 'same_slot' in repo
assert 'source_empty' in repo and 'target_empty' in repo
assert 'source_not_equipment' in repo and 'target_not_equipment' in repo
assert 'stone_insufficient' in repo and 'gold_insufficient' in repo
assert 'bound = true, bound_at = now()' in repo
assert '(fromSlot, 0), (toSlot, fromLevelBefore)' in repo
assert 'Random' not in repo, '转移必定成功，不能掷点'
svc = open(root + '/src/Character/CharacterGrpcService.cs', encoding='utf-8').read()
assert 'TransferEnhance(' in svc and 'transfers.ApplyAsync' in svc
api = open(root + '/src/Gateway/CharacterApi.cs', encoding='utf-8').read()
assert 'public sealed record TransferBody(ulong? CharacterId, uint? FromSlot, uint? ToSlot)' in api
assert 'MapPost("/character/transfer"' in api and 'OwnsAsync(client, account' in api
assert 'Guid.NewGuid()' in api and 'Npgsql' not in api
program = open(root + '/src/Character/Program.cs', encoding='utf-8').read()
process = open(root + '/src/Character/CharacterProcess.cs', encoding='utf-8').read()
assert 'case "--transfer-state"' in program and 'case "--transfer-level"' in program
# 启动日志与只读入口必须同形，否则 wait_line 等不到。
for name in ('transfer_stone=', 'transfer_stone_cost=', 'transfer_gold='):
    assert program.count(name) == 1 and process.count(name) == 1, name
print('static_code=ok gate_server_side=1 no_random=1')
PY
then ok "配置、契约、事务、绑定、进程边界与只读入口都落在指定文件里"
else bad "服务端静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 3. 转移表的配置校验：缺字段 / 非法数值都起不来 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
base = json.load(open(root + '/config/transfer.json', encoding='utf-8'))
def dump(name, obj):
    json.dump(obj, open(tmp + '/' + name, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
w = copy.deepcopy(base); w.pop('stone_item_id'); dump('t-no-stone.json', w)
w = copy.deepcopy(base); w['stone_item_id'] = 'stone_not_exist'; dump('t-stone-missing.json', w)
w = copy.deepcopy(base); w['stone_item_id'] = 'stone_enhance_low'; dump('t-stone-reuse.json', w)
w = copy.deepcopy(base); w['stone_item_id'] = 'sword_trial'; dump('t-stone-kind.json', w)
w = copy.deepcopy(base); w['stone_cost'] = 2; dump('t-stone-cost.json', w)
w = copy.deepcopy(base); w.pop('gold_cost_by_level'); dump('t-no-costs.json', w)
w = copy.deepcopy(base); w['gold_cost_by_level'] = base['gold_cost_by_level'][:-1]; dump('t-short-costs.json', w)
w = copy.deepcopy(base); w['gold_cost_by_level'][5] = w['gold_cost_by_level'][4]; dump('t-flat-costs.json', w)
w = copy.deepcopy(base); w['gold_cost_by_level'][0] = 0; dump('t-zero-costs.json', w)
w = copy.deepcopy(base); w['gold_kind'] = 'yuanbao'; dump('t-gold-kind.json', w)
w = copy.deepcopy(base); w['gold_kind'] = 'not_a_currency'; dump('t-gold-missing.json', w)
PY
config_reject() {
  local label="$1" file="$2" expect="$3" out
  out="$("$DOTNET" "$CHARACTER_DLL" --transfer-state --transfer-config "$TMP/$file" \
    --config config/character.json 2>&1)"
  if printf '%s' "$out" | grep -qF "$expect"; then ok "$label"; else bad "$label：${out:0:180}"; fi
}
config_reject "缺 stone_item_id 起不来" t-no-stone.json "config_error=transfer.json 缺 stone_item_id"
config_reject "转移石编号不在物品表里起不来" t-stone-missing.json "config_error=transfer.json 的 stone_item_id 不在 items.json 里"
config_reject "转移石不能复用强化石" t-stone-reuse.json "config_error=转移石不能和强化石或保护券是同一种"
config_reject "转移石指向装备起不来" t-stone-kind.json "config_error=transfer.json 的 stone_item_id 必须是材料"
config_reject "stone_cost 不是写定的一枚就起不来" t-stone-cost.json "config_error=transfer.json 的 stone_cost 必须是方案写定的 1"
config_reject "缺金币费用表起不来" t-no-costs.json "config_error=transfer.json 缺 gold_cost_by_level"
config_reject "费用表不是 12 项起不来" t-short-costs.json "config_error=transfer.json 的 gold_cost_by_level 必须正好 12 项"
config_reject "费用不随等级上升起不来" t-flat-costs.json "config_error=transfer.json 的 +6 金币费用必须高于 +5 的费用"
config_reject "费用写成 0 起不来" t-zero-costs.json "config_error=transfer.json 的 +1 金币费用必须大于 0"
config_reject "金币币种与强化表不一致起不来" t-gold-kind.json "config_error=transfer.json 的 gold_kind 必须和 enhance.json 一致"
config_reject "币种不存在起不来" t-gold-missing.json "config_error=transfer.json 的 gold_kind 不在币种表里"
if "$DOTNET" "$CHARACTER_DLL" --transfer-state --config config/character.json 2>&1 \
    | grep -qF 'transfer_stone=stone_transfer transfer_stone_cost=1 transfer_gold_kind=gold'; then
  ok "只读入口与启动共用同一套校验：好配置读出唯一一种转移石"
else
  bad "好配置的只读入口没输出转移石信息"
fi

if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 gamedb 凭据，连服段跑不了（这一段要在笔记本上跑）"
  finish "$TITLE"
fi

echo "== 4. 建表、播种与临时配置 =="
MIGRATE_OK=1
for sql in account.sql character.sql dungeon_entry.sql inventory.sql currency.sql transaction.sql exp_settlement.sql enhance.sql transfer.sql; do
  if [ "$sql" = "account.sql" ]; then
    if ! "$DOTNET" "$GATEWAY_DLL" --migrate --sql "db/character/$sql" --config config/gateway.json >"$TMP/migrate-$sql.log" 2>&1; then
      MIGRATE_OK=0; bad "建表失败：$sql"; tail -8 "$TMP/migrate-$sql.log" | detail
    fi
  elif ! "$DOTNET" "$CHARACTER_DLL" --migrate --sql "db/character/$sql" --config config/character.json >"$TMP/migrate-$sql.log" 2>&1; then
    MIGRATE_OK=0; bad "建表失败：$sql"; tail -8 "$TMP/migrate-$sql.log" | detail
  fi
done
[ "$MIGRATE_OK" = "1" ] && ok "角色 schema 就位（transfer 排在 enhance 之后）"
[ "$MIGRATE_OK" = "1" ] || finish "$TITLE"
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
t = json.load(open(root + '/config/transfer.json', encoding='utf-8'))
json.dump(t, open(tmp + '/transfer.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
c = {
    'schema': 'character-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'db_pool_max': 6, 'bag_slots': 40,
    'dungeon_table': 'config/dungeons.json', 'exp_table': 'config/exp.json',
    'enhance_table': tmp + '/enhance.json', 'transfer_table': tmp + '/transfer.json',
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
   && wait_line "$CHARACTER_LOG" 'transfer_stone=stone_transfer' \
   && wait_line "$CHARACTER_LOG" 'transfer_gold=120,150,190'; then
  ok "角色服与网关起来，转移表与只读入口同形"
else
  bad "进程没起来或启动日志缺转移表信息"; tail -12 "$CHARACTER_LOG" | detail; finish "$TITLE"
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then ok "两个账号都拿到令牌"; else bad "登录失败"; finish "$TITLE"; fi
if create_char "$TOKEN" "p14c_a" && [ -n "$NEW_ID" ]; then ID="$NEW_ID"; ok "建好主角色 id=$ID"; else bad "建主角色失败"; finish "$TITLE"; fi
if create_char "$OTHER_TOKEN" "p14c_other" && [ -n "$NEW_ID" ]; then OTHER_ID="$NEW_ID"; ok "建好别人的角色 id=$OTHER_ID"; else bad "建别人的角色失败"; finish "$TITLE"; fi
if create_char "$TOKEN" "p14c_nostone" && [ -n "$NEW_ID" ]; then NOSTONE_ID="$NEW_ID"; ok "建好无石头的角色 id=$NOSTONE_ID"; else bad "建无石头角色失败"; finish "$TITLE"; fi

echo "== 6. 鉴权、归属与两个格子的形状 =="
resp="$(transfer "" "$ID" 1 2)"
[ "$(code_of "$resp")" = "401" ] && ok "没有令牌 401" || bad "没有令牌没被挡：$(code_of "$resp")"
resp="$(transfer forged "$ID" 1 2)"
[ "$(code_of "$resp")" = "401" ] && ok "伪造令牌 401" || bad "伪造令牌没被挡：$(code_of "$resp")"
resp="$(transfer "$TOKEN" "$OTHER_ID" 1 2)"
[ "$(code_of "$resp")" = "404" ] && ok "别人的角色 404（网关复核归属）" || bad "别人的角色没被挡：$(code_of "$resp")"
resp="$(transfer "$TOKEN" "$ID" 0 2)"
[ "$(code_of "$resp")" = "400" ] && ok "from_slot=0 被挡：400 + bad_request" || bad "from_slot 形状没校验：$(body_of "$resp")"
resp="$(transfer "$TOKEN" "$ID" 1 0)"
[ "$(code_of "$resp")" = "400" ] && ok "to_slot=0 被挡：400 + bad_request" || bad "to_slot 形状没校验：$(body_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/character/transfer?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID},\"from_slot\":1,\"to_slot\":\"2\"}")"
[ "$(code_of "$resp")" = "400" ] && ok "to_slot 传字符串被挡：400 + bad_request" || bad "格子字段形状没校验：$(body_of "$resp")"

echo "== 7. 发放装备、转移石、金币与老卡材料 =="
SEED_OK=1
# 1/4 是拾取即绑的装备，2/3/10/11 是到账不绑的装备；5 是转移石。
for spec in "1 sword_trial 1" "2 sword_trade 1" "3 sword_trade 1" "4 sword_trial 1" \
            "5 stone_transfer 20" "6 ticket_protect 2" "7 stone_enhance_low 60" \
            "8 stone_enhance_mid 60" "9 stone_enhance_high 60" "10 sword_trade 1" "11 sword_trade 1"; do
  set -- $spec
  r="$(add_item "$TOKEN" "$ID" "$1" "$2" "$3")"
  [ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "发 $2 失败：$(body_of "$r")"; }
done
r="$(add_gold 100000)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "发金币失败：$(body_of "$r")"; }
r="$(add_item "$OTHER_TOKEN" "$OTHER_ID" 1 sword_trial 1)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "给别人的角色发装备失败"; }
# 无石头角色：两件装备、有金币，就是没有转移石。
for spec in "1 sword_trial 1" "2 sword_trade 1"; do
  set -- $spec
  r="$(add_item "$TOKEN" "$NOSTONE_ID" "$1" "$2" "$3")"
  [ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "无石头角色发 $2 失败"; }
done
r="$(curl -s -m 10 -w '\n%{http_code}' -X POST "http://127.0.0.1:${GATEWAY_PORT}/currency/add?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${NOSTONE_ID},\"kind\":\"gold\",\"delta\":100000}")"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "无石头角色发金币失败"; }
[ "$SEED_OK" = "1" ] && ok "两件绑定装备、四件非绑装备、20 枚转移石与金币到位"
[ "$SEED_OK" = "1" ] || finish "$TITLE"
level "$ID" 1 3
level "$ID" 2 5
level "$ID" 3 3
expect_state "$ID" 1 2 3 5 true false 100000 20 "库里初始状态：来源 +3 目标 +5、来源绑定目标非绑"

echo "== 8. 完成标准：来源 +3、目标 +5 被拒绝，一笔不动 =="
BEFORE="$(state_line "$ID" 1 2)"
resp="$(transfer "$TOKEN" "$ID" 1 2)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=false reason=source_not_higher from_level=0 to_level=0 max_level=12 gold_cost=0 target_bound=false duplicate=false)" = "ok" ]; then
  ok "来源 +3 目标 +5：200 + ok=false + source_not_higher，未生效数值字段都是 0"
else
  bad "来源不高于目标没被挡：$BODY"
fi
if [ "$(state_line "$ID" 1 2)" = "$BEFORE" ]; then ok "被拒后两格等级、绑定、金币与转移石一格没动"; else bad "被拒后库里状态变了"; fi
r="$(can_trade 2)"
[ "$(json_field "$r" tradable)" = "true" ] && ok "被拒后目标仍是非绑、可交易" || bad "被拒后目标绑定被改了：$r"

level "$ID" 1 3
resp="$(transfer "$TOKEN" "$ID" 1 3)"
[ "$(code_of "$resp")" = "200" ] && [ "$(json_field "$(body_of "$resp")" reason)" = "source_not_higher" ] \
  && ok "两格相等（+3 / +3）也按「必须高于」拒绝" || bad "相等没被挡：$(body_of "$resp")"
expect_state "$ID" 1 3 3 3 true false - - "相等被拒后两格等级没变"

echo "== 9. 同一格 / 空格 / 不是装备都拒绝 =="
resp="$(transfer "$TOKEN" "$ID" 1 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "same_slot" ] \
  && ok "来源与目标同一格：same_slot" || bad "同一格没被挡：$(body_of "$resp")"
resp="$(transfer "$TOKEN" "$ID" 1 30)"
[ "$(json_field "$(body_of "$resp")" reason)" = "target_empty" ] \
  && ok "目标格空：target_empty" || bad "目标空格没被挡：$(body_of "$resp")"
resp="$(transfer "$TOKEN" "$ID" 30 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "source_empty" ] \
  && ok "来源格空：source_empty" || bad "来源空格没被挡：$(body_of "$resp")"
resp="$(transfer "$TOKEN" "$ID" 1 5)"
[ "$(json_field "$(body_of "$resp")" reason)" = "target_not_equipment" ] \
  && ok "目标格是转移石：target_not_equipment" || bad "目标不是装备没被挡：$(body_of "$resp")"
resp="$(transfer "$TOKEN" "$ID" 5 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "source_not_equipment" ] \
  && ok "来源格是转移石：source_not_equipment" || bad "来源不是装备没被挡：$(body_of "$resp")"
expect_state "$ID" 1 2 3 5 true false 100000 20 "六类被拒后两格等级、绑定、金币与转移石都没动"

echo "== 10. 完成标准：成功后来源 +0、目标覆盖为来源原等级，不限部位 =="
level "$ID" 1 5
level "$ID" 2 3
resp="$(transfer "$TOKEN" "$ID" 1 2)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true reason= from_level_before=5 to_level_before=3 from_level=0 to_level=5 max_level=12 gold_cost=300 stone_item=stone_transfer stone_cost=1 stone_left=19 target_bound_before=false target_bound=true duplicate=false gold=99700)" = "ok" ]; then
  ok "来源 +5 目标 +3：成功覆盖为 +5、来源归 0、扣 1 枚石与 300 金币（+5 档），目标变为绑定"
else
  bad "成功转移没按预期：$BODY"
fi
expect_state "$ID" 1 2 0 5 true true 99700 19 "库里两格等级、绑定、金币与石头与回包一致"
r="$(can_trade 2)"
[ "$(json_field "$r" tradable)" = "false" ] && [ "$(json_field "$r" reason)" = "bound_item" ] \
  && ok "方案 9.3：来源已绑定而目标未绑定时目标变为绑定，can-trade 回 bound_item" \
  || bad "目标绑定没生效：$r"
r="$(can_trade 1)"
[ "$(json_field "$r" tradable)" = "false" ] && ok "来源本来就是绑定装备，仍不可交易" || bad "来源绑定被改了：$r"

echo "== 11. 非绑转非绑仍可交易；非绑转已绑目标保持绑定 =="
level "$ID" 10 6
level "$ID" 11 2
resp="$(transfer "$TOKEN" "$ID" 10 11)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true from_level=0 to_level=6 gold_cost=380 target_bound_before=false target_bound=false)" = "ok" ]; then
  ok "非绑 +6 转非绑 +2：目标覆盖为 +6、来源归 0，费用按 +6 档 380（随等级上升）"
else
  bad "非绑转非绑没按预期：$BODY"
fi
expect_state "$ID" 10 11 0 6 false false 99320 18 "库里两格等级与 +6 档费用一致"
r="$(can_trade 11)"
[ "$(json_field "$r" tradable)" = "true" ] && ok "非绑来源转非绑目标：目标仍可交易" || bad "非绑目标被误绑：$r"
level "$ID" 10 6
level "$ID" 2 1
resp="$(transfer "$TOKEN" "$ID" 10 2)"
BODY="$(body_of "$resp")"
if [ "$(expect_json "$BODY" ok=true from_level=0 to_level=6 target_bound_before=true target_bound=true)" = "ok" ]; then
  ok "非绑来源转已绑目标：覆盖成功，目标保持绑定，不解开"
else
  bad "非绑转已绑没按预期：$BODY"
fi
r="$(can_trade 2)"
[ "$(json_field "$r" tradable)" = "false" ] && ok "目标本来已绑定，转完仍绑定" || bad "已绑目标被解开了：$r"

echo "== 12. 转移必定成功、不吃强化概率；费用改配置重启生效 =="
if restart_character "0,0,0" "" "$TMP/character2.log" && wait_line "$CHARACTER_LOG" 'rates=0,0,0'; then
  ok "把三档强化概率改成 0 重启，验证转移不掷点"
else
  bad "重启后没读到新概率"; finish "$TITLE"
fi
level "$ID" 1 2
level "$ID" 3 0
resp="$(transfer "$TOKEN" "$ID" 1 3)"
BODY="$(body_of "$resp")"
if [ "$(expect_json "$BODY" ok=true from_level=0 to_level=2 gold_cost=150)" = "ok" ]; then
  ok "强化概率全 0 时转移照样成功：没有掷点，+2 档费用 150"
else
  bad "概率 0 时转移失败或费用不对：$BODY"
fi
if restart_character "1,1,1" "125" "$TMP/character3.log" \
   && wait_line "$CHARACTER_LOG" 'transfer_gold=125,150,190'; then
  ok "只改配置把 +1 档费用 120 改成 125，重启即生效（其它档不变）"
else
  bad "重启后没读到新费用"; grep 'transfer_table_version' "$CHARACTER_LOG" | detail; finish "$TITLE"
fi
level "$ID" 1 1
level "$ID" 4 0
resp="$(transfer "$TOKEN" "$ID" 1 4)"
BODY="$(body_of "$resp")"
if [ "$(expect_json "$BODY" ok=true from_level=0 to_level=1 gold_cost=125)" = "ok" ]; then
  ok "+1 转移按新配置的 125 扣费，代码里没有第二套数字"
else
  bad "改费用后回包不对：$BODY"
fi

echo "== 13. 转移石 / 金币不足整笔回滚，绑定也不生效 =="
level "$NOSTONE_ID" 1 5
level "$NOSTONE_ID" 2 1
resp="$(transfer "$TOKEN" "$NOSTONE_ID" 1 2)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=false reason=stone_insufficient from_level=0 to_level=0 gold_cost=0 target_bound=false)" = "ok" ]; then
  ok "没有转移石：200 + ok=false + stone_insufficient，未生效数值字段都是 0"
else
  bad "石头不足没被挡：$BODY"
fi
expect_state "$NOSTONE_ID" 1 2 5 1 true false 100000 0 "石头不足整笔回滚：两格等级、绑定、金币都没动"

level "$ID" 1 4
level "$ID" 11 0
GOLD_BEFORE="$(state_line "$ID" 1 11 | awk '{print $5}')"
STONE_BEFORE="$(state_line "$ID" 1 11 | awk '{print $6}')"
BEFORE="$(state_line "$ID" 1 11)"
add_gold "-$((GOLD_BEFORE - 50))" >/dev/null 2>&1
resp="$(transfer "$TOKEN" "$ID" 1 11)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=false reason=gold_insufficient from_level=0 to_level=0 gold_cost=0 target_bound=false stone_left=0)" = "ok" ]; then
  ok "金币不足：200 + ok=false + gold_insufficient，未生效数值字段都是 0"
else
  bad "金币不足没被挡：$BODY"
fi
AFTER="$(state_line "$ID" 1 11)"
if [ "$AFTER" = "$(printf '%s' "$BEFORE" | sed "s/ $GOLD_BEFORE / 50 /")" ] \
   && [ "$(printf '%s' "$AFTER" | awk '{print $6}')" = "$STONE_BEFORE" ]; then
  ok "金币不足整笔回滚：等级、绑定与转移石没动，目标没被误绑"
else
  bad "金币不足时状态不一致：before=[$BEFORE] after=[$AFTER]"
fi
add_gold "$((GOLD_BEFORE - 50))" >/dev/null 2>&1

echo "== 14. 伪造字段无效、事务号恰好一行 =="
level "$ID" 1 3
level "$ID" 11 0
FAKE=',"from_level":12,"to_level":12,"bound":true,"target_bound":true,"gold":999999,"gold_cost":0,"stone_item":"stone_enhance_low","stone_cost":99,"txn_id":"fake","account":"p14c_other","max_level":99,"duplicate":true'
resp="$(transfer "$TOKEN" "$ID" 1 11 "$FAKE")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true reason= from_level_before=3 to_level_before=0 from_level=0 to_level=3 max_level=12 gold_cost=190 target_bound_before=false target_bound=true duplicate=false)" = "ok" ]; then
  ok "伪造等级 / 绑定 / 金币 / 石头 / 事务号 / 账号 / 上限全无效"
else
  bad "伪造字段影响了结果：$BODY"
fi
LAST_TXN="$(grep -o 'txn=transfer:[0-9a-f]*' "$TMP/gateway.log" | tail -1 | cut -d= -f2)"
TXN_ROWS="$("$DOTNET" "$CHARACTER_DLL" --transfer-txn-count "$LAST_TXN" --config "$TMP/character.json" 2>/dev/null \
  | sed -n 's/^transfer_txn_count=\([0-9]*\) .*/\1/p')"
[ "$TXN_ROWS" = "1" ] && ok "这一笔的服务端事务号在库里恰好一行（防重放）" \
  || bad "事务号行数不对：txn=$LAST_TXN rows=$TXN_ROWS"

echo "== 15. P1-4a / P1-4b 老规则不回退 =="
resp="$(enhance "$TOKEN" "$ID" stone_enhance_mid)"
BODY="$(body_of "$resp")"
[ "$(code_of "$resp")" = "200" ] && [ "$(json_field "$BODY" reason)" = "wrong_stone" ] \
  && ok "P1-4a：低级档目标用中级石仍回 wrong_stone" || bad "三档分界回退了：$BODY"
level "$ID" 1 12
resp="$(enhance "$TOKEN" "$ID" stone_enhance_high)"
[ "$(json_field "$(body_of "$resp")" reason)" = "at_cap" ] \
  && ok "P1-4a：+12 仍然封顶" || bad "+12 没封顶：$(body_of "$resp")"
if restart_character "0,0,0" "" "$TMP/character4.log" && wait_line "$CHARACTER_LOG" 'rates=0,0,0'; then
  level "$ID" 1 4
  resp="$(enhance "$TOKEN" "$ID" stone_enhance_low ',"use_coupon":true')"
  BODY="$(body_of "$resp")"
  [ "$(expect_json "$BODY" ok=true success=false level_before=4 level=4 protected_level=true coupon_used=1 success_rate=0)" = "ok" ] \
    && ok "P1-4b：带券失败仍挡掉级、成功率与不带券同为 0" || bad "保护券回退了：$BODY"
else
  bad "第五次重启没读到新概率"
fi
r="$(can_trade 6)"
[ "$(json_field "$r" tradable)" = "false" ] && [ "$(json_field "$r" reason)" = "bound_item" ] \
  && ok "P1-4b / P1-1b3：保护券与拾取即绑装备仍不可交易" || bad "绑定判定回退了：$r"
r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"dungeon_id\":\"main\",\"party_size\":1}")"
[ "$(json_field "$r" allowed)" = "true" ] && ok "P1-2a：主线单人仍能进本" || bad "进本回退了：$r"
r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/exp/settle?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"txn_id\":\"p14c-exp-1\",\"source\":\"task\",\"party_size\":1}")"
[ "$(json_field "$r" level_cap)" = "100" ] && ok "P1-2d：经验结算仍按 level_cap=100" || bad "经验结算回退了：$r"
for _ in 1 2; do
  r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/grant?token=${TOKEN}" \
    -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"txn_id\":\"p14c-grant-repeat\",\"entries\":[{\"currency\":{\"kind\":\"gold\",\"delta\":1}}]}")"
done
[ "$(json_field "$r" duplicate)" = "true" ] && ok "P1-1b5：同一发奖事务号仍只发一次" || bad "发奖幂等回退了：$r"
r="$(add_gold -999999999)"
[ "$(code_of "$r")" != "200" ] && ok "P1-1b4：金币减成负数被拒" || bad "余额能写成负数：$(body_of "$r")"

finish "$TITLE"
