#!/usr/bin/env bash
# P1-3b 核对：采矿和采药。
#
# 完成标准（开发架构第 14 节第 33 行 + 第 11 节 P1-3 + 第 13 节 P1-3b + 方案 9.5）：
#   采一次矿背包出现矿石、采一次药背包出现药材、疲劳不变；
#   灵石矿脉采集被拒绝；成功的一笔由角色服入背包；数值只在配置里；
#   请求体带不进产物/数量/身份；节点占用与刷新由世界服判。
#
# 两种模式：
#   bash tools/check/check-p1-3b.sh                    服务端段：编译、静态、连库连服（要有 gamedb）
#   MMO_P13B_MODE=client bash tools/check/check-p1-3b.sh  客户端段：静态 + Unity 编辑器自检（要有 Unity）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
MODE="${MMO_P13B_MODE:-server}"

WORLD_DLL="$ROOT/src/World/bin/Release/net10.0/World.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"

WORLD_PORT=${MMO_P13B_WORLD_PORT:-63094}
CHAR_PORT=${MMO_P13B_CHAR_PORT:-62109}
GATEWAY_PORT=${MMO_P13B_GATEWAY_PORT:-61110}
ACCOUNT="p13b_user"
OTHER="p13b_other"
TEST_PASSWORD="p13b-Pw-7c19"
CHAR_NAMES=("p13b_a" "p13b_b" "p13b_c" "p13b_other")

PASS=0
FAIL=0
TMP="$(mktemp -d)"
WORLD_PID=""
CHARACTER_PID=""
GATEWAY_PID=""
HAVE_DB=""
TOKEN=""
OTHER_TOKEN=""
NEW_ID=""
LAST_TXN=""

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
  stop_pid "$WORLD_PID"
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
    if isinstance(v, (list, dict)):
        v = ",".join(str(x) for x in v)
    print(v)
except Exception:
    print("")'
}

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

create_char() {
  local resp
  resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"$2\",\"race\":\"mai\",\"class_id\":\"warrior\"}")"
  body_of "$resp" | grep -q 'character_id' || return 1
  [ "$(code_of "$resp")" = "200" ] || return 1
  NEW_ID="$(json_field "$(body_of "$resp")" character_id)"
  [ -n "$NEW_ID" ]
}

world_enter() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/world/enter?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2},\"region_id\":\"${3}\"}"
}

# 第 4 个参数是往请求体里塞的伪造字段，形如 ,"item_id":"stone_1"
gather() {
  local token="$1" char="$2" node="$3" extra="${4:-}"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/world/gather?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"node_id\":\"${node}\"${extra}}"
}

bag_count() {
  local item="$1" resp
  resp="$(curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/inventory?token=${TOKEN}&character_id=${IDS[0]}")"
  printf '%s' "$resp" | ITEM="$item" python3 -c 'import json,os,sys
try:
    d = json.load(sys.stdin)
    print(sum(s["count"] for s in d["slots"] if s["item_id"] == os.environ["ITEM"]))
except Exception:
    print("-1")'
}

currency_of() {
  local kind="$1" resp
  resp="$(curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/currency?token=${TOKEN}&character_id=${IDS[0]}")"
  printf '%s' "$resp" | KIND="$kind" python3 -c 'import json,os,sys
try:
    d = json.load(sys.stdin)
    print(sum(b["amount"] for b in d["balances"] if b["kind"] == os.environ["KIND"]))
except Exception:
    print("-1")'
}

fatigue_of() {
  "$DOTNET" "$CHARACTER_DLL" --fatigue-state "$1" --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^fatigue_state=ok .* fatigue=\([0-9-]*\) .*/\1/p'
}

finish() {
  echo
  echo "P1-3b（${MODE}）核对：$PASS 项通过，$FAIL 项未通过"
  [ "$FAIL" = "0" ] || exit 1
  exit 0
}

if [ "$MODE" = "client" ]; then
  echo "== C1. 配置、proto 与客户端静态断言 =="
  if ROOT="$ROOT" python3 - >"$TMP/client-static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/gather.json', encoding='utf-8'))
items = {i['id'] for i in json.load(open(root + '/config/items.json', encoding='utf-8'))['items']}
assert '数值待定' in cfg['note'], '产量与刷新秒数要注明待定'
by = {}
for n in cfg['nodes']:
    by.setdefault(n['kind'], []).append(n)
assert set(by) == {'ore', 'herb', 'spirit_stone'}, by.keys()
for n in by['ore']:
    assert n['item_id'] == 'ore_iron' and n['item_id'] in items
for n in by['herb']:
    assert n['item_id'] == 'herb_ling' and n['item_id'] in items
for kind in ('ore', 'herb'):
    for n in by[kind]:
        assert n['count'] > 0 and n['bag_slot'] > 0 and n['respawn_s'] > 0
for n in by['spirit_stone']:
    for field in ('item_id', 'count', 'bag_slot', 'respawn_s'):
        assert field not in n, field
proto = open(root + '/proto/mmo/world.proto', encoding='utf-8').read()
request = re.search(r'message GatherRequest \{(.*?)\}', proto, re.S).group(1)
# 按字段名比，不用裸子串：account 里也含 count 这几个字母。
fields = set(re.findall(r'(?:string|uint64|int32|uint32|bool)\s+(\w+)\s*=', request))
for field in ('item_id', 'count', 'bag_slot', 'txn_id', 'fatigue'):
    assert field not in fields, field
assert {'node_id', 'account', 'character_id'} <= fields
client = open(root + '/client/MMOGame/Assets/Net/WorldGather.cs', encoding='utf-8').read()
assert 'RequestBody(string nodeId)' in client and 'Escape(nodeId' in client
assert 'Ok = ok,' in client
assert 'GatherTable' not in client and 'ApplyGrant' not in client and 'Math.Sqrt' not in client
svc = open(root + '/src/World/WorldGrpcService.cs', encoding='utf-8').read()
assert 'spirit_stone_forbidden' in svc and 'nodes.TryClaim' in svc and 'grants.GrantAsync' in svc
print('client_static=ok nodes=' + str(len(cfg['nodes'])) + ' forbidden=' + str(len(by['spirit_stone'])))
PY
  then ok "采集数值只在配置、灵石矿脉没有产物、客户端只按回包展示"
  else bad "客户端静态断言失败"; cat "$TMP/client-static.out" | detail
  fi

  echo "== C2. Unity 编辑器自检：请求与回包拆装 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    finish
  fi
  if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod P13bCheck.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity P13bCheck.Run 退出码 0"
  else
    bad "Unity P13bCheck.Run 失败"
    tail -25 "$TMP/unity.log" | detail
  fi
  for key in config proto wire boundary; do
    if grep -q "p13b_${key}=ok" "$TMP/unity.log"; then
      ok "编辑器自检 p13b_${key}=ok"
    else
      bad "编辑器自检 p13b_${key} 没过"
      grep "p13b_${key}" "$TMP/unity.log" | tail -2 | detail
    fi
  done
  finish
fi

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  finish
fi
BUILD_OK=1
for proj in src/World/World.csproj src/Gateway/Gateway.csproj src/Character/Character.csproj src/Battle/Battle.csproj; do
  if ! "$DOTNET" build "$ROOT/$proj" -c Release --nologo -v q >"$TMP/build-$(basename "$proj" .csproj).log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E "error " "$TMP/build-$(basename "$proj" .csproj).log" | head -4 | detail
  fi
done
if [ "$BUILD_OK" = "1" ]; then ok "世界服、网关、角色服与战斗服编译通过"; else finish; fi

echo "== 2. 静态断言（服务端）=="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/gather.json', encoding='utf-8'))
items = {i['id'] for i in json.load(open(root + '/config/items.json', encoding='utf-8'))['items']}
assert '数值待定' in cfg['note']
by = {}
for n in cfg['nodes']:
    by.setdefault(n['kind'], []).append(n)
assert set(by) == {'ore', 'herb', 'spirit_stone'}
for n in by['ore'] + by['herb']:
    assert n['item_id'] in items and n['count'] > 0 and n['bag_slot'] > 0 and n['respawn_s'] > 0
for n in by['spirit_stone']:
    for field in ('item_id', 'count', 'bag_slot', 'respawn_s'):
        assert field not in n, field
proto = open(root + '/proto/mmo/world.proto', encoding='utf-8').read()
req = re.search(r'message GatherRequest \{(.*?)\}', proto, re.S).group(1)
fields = set(re.findall(r'(?:string|uint64|int32|uint32|bool)\s+(\w+)\s*=', req))
for field in ('item_id', 'count', 'bag_slot', 'txn_id', 'fatigue'):
    assert field not in fields, field
assert {'node_id', 'account', 'character_id'} <= fields
assert 'string node_id = 3' in req and 'uint64 character_id = 2' in req
assert 'rpc Gather(GatherRequest) returns (GatherResponse)' in proto
resp = re.search(r'message GatherResponse \{(.*?)\}', proto, re.S).group(1)
assert 'bool ok = 1' in resp and 'string reason' in resp and 'string txn_id' in resp
table = open(root + '/src/World/GatherTable.cs', encoding='utf-8').read()
assert '不许配产物或刷新字段' in table and '缺 kind=' in table
assert 'gather.json 节点 ' in table and '缺 item_id' in table
svc = open(root + '/src/World/WorldGrpcService.cs', encoding='utf-8').read()
assert 'GatherTable.SpiritStone' in svc and 'spirit_stone_forbidden' in svc
assert 'nodes.TryClaim' in svc and 'nodes.Release' in svc and 'grants.GrantAsync' in svc
assert 'Guid.NewGuid().ToString("N")' in svc
grant = open(root + '/src/World/GatherGrant.cs', encoding='utf-8').read()
assert 'ApplyGrantRequest' in grant and 'ItemDelta' in grant
assert 'Slot = (uint)node.BagSlot' in grant and 'Count = node.Count' in grant
api = open(root + '/src/Gateway/WorldApi.cs', encoding='utf-8').read()
assert 'public sealed record GatherBody(ulong? CharacterId, string? NodeId)' in api
assert 'OwnsAsync(characters, account, body.CharacterId.Value, context)' in api
assert 'GatherRequest' in api and 'NodeId = body.NodeId.Trim()' in api
proc = open(root + '/src/World/WorldProcess.cs', encoding='utf-8').read()
assert 'GatherTable.Load(' in proc and 'GrpcGatherGrant' in proc
for path in ['src/World/WorldProcess.cs', 'src/World/WorldGrpcService.cs',
             'src/World/GatherTable.cs', 'src/World/GatherGrant.cs',
             'src/World/GatherNodeRegistry.cs']:
    assert 'Npgsql' not in open(root + '/' + path, encoding='utf-8').read(), path
print('static_code=ok nodes=' + str(len(cfg['nodes']))
      + ' server_owns_gather=1 character_owns_bag=1 world_no_db=1')
PY
then ok "判定在世界服、产物交角色服、请求带不进产物、进程边界都落在指定文件里"
else bad "服务端静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 3. 采集配置：缺字段与越界都起不来 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
base = json.load(open(root + '/config/gather.json', encoding='utf-8'))
def dump(name, obj):
    json.dump(obj, open(tmp + '/' + name, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
w = copy.deepcopy(base); w['nodes'][0].pop('count'); dump('bad-missing-count.json', w)
w = copy.deepcopy(base); w['nodes'][0]['count'] = 0; dump('bad-zero-count.json', w)
w = copy.deepcopy(base); w['nodes'][-1]['item_id'] = 'stone_1'; dump('bad-spirit-item.json', w)
w = copy.deepcopy(base); w['nodes'] = [n for n in w['nodes'] if n['kind'] != 'spirit_stone']; dump('bad-no-spirit.json', w)
w = copy.deepcopy(base); w['nodes'][1]['id'] = w['nodes'][0]['id']; dump('bad-dup-id.json', w)
w = copy.deepcopy(base); w.pop('character_upstream'); dump('bad-no-upstream.json', w)
PY
config_reject() {
  local label="$1" file="$2" expect="$3" out
  out="$("$DOTNET" "$WORLD_DLL" --gather-state --gather-config "$TMP/$file" 2>&1)"
  if printf '%s' "$out" | grep -qF "config_error=$expect"; then
    ok "$label"
  else
    bad "$label：${out:0:150}"
  fi
}
config_reject "缺 count 起不来" bad-missing-count.json "gather.json 节点 ore_kulan_1 缺 count"
config_reject "count 非正起不来" bad-zero-count.json "gather.json 节点 ore_kulan_1 的 count 必须大于 0，现在是 0"
config_reject "灵石矿脉配了产物起不来" bad-spirit-item.json "gather.json 的灵石矿脉 spirit_qiyun_1 不许配产物或刷新字段"
config_reject "删掉灵石矿脉起不来（不许绕开规则）" bad-no-spirit.json "gather.json 缺 kind=spirit_stone 的节点"
config_reject "节点编号重复起不来" bad-dup-id.json "gather.json 里有重复节点编号 ore_kulan_1"
config_reject "缺角色服上游起不来" bad-no-upstream.json "gather.json 的 character_upstream 必须是 http(s) 地址："
STATE="$("$DOTNET" "$WORLD_DLL" --gather-state --config config/world.json 2>&1)"
if printf '%s' "$STATE" | grep -qF 'gather_state=ok nodes=4 ore=ore_iron:2 herb=herb_ling:3 respawn_s=2 forbidden=1'; then
  ok "只读入口读到配置：矿石 2、药材 3、刷新 2 秒、灵石矿脉 1 个"
else
  bad "只读入口输出不对：${STATE:0:150}"
fi

echo "== 4. 环境与启动 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 gamedb 凭据，连服段跑不了（这一段要在笔记本上跑）"
  finish
fi
if ! "$DOTNET" "$GATEWAY_DLL" --migrate --sql db/character/account.sql --config config/gateway.json >"$TMP/migrate1.log" 2>&1; then
  bad "账号表建表失败"; tail -8 "$TMP/migrate1.log" | detail; finish
fi
if ! "$DOTNET" "$CHARACTER_DLL" --migrate --config config/character.json >"$TMP/migrate2.log" 2>&1; then
  bad "角色表建表失败"; tail -8 "$TMP/migrate2.log" | detail; finish
fi
HAVE_DB=1
for acct in "$ACCOUNT" "$OTHER"; do
  printf '%s\n' "$TEST_PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$acct" --config config/gateway.json >>"$TMP/seed.log" 2>&1
done
ok "账号表、角色表就绪，两个测试账号已播种"

cp "$ROOT/config/dungeons.json" "$TMP/dungeons.json"
cp "$ROOT/config/exp.json" "$TMP/exp.json"
ROOT="$ROOT" TMP="$TMP" WORLD_PORT="$WORLD_PORT" CHAR_PORT="$CHAR_PORT" GATEWAY_PORT="$GATEWAY_PORT" python3 - <<'PY'
import json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
w = json.load(open(root + '/config/world.json', encoding='utf-8'))
w['listen'] = 'http://127.0.0.1:' + os.environ['WORLD_PORT']
w['region_dir'] = 'config/region'
w['dialogue_dir'] = 'config/dialogue'
json.dump(w, open(tmp + '/world.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
g = json.load(open(root + '/config/gather.json', encoding='utf-8'))
g['character_upstream'] = 'http://127.0.0.1:' + os.environ['CHAR_PORT']
json.dump(g, open(tmp + '/gather.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
c = {
    'schema': 'character-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'db_pool_max': 6, 'bag_slots': 40,
    'dungeon_table': tmp + '/dungeons.json', 'exp_table': tmp + '/exp.json',
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

"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & CHARACTER_PID=$!
"$DOTNET" "$WORLD_DLL" --config "$TMP/world.json" --gather-config "$TMP/gather.json" >"$TMP/world.log" 2>&1 & WORLD_PID=$!
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >"$TMP/gateway.log" 2>&1 & GATEWAY_PID=$!

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
if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
   && wait_ready "$TMP/world.log" '^ready name=World' "世界服" \
   && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关"; then
  ok "角色服、世界服、网关三个进程都起来"
else
  finish
fi
if grep -qF 'gather_config=ok nodes=4 kinds=ore,herb,spirit_stone forbidden=1' "$TMP/world.log" \
   && wait_line "$TMP/world.log" '^position_sync_hz=8$' \
   && grep -qF 'tier_config=ok near=8 far=20' "$TMP/world.log"; then
  ok "世界服读到四个采集节点，老的 8Hz 与三档阈值没回退"
else
  bad "世界服启动日志里的采集/老配置不对"; grep -E '^(gather_|tier_|position_)' "$TMP/world.log" | detail
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then ok "两个账号都拿到令牌"; else bad "登录失败"; finish; fi
IDS=()
for name in p13b_a p13b_b p13b_c; do
  if create_char "$TOKEN" "$name"; then IDS+=("$NEW_ID"); else bad "建角色失败：$name"; fi
done
if create_char "$OTHER_TOKEN" "p13b_other"; then OTHER_ID="$NEW_ID"; else bad "建别人账号的角色失败"; fi
[ "${#IDS[@]}" = "3" ] && [ -n "${OTHER_ID:-}" ] || finish
ok "主账号三个角色、别人账号一个角色就绪（${IDS[*]} / $OTHER_ID）"

resp="$(world_enter "$TOKEN" "${IDS[0]}" "枯岚道")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(json_field "$(body_of "$resp")" line_id)" = "枯岚道#1" ]; then
  ok "A 进枯岚道#1"
else
  bad "A 进线失败：$(body_of "$resp")"; finish
fi

# 先让疲劳不是 0，采完之后才能证明「疲劳不变」不是拿空值糊弄过去。
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]},\"dungeon_id\":\"abyss\",\"party_size\":4}")"
if [ "$(json_field "$(body_of "$resp")" allowed)" = "True" ] \
   && [ "$(json_field "$(body_of "$resp")" fatigue)" = "6000" ]; then
  ok "A 先进一次 4 人深渊，疲劳到 6000（采集前的基线）"
else
  bad "疲劳基线没建立：$(body_of "$resp")"; finish
fi
FATIGUE_BEFORE="$(fatigue_of "${IDS[0]}")"
[ "$FATIGUE_BEFORE" = "6000" ] && ok "角色服只读入口读到 fatigue=6000" \
  || bad "疲劳基线读不出来：${FATIGUE_BEFORE:-空}"

echo "== 5. 网关鉴权与归属复核 =="
resp="$(gather "" "${IDS[0]}" ore_kulan_1)"
[ "$(code_of "$resp")" = "401" ] && ok "没有令牌 401" || bad "没有令牌没被挡：$(code_of "$resp")"
resp="$(gather forged "${IDS[0]}" ore_kulan_1)"
[ "$(code_of "$resp")" = "401" ] && ok "伪造令牌 401" || bad "伪造令牌没被挡：$(code_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/world/gather?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]}}")"
[ "$(code_of "$resp")" = "400" ] && ok "缺 node_id 400" || bad "缺节点编号没被挡：$(code_of "$resp")"
resp="$(gather "$TOKEN" "$OTHER_ID" ore_kulan_1)"
[ "$(code_of "$resp")" = "404" ] && ok "别人的角色 404（角色服复核归属）" || bad "别人的角色没被挡：$(code_of "$resp")"

echo "== 6. 采矿：背包出现矿石，疲劳不变 =="
resp="$(gather "$TOKEN" "${IDS[0]}" ore_kulan_1)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(json_field "$BODY" ok)" = "True" ] \
   && [ "$(json_field "$BODY" item_id)" = "ore_iron" ] \
   && [ "$(json_field "$BODY" count)" = "2" ] \
   && [ "$(json_field "$BODY" bag_slot)" = "1" ] \
   && [ "$(json_field "$BODY" kind)" = "ore" ]; then
  ok "采一次矿：回包是矿石 2 个、落在第 1 格（配置里的数值）"
else
  bad "采矿回包不对：$BODY"
fi
LAST_TXN="$(json_field "$BODY" txn_id)"
[ -n "$LAST_TXN" ] && ok "这一笔带角色服事务号 $LAST_TXN" || bad "回包没有事务号"
[ "$(bag_count ore_iron)" = "2" ] && ok "背包里出现 ore_iron ×2" || bad "背包没进矿石：$(bag_count ore_iron)"
[ "$(fatigue_of "${IDS[0]}")" = "6000" ] && ok "采矿后疲劳仍是 6000" || bad "采矿动了疲劳：$(fatigue_of "${IDS[0]}")"

echo "== 7. 灵石矿脉：个人采集被拒绝 =="
resp="$(gather "$TOKEN" "${IDS[0]}" spirit_qiyun_1)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(json_field "$BODY" ok)" = "False" ] \
   && [ "$(json_field "$BODY" reason)" = "spirit_stone_forbidden" ] \
   && [ "$(json_field "$BODY" item_id)" = "" ] \
   && [ "$(json_field "$BODY" count)" = "0" ] \
   && [ "$(json_field "$BODY" txn_id)" = "" ]; then
  ok "灵石矿脉被拒：reason=spirit_stone_forbidden，未生效的产物字段都是 0 / 空"
else
  bad "灵石矿脉没按预期被拒：$BODY"
fi
[ "$(bag_count ore_iron)" = "2" ] && ok "被拒后背包没有多出东西" || bad "被拒后背包变了：$(bag_count ore_iron)"
[ "$(currency_of stone_1)" = "0" ] && ok "灵石一档余额仍是 0，没从矿点进个人背包" \
  || bad "灵石余额被改了：$(currency_of stone_1)"

echo "== 8. 采药：背包出现药材，疲劳仍不变 =="
resp="$(world_enter "$TOKEN" "${IDS[0]}" "听谣镇")"
[ "$(json_field "$(body_of "$resp")" line_id)" = "听谣镇#1" ] \
  && ok "A 换到听谣镇（节点长在这块地表上）" || bad "换区失败：$(body_of "$resp")"
resp="$(gather "$TOKEN" "${IDS[0]}" herb_tingyao_1)"
BODY="$(body_of "$resp")"
if [ "$(json_field "$BODY" ok)" = "True" ] \
   && [ "$(json_field "$BODY" item_id)" = "herb_ling" ] \
   && [ "$(json_field "$BODY" count)" = "3" ] \
   && [ "$(json_field "$BODY" bag_slot)" = "2" ]; then
  ok "采一次药：回包是药材 3 个、落在第 2 格"
else
  bad "采药回包不对：$BODY"
fi
[ "$(bag_count herb_ling)" = "3" ] && ok "背包里出现 herb_ling ×3" || bad "背包没进药材：$(bag_count herb_ling)"
[ "$(fatigue_of "${IDS[0]}")" = "6000" ] && ok "采药后疲劳仍是 6000" || bad "采药动了疲劳：$(fatigue_of "${IDS[0]}")"

echo "== 9. 节点占用、区域与改包 =="
resp="$(gather "$TOKEN" "${IDS[0]}" ore_kulan_2)"
[ "$(json_field "$(body_of "$resp")" reason)" = "wrong_region" ] \
  && ok "不在节点所在地表时回 wrong_region" || bad "跨区采集没被挡：$(body_of "$resp")"
world_enter "$TOKEN" "${IDS[0]}" "枯岚道" >/dev/null 2>&1
resp="$(gather "$TOKEN" "${IDS[0]}" ore_kulan_2)"
[ "$(json_field "$(body_of "$resp")" ok)" = "True" ] \
  && ok "回到枯岚道后第二个矿点采成功" || bad "第二个矿点失败：$(body_of "$resp")"
resp="$(gather "$TOKEN" "${IDS[0]}" ore_kulan_2)"
if [ "$(json_field "$(body_of "$resp")" ok)" = "False" ] \
   && [ "$(json_field "$(body_of "$resp")" reason)" = "node_busy" ]; then
  ok "刷新时间没到再采回 node_busy（占用只在世界服内存）"
else
  bad "节点占用没生效：$(body_of "$resp")"
fi
[ "$(bag_count ore_iron)" = "4" ] && ok "被占的第二次没再发一份，背包仍是 ×4" \
  || bad "重复采集发了奖：$(bag_count ore_iron)"

sleep 2.2   # 等过配置里的 respawn_s=2，再用同一个节点做改包核对
resp="$(gather "$TOKEN" "${IDS[0]}" ore_kulan_1 \
  ',"item_id":"stone_1","count":9999,"bag_slot":9,"txn_id":"forged","account":"other","kind":"spirit_stone","fatigue_cost":-999')"
BODY="$(body_of "$resp")"
if [ "$(json_field "$BODY" ok)" = "True" ] \
   && [ "$(json_field "$BODY" item_id)" = "ore_iron" ] \
   && [ "$(json_field "$BODY" count)" = "2" ] \
   && [ "$(json_field "$BODY" bag_slot)" = "1" ] \
   && [ "$(json_field "$BODY" txn_id)" != "forged" ]; then
  ok "请求体塞产物、数量、格子、事务号、账号都无效，仍按配置发矿石 2 个"
else
  bad "改包影响了产物：$BODY"
fi
LAST_TXN="$(json_field "$BODY" txn_id)"
[ "$(bag_count ore_iron)" = "6" ] && ok "背包叠加到 ×6，没有凭空多出伪造数量" \
  || bad "改包后背包数量不对：$(bag_count ore_iron)"
[ "$(currency_of stone_1)" = "0" ] && ok "伪造的灵石没有进货币" || bad "灵石被改包写进去了"
resp="$(gather "$TOKEN" "${IDS[0]}" node_not_there)"
[ "$(json_field "$(body_of "$resp")" reason)" = "unknown_node" ] \
  && ok "未知节点回 unknown_node" || bad "未知节点没被挡：$(body_of "$resp")"
resp="$(gather "$TOKEN" "${IDS[1]}" ore_kulan_1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "not_in_line" ] \
  && ok "没进分线的人回 not_in_line" || bad "没进线没被挡：$(body_of "$resp")"

echo "== 10. 发奖事务号不可复用 =="
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/grant?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]},\"txn_id\":\"${LAST_TXN}\",\"entries\":[{\"item\":{\"slot\":1,\"item_id\":\"ore_iron\",\"count\":1}}]}")"
if [ "$(json_field "$(body_of "$resp")" duplicate)" = "True" ] \
   && [ "$(bag_count ore_iron)" = "6" ]; then
  ok "同一个事务号再提交回 duplicate，背包仍是 ×6"
else
  bad "事务号被复用：$(body_of "$resp") / $(bag_count ore_iron)"
fi
TXN_ROWS="$("$DOTNET" "$CHARACTER_DLL" --txn-count "$LAST_TXN" --config "$TMP/character.json" 2>/dev/null \
  | sed -n 's/^txn_count=\([0-9]*\) .*/\1/p')"
[ "$TXN_ROWS" = "1" ] && ok "角色库 grant_txn 里这个号只有一行" || bad "事务号行数不对：${TXN_ROWS:-空}"

echo "== 11. 改配置重启：产量与刷新跟着变 =="
stop_pid "$WORLD_PID"
WORLD_PID=""
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
p = os.environ['TMP'] + '/gather.json'
g = json.load(open(p, encoding='utf-8'))
for n in g['nodes']:
    if n['kind'] == 'ore':
        n['count'] = 7
        n['respawn_s'] = 1
json.dump(g, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
STATE="$("$DOTNET" "$WORLD_DLL" --gather-state --gather-config "$TMP/gather.json" 2>&1)"
if printf '%s' "$STATE" | grep -qF 'ore=ore_iron:7' && printf '%s' "$STATE" | grep -qF 'respawn_s=1'; then
  ok "只读入口读到新产量 7 与刷新 1 秒（代码没写第二套数字）"
else
  bad "改配置没被读到：${STATE:0:150}"
fi
"$DOTNET" "$WORLD_DLL" --config "$TMP/world.json" --gather-config "$TMP/gather.json" >"$TMP/world2.log" 2>&1 & WORLD_PID=$!
if wait_ready "$TMP/world2.log" '^ready name=World' "重启后的世界服"; then ok "改配置后世界服重启成功"; else finish; fi
# 世界服重启后在线状态在内存里丢了，重新进线再采。
resp="$(world_enter "$TOKEN" "${IDS[0]}" "枯岚道")"
[ "$(json_field "$(body_of "$resp")" line_id)" = "枯岚道#1" ] \
  && ok "重启后重新进线" || bad "重启后进线失败：$(body_of "$resp")"
resp="$(gather "$TOKEN" "${IDS[0]}" ore_kulan_1)"
if [ "$(json_field "$(body_of "$resp")" ok)" = "True" ] \
   && [ "$(json_field "$(body_of "$resp")" count)" = "7" ] \
   && [ "$(json_field "$(body_of "$resp")" respawn_s)" = "1" ] \
   && [ "$(bag_count ore_iron)" = "13" ]; then
  ok "同一个节点按新配置发 7 个，背包 ×6 变 ×13，疲劳仍不改"
else
  bad "改配置后采集不对：$(body_of "$resp") / $(bag_count ore_iron)"
fi
[ "$(fatigue_of "${IDS[0]}")" = "6000" ] && ok "整段采集下来疲劳始终 6000" \
  || bad "疲劳被改了：$(fatigue_of "${IDS[0]}")"

echo "== 12. 老规则不回退 =="
resp="$(curl -s -m 10 -w '\n%{http_code}' "http://127.0.0.1:${GATEWAY_PORT}/world/regions?token=${TOKEN}")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(printf '%s' "$(body_of "$resp")" | python3 -c 'import json,sys; print(len(json.load(sys.stdin)["regions"]))')" = "8" ]; then
  ok "P1-3a1 的 /world/regions 仍回八块地表编号"
else
  bad "/world/regions 回包变了：$(body_of "$resp")"
fi
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]},\"dungeon_id\":\"main\",\"party_size\":1}")"
if [ "$(json_field "$(body_of "$resp")" allowed)" = "True" ] \
   && [ "$(json_field "$(body_of "$resp")" fatigue_cost)" = "0" ]; then
  ok "P1-2a 主线单人进本仍 allowed、不扣疲劳"
else
  bad "主线进本规则被改了：$(body_of "$resp")"
fi
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/exp/settle?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]},\"txn_id\":\"p13b-oldrules-1\",\"party_size\":1,\"source\":\"dungeon\"}")"
if [ "$(json_field "$(body_of "$resp")" exp_gain)" = "120" ] \
   && [ "$(json_field "$(body_of "$resp")" party_bonus)" = "0" ]; then
  ok "P1-2d 单人经验仍是 120、没有组队加成"
else
  bad "经验结算规则被改了：$(body_of "$resp")"
fi
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/hot-spring?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]}}")"
if [ "$(json_field "$(body_of "$resp")" recovered)" = "1500" ]; then
  ok "P1-2b2 温泉仍只恢复 1500（采集没有动疲劳口径）"
else
  bad "温泉规则被改了：$(body_of "$resp")"
fi

finish
