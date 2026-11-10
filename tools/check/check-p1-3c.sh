#!/usr/bin/env bash
# P1-3c 核对：飞行和跨区（开发架构第 14 节第 34 行）。
#
# 完成标准（第 11 节 P1-3 + 第 13 节 P1-3c + 方案 02 / 8.1 / 12.1）：
#   没有飞行权限时，客户端发起飞请求会被拉回地面。
#   展开：飞请求回 flight_forbidden、y 回到地面、位置帧改高 y 也不起飞；
#   跨区按境界门槛拉回、被拉回时仍在原区域原分线；改配置重启生效；
#   请求带不进境界/权限/身份；老的位置同步、三档、采集与疲劳规则不回退。
#
# 两种模式：
#   bash tools/check/check-p1-3c.sh                          服务端段：编译、静态、连库连服（要有 gamedb）
#   MMO_P13C_MODE=client bash tools/check/check-p1-3c.sh    客户端段：静态 + Unity 编辑器自检（要有 Unity）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
MODE="${MMO_P13C_MODE:-server}"

WORLD_DLL="$ROOT/src/World/bin/Release/net10.0/World.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
PROBE_DLL="$ROOT/tools/probe/WorldProbe/bin/Release/net10.0/WorldProbe.dll"

WORLD_PORT=${MMO_P13C_WORLD_PORT:-63095}
CHAR_PORT=${MMO_P13C_CHAR_PORT:-62110}
GATEWAY_PORT=${MMO_P13C_GATEWAY_PORT:-61111}
ACCOUNT="p13c_user"
OTHER="p13c_other"
TEST_PASSWORD="p13c-Pw-7c19"
CHAR_NAMES=("p13c_a" "p13c_b" "p13c_other")

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
    if isinstance(v, bool):
        v = str(v).lower()
    print(v)
except Exception:
    print("")'
}

# 一次比对回包里多个字段。数值按 float 比，bool 按 true/false 比。
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
    if isinstance(got, bool):
        got = "true" if got else "false"
    try:
        if float(got) == float(want):
            continue
    except (TypeError, ValueError):
        pass
    if str(got) == want:
        continue
    print("mismatch %s got=%r want=%r" % (key, got, want))
    sys.exit(1)
print("ok")'
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

# $4 是往请求体里塞的伪造字段，形如 ,"realm":"结丹"
fly() {
  local token="$1" char="$2" want="$3" extra="${4:-}"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/world/fly?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"want_fly\":${want}${extra}}"
}

region() {
  local token="$1" char="$2" target="$3" extra="${4:-}"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/world/region?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"target_region\":\"${target}\"${extra}}"
}

fatigue_of() {
  "$DOTNET" "$CHARACTER_DLL" --fatigue-state "$1" --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^fatigue_state=ok .* fatigue=\([0-9-]*\) .*/\1/p'
}

probe() {
  "$DOTNET" "$PROBE_DLL" \
    --base "ws://127.0.0.1:${GATEWAY_PORT}/world/stream" \
    --http "http://127.0.0.1:${GATEWAY_PORT}" \
    --token-a "$TOKEN" --token-b "$TOKEN" \
    --char-a "${IDS[0]}" --char-b "${IDS[1]}" \
    --case flight_frame 2>&1
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

finish() {
  echo
  echo "P1-3c（${MODE}）核对：$PASS 项通过，$FAIL 项未通过"
  [ "$FAIL" = "0" ] || exit 1
  exit 0
}

# ---------------------------------------------------------------- 客户端段
if [ "$MODE" = "client" ]; then
  echo "== C1. 配置、proto 与客户端静态断言 =="
  if ROOT="$ROOT" python3 - >"$TMP/client-static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/flight.json', encoding='utf-8'))
world = json.load(open(root + '/config/world.json', encoding='utf-8'))
assert '数值待定' in cfg['note'], '门槛要注明待定'
assert cfg['realms'][:3] == ['炼体', '筑基', '结丹'], cfg['realms']
assert cfg['flight_realm'] in cfg['realms']
gates = {g['region_id']: g['realm'] for g in cfg['region_realms']}
assert set(gates) == set(world['regions']), set(world['regions']) ^ set(gates)
assert all(v in cfg['realms'] for v in gates.values())
assert 'spawn_y' in cfg['note'] and 'max_move_speed' in cfg['note'], '地面高度与速度不另写一套'
proto = open(root + '/proto/mmo/world.proto', encoding='utf-8').read()
cproto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
def fields(msg):
    body = re.search(r'message ' + msg + r' \{(.*?)\}', proto, re.S).group(1)
    return set(re.findall(r'(?:string|uint64|int32|uint32|bool)\s+(\w+)\s*=', body))
fly = fields('FlyRequest')
cross = fields('CrossRegionRequest')
assert fly == {'account', 'character_id', 'want_fly'}, fly
assert cross == {'account', 'character_id', 'target_region'}, cross
assert 'rpc Fly(FlyRequest) returns (FlyResponse)' in proto
assert 'rpc CrossRegion(CrossRegionRequest) returns (CrossRegionResponse)' in proto
assert 'bool flying = 9;' in proto and 'bool can_fly = 10;' in proto
assert 'bool self_flying = 7;' in proto and 'bool self_can_fly = 8;' in proto
assert 'string realm = 4;' in cproto
client = open(root + '/client/MMOGame/Assets/Net/WorldFlight.cs', encoding='utf-8').read()
assert 'FlyRequestBody(bool wantFly)' in client and 'RegionRequestBody(string targetRegion)' in client
assert 'TrailAllowed => Flying' in client, '拖尾只跟服务端判完的 flying'
assert 'FlightTable' not in client and 'FlightGate' not in client and 'Math.Sqrt' not in client
print('client_static=ok realms=' + str(len(cfg['realms'])) + ' gates=' + str(len(gates)))
PY
  then ok "境界顺序、门槛覆盖八块地表、请求带不进权限、拖尾只跟服务端"
  else bad "客户端静态断言失败"; cat "$TMP/client-static.out" | detail
  fi

  echo "== C2. Unity 编辑器自检：飞请求与拉回回包拆装 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    finish
  fi
  if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod P13cCheck.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity P13cCheck.Run 退出码 0"
  else
    bad "Unity P13cCheck.Run 失败"
    tail -25 "$TMP/unity.log" | detail
  fi
  for key in config proto wire boundary; do
    if grep -q "p13c_${key}=ok" "$TMP/unity.log"; then
      ok "编辑器自检 p13c_${key}=ok"
    else
      bad "编辑器自检 p13c_${key} 没过"
      grep "p13c_${key}" "$TMP/unity.log" | tail -2 | detail
    fi
  done
  finish
fi

# ---------------------------------------------------------------- 服务端段
echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then bad "找不到 dotnet"; finish; fi
BUILD_OK=1
for proj in src/World/World.csproj src/Gateway/Gateway.csproj src/Character/Character.csproj src/Battle/Battle.csproj tools/probe/WorldProbe/WorldProbe.csproj; do
  if ! "$DOTNET" build "$ROOT/$proj" -c Release --nologo -v q >"$TMP/build-$(basename "$proj" .csproj).log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E "error " "$TMP/build-$(basename "$proj" .csproj).log" | head -4 | detail
  fi
done
if [ "$BUILD_OK" = "1" ]; then ok "世界服、网关、角色服、战斗服与位置探针编译通过"; else finish; fi

echo "== 2. 静态断言（服务端）=="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/flight.json', encoding='utf-8'))
assert cfg['realms'][:3] == ['炼体', '筑基', '结丹']
assert '数值待定' in cfg['note']
proto = open(root + '/proto/mmo/world.proto', encoding='utf-8').read()
cproto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
def fields(msg, text=proto):
    body = re.search(r'message ' + msg + r' \{(.*?)\}', text, re.S).group(1)
    return set(re.findall(r'(?:string|uint64|int32|uint32|bool)\s+(\w+)\s*=', body))
assert fields('FlyRequest') == {'account', 'character_id', 'want_fly'}
assert fields('CrossRegionRequest') == {'account', 'character_id', 'target_region'}
assert 'string realm' in re.search(r'message ListInventoryResponse \{(.*?)\}', cproto, re.S).group(1)
sql = open(root + '/db/character/character.sql', encoding='utf-8').read()
assert "realm       text        NOT NULL DEFAULT '炼体'" in sql, '境界默认炼体由库给，不写在代码里'
gate = open(root + '/src/World/FlightGate.cs', encoding='utf-8').read()
assert 'realms.ReadAsync' in gate and 'registry.SetFlight' in gate
assert 'region_realm_locked' in gate and 'region_locked' in gate
assert 'catalog.Find' in gate and 'target.Neighbors' in gate
table = open(root + '/src/World/FlightTable.cs', encoding='utf-8').read()
assert 'WrittenRealms' in table and '缺地点' in table and '方案 8.1' in table
reader = open(root + '/src/World/RealmReader.cs', encoding='utf-8').read()
assert 'ListInventoryAsync' in reader and 'reply.Realm' in reader
reg = open(root + '/src/World/LineRegistry.cs', encoding='utf-8').read()
assert 'flight_forbidden' in reg and 'public float GroundY' in reg
svc = open(root + '/src/World/WorldGrpcService.cs', encoding='utf-8').read()
assert 'flight.FlyAsync' in svc and 'flight.CrossAsync' in svc
hub = open(root + '/src/World/PositionHub.cs', encoding='utf-8').read()
assert 'SelfFlying' in hub and 'Flying = state.Flying' in hub
api = open(root + '/src/Gateway/WorldApi.cs', encoding='utf-8').read()
assert 'FlyBody(ulong? CharacterId, bool? WantFly)' in api
assert 'CrossRegionBody(ulong? CharacterId, string? TargetRegion)' in api
assert api.count('OwnsAsync(characters, account, body.CharacterId.Value, context)') >= 2
proc = open(root + '/src/World/WorldProcess.cs', encoding='utf-8').read()
assert 'FlightTable.Load(' in proc and 'GrpcRealmReader' in proc and 'flight_config=ok' in proc
inv = open(root + '/src/Character/InventoryRepository.cs', encoding='utf-8').read()
assert 'select realm from' in inv and 'RealmAsync' in inv
grpc = open(root + '/src/Character/CharacterGrpcService.cs', encoding='utf-8').read()
assert 'Realm = realm ?? ""' in grpc
for path in ['src/World/WorldProcess.cs', 'src/World/WorldGrpcService.cs',
             'src/World/FlightGate.cs', 'src/World/FlightTable.cs',
             'src/World/RealmReader.cs', 'src/World/LineRegistry.cs']:
    assert 'Npgsql' not in open(root + '/' + path, encoding='utf-8').read(), path
print('static_code=ok character_owns_realm=1 world_no_db=1 gate_server_side=1')
PY
then ok "境界只在角色服、判定只在世界服、请求带不进权限、进程边界都落在指定文件里"
else bad "服务端静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 3. 飞行配置：缺字段与越界都起不来 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
base = json.load(open(root + '/config/flight.json', encoding='utf-8'))
def dump(name, obj):
    json.dump(obj, open(tmp + '/' + name, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
w = copy.deepcopy(base); w.pop('flight_realm'); dump('bad-no-flight-realm.json', w)
w = copy.deepcopy(base); w['flight_realm'] = '元婴'; dump('bad-flight-realm.json', w)
w = copy.deepcopy(base); w['realms'] = ['筑基', '炼体', '结丹']; dump('bad-realm-order.json', w)
w = copy.deepcopy(base); w['region_realms'][0]['realm'] = '大乘'; dump('bad-gate-realm.json', w)
w = copy.deepcopy(base); w['region_realms'] = w['region_realms'][:-1]; dump('bad-missing-gate.json', w)
w = copy.deepcopy(base); w['region_realms'][0]['region_id'] = '不存在的地表'; dump('bad-unknown-region.json', w)
w = copy.deepcopy(base); w.pop('character_upstream'); dump('bad-no-upstream.json', w)
PY
config_reject() {
  local label="$1" file="$2" expect="$3" out
  out="$("$DOTNET" "$WORLD_DLL" --flight-state --config config/world.json --flight-config "$TMP/$file" 2>&1)"
  if printf '%s' "$out" | grep -qF "$expect"; then
    ok "$label"
  else
    bad "$label：${out:0:150}"
  fi
}
config_reject "缺 flight_realm 起不来" bad-no-flight-realm.json "config_error=flight.json 的 flight_realm 不在 realms 里："
config_reject "飞行门槛不在境界顺序表里起不来" bad-flight-realm.json "config_error=flight.json 的 flight_realm 不在 realms 里：元婴"
config_reject "打乱方案 8.1 的境界顺序起不来" bad-realm-order.json "config_error=flight.json 的 realms 必须按方案 8.1 以 炼体,筑基,结丹 开头"
config_reject "区域门槛填了不认识的境界起不来" bad-gate-realm.json "config_error=flight.json 的地点 枯岚道 门槛境界不在 realms 里：大乘"
# HashSet 遍历顺序不定，只比对报错前缀，不写死是哪一块地表。
config_reject "漏配一块地表的门槛起不来" bad-missing-gate.json "config_error=flight.json 缺地点 "
config_reject "门槛写了不存在的地点起不来" bad-unknown-region.json "config_error=flight.json 的地点不在 world.json 里：不存在的地表"
config_reject "缺角色服上游起不来" bad-no-upstream.json "config_error=flight.json 的 character_upstream 必须是 http(s) 地址："
STATE="$("$DOTNET" "$WORLD_DLL" --flight-state --config config/world.json 2>&1)"
if printf '%s' "$STATE" | grep -qF 'flight_state=ok realms=炼体,筑基,结丹 flight_realm=筑基 gates=8' \
   && printf '%s' "$STATE" | grep -qF 'region=龙巢门口 realm=结丹'; then
  ok "只读入口读到境界顺序、飞行门槛与龙巢门口的结丹门槛"
else
  bad "只读入口输出不对：${STATE:0:180}"
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
cp "$ROOT/config/gather.json" "$TMP/gather.json"
cp "$ROOT/config/flight.json" "$TMP/flight.json"
ROOT="$ROOT" TMP="$TMP" WORLD_PORT="$WORLD_PORT" CHAR_PORT="$CHAR_PORT" GATEWAY_PORT="$GATEWAY_PORT" python3 - <<'PY'
import json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
def rw(name, fn):
    d = json.load(open(tmp + '/' + name, encoding='utf-8'))
    fn(d)
    json.dump(d, open(tmp + '/' + name, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
w = json.load(open(root + '/config/world.json', encoding='utf-8'))
w['listen'] = 'http://127.0.0.1:' + os.environ['WORLD_PORT']
w['region_dir'] = 'config/region'
w['dialogue_dir'] = 'config/dialogue'
json.dump(w, open(tmp + '/world.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
up = 'http://127.0.0.1:' + os.environ['CHAR_PORT']
rw('gather.json', lambda d: d.update(character_upstream=up))
rw('flight.json', lambda d: d.update(character_upstream=up))
c = {
    'schema': 'character-config/1',
    'listen': up, 'db_pool_max': 6, 'bag_slots': 40,
    'dungeon_table': tmp + '/dungeons.json', 'exp_table': tmp + '/exp.json',
}
json.dump(c, open(tmp + '/character.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
a = {
    'schema': 'gateway-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['GATEWAY_PORT'],
    'token_ttl_s': 7200, 'pbkdf2_iterations': 210000,
    'battle_upstream': 'ws://127.0.0.1:60001/battle',
    'character_upstream': up,
    'world_upstream': 'http://127.0.0.1:' + os.environ['WORLD_PORT'],
}
json.dump(a, open(tmp + '/gateway.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY

# 每次重启换一份日志，wait_ready 才不会命中上一次的 ready 行。
WORLD_LOG="$TMP/world.log"
start_world() {
  [ "$#" -eq 0 ] || WORLD_LOG="$1"
  "$DOTNET" "$WORLD_DLL" --config "$TMP/world.json" \
    --gather-config "$TMP/gather.json" --flight-config "$TMP/flight.json" \
    >"$WORLD_LOG" 2>&1 & WORLD_PID=$!
}

wait_line() {
  local log="$1" pattern="$2"
  for _ in $(seq 1 40); do
    if grep -q "$pattern" "$log" 2>/dev/null; then return 0; fi
    sleep 0.1
  done
  return 1
}

"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & CHARACTER_PID=$!
start_world
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >"$TMP/gateway.log" 2>&1 & GATEWAY_PID=$!

if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
   && wait_ready "$WORLD_LOG" '^ready name=World' "世界服" \
   && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关" \
   && wait_line "$WORLD_LOG" '^position_sync_hz=8$'; then
  ok "角色服、世界服、网关三个进程都起来，8Hz 广播后台服务也就绪"
else
  bad "有进程没起来或广播服务没就绪"; tail -12 "$WORLD_LOG" | detail; finish
fi
if grep -qF 'flight_config=ok realms=炼体,筑基,结丹 flight_realm=筑基' "$WORLD_LOG" \
   && grep -qF 'position_sync_hz=8' "$WORLD_LOG" \
   && grep -qF 'tier_config=ok near=8 far=20' "$WORLD_LOG" \
   && grep -qF 'gather_config=ok nodes=4' "$WORLD_LOG"; then
  ok "启动日志读到飞行门槛，老的 8Hz、三档与四个采集节点没回退"
else
  bad "启动日志缺飞行配置或老配置回退了"; grep -E '^(flight_|tier_|position_|gather_)' "$WORLD_LOG" | detail
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then ok "两个账号都拿到令牌"; else bad "登录失败"; finish; fi
IDS=()
for name in p13c_a p13c_b; do
  if create_char "$TOKEN" "$name"; then IDS+=("$NEW_ID"); else bad "建角色失败：$name"; fi
done
if create_char "$OTHER_TOKEN" "p13c_other"; then OTHER_ID="$NEW_ID"; else bad "建别人账号的角色失败"; fi
[ "${#IDS[@]}" = "2" ] && [ -n "${OTHER_ID:-}" ] || finish
ok "主账号两个角色、别人账号一个角色就绪（${IDS[*]} / $OTHER_ID）"

for id in "${IDS[@]}"; do
  resp="$(world_enter "$TOKEN" "$id" "栖云城")"
  if [ "$(code_of "$resp")" != "200" ] || [ "$(json_field "$(body_of "$resp")" line_id)" != "栖云城#1" ]; then
    bad "角色 $id 进栖云城失败：$(body_of "$resp")"; finish
  fi
done
ok "两个角色进同一条分线 栖云城#1"

# 先把疲劳打到 6000，飞行与跨区不改疲劳才有可比的东西。
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]},\"dungeon_id\":\"abyss\",\"party_size\":4}")"
if [ "$(json_field "$(body_of "$resp")" allowed)" = "true" ] \
   && [ "$(json_field "$(body_of "$resp")" fatigue)" = "6000" ] \
   && [ "$(fatigue_of "${IDS[0]}")" = "6000" ]; then
  ok "A 先进一次 4 人深渊，疲劳基线 6000"
else
  bad "疲劳基线没建立：$(body_of "$resp") / $(fatigue_of "${IDS[0]}")"; finish
fi

echo "== 5. 网关鉴权与归属复核 =="
resp="$(fly "" "${IDS[0]}" true)"
[ "$(code_of "$resp")" = "401" ] && ok "没有令牌 401" || bad "没有令牌没被挡：$(code_of "$resp")"
resp="$(fly forged "${IDS[0]}" true)"
[ "$(code_of "$resp")" = "401" ] && ok "伪造令牌 401" || bad "伪造令牌没被挡：$(code_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/world/fly?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${IDS[0]}}")"
[ "$(code_of "$resp")" = "400" ] && ok "缺 want_fly 400" || bad "缺开关没被挡：$(code_of "$resp")"
resp="$(fly "$TOKEN" "$OTHER_ID" true)"
[ "$(code_of "$resp")" = "404" ] && ok "别人的角色 404（角色服复核归属）" || bad "别人的角色没被挡：$(code_of "$resp")"
resp="$(region "$TOKEN" "$OTHER_ID" "听谣镇")"
[ "$(code_of "$resp")" = "404" ] && ok "跨区也复核归属：别人的角色 404" || bad "跨区没复核归属：$(code_of "$resp")"
resp="$(region "$TOKEN" "${IDS[0]}" "")"
[ "$(code_of "$resp")" = "400" ] && ok "缺目标地点 400" || bad "缺目标地点没被挡：$(code_of "$resp")"

echo "== 6. 没有飞行权限：飞请求被拉回地面 =="
resp="$(fly "$TOKEN" "${IDS[0]}" true)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=false reason=flight_forbidden flying=false can_fly=false realm=炼体 required_realm=筑基 y=0)" = "ok" ]; then
  ok "炼体（低于筑基）发飞请求：200 + ok=false + flight_forbidden，y 拉回 0"
else
  bad "飞请求没按预期被拉回：$(code_of "$resp") $BODY"
fi
resp="$(fly "$TOKEN" "${IDS[0]}" true ',"realm":"结丹","can_fly":true,"account":"other","y":99')"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=false reason=flight_forbidden flying=false y=0 realm=炼体)" = "ok" ]; then
  ok "请求体塞 realm/can_fly/account/y 都无效，境界仍由角色服给"
else
  bad "改包绕过了境界判定：$BODY"
fi
if [ "$(fatigue_of "${IDS[0]}")" = "6000" ]; then
  ok "飞请求不改疲劳（仍是 6000）"
else
  bad "飞请求动了疲劳：$(fatigue_of "${IDS[0]}")"
fi

echo "== 7. 位置帧改包：不通过 /world/fly 直接报高 y 也飞不起来 =="
PROBE_OUT="$(probe)"
if printf '%s' "$PROBE_OUT" | grep -q '^verdict=ok$' \
   && printf '%s' "$PROBE_OUT" | grep -q 'flight_frame=ok rejected=1 peer_y=0 flying=false can_fly=false'; then
  ok "探针：flight_forbidden 拉回，别人看到的 y=0、flying/can_fly 都是 false"
else
  bad "位置帧改包没被拉回：${PROBE_OUT:0:220}"
fi

echo "== 8. 跨区：境界门槛与老规则 =="
resp="$(region "$TOKEN" "${IDS[0]}" "不存在的地表")"
[ "$(expect_json "$(body_of "$resp")" ok=false reason=unknown_region)" = "ok" ] \
  && ok "未知地点 unknown_region" || bad "未知地点没被挡：$(body_of "$resp")"
resp="$(region "$TOKEN" "${IDS[0]}" "外环隙")"
[ "$(expect_json "$(body_of "$resp")" ok=false reason=not_adjacent)" = "ok" ] \
  && ok "不相连的地表 not_adjacent" || bad "不相连没被挡：$(body_of "$resp")"
resp="$(region "$TOKEN" "${IDS[0]}" "旧天城区")"
[ "$(expect_json "$(body_of "$resp")" ok=false reason=region_locked)" = "ok" ] \
  && ok "旧天城区仍是 P1-3a1 的 region_locked（第 11 场才开）" || bad "主线开放规则回退了：$(body_of "$resp")"
resp="$(region "$TOKEN" "${IDS[0]}" "龙巢门口")"
BODY="$(body_of "$resp")"
if [ "$(expect_json "$BODY" ok=false reason=region_realm_locked realm=炼体 required_realm=结丹 region_id=栖云城 line_id=栖云城#1)" = "ok" ]; then
  ok "炼体去龙巢门口（要结丹）被拉回，仍在栖云城#1"
else
  bad "境界门槛没生效：$BODY"
fi
resp="$(region "$TOKEN" "${IDS[0]}" "龙巢门口" ',"realm":"结丹","line_id":"hacked","region_id":"龙巢门口","account":"other"')"
[ "$(expect_json "$(body_of "$resp")" ok=false reason=region_realm_locked region_id=栖云城)" = "ok" ] \
  && ok "跨区请求塞 realm/line_id/region_id/account 都没用" || bad "跨区改包绕过门槛：$(body_of "$resp")"
resp="$(region "$TOKEN" "${IDS[0]}" "听谣镇")"
BODY="$(body_of "$resp")"
if [ "$(expect_json "$BODY" ok=true reason= region_id=听谣镇 line_id=听谣镇#1 realm=炼体)" = "ok" ]; then
  ok "门槛够且相连：跨到听谣镇#1，境界只从角色服读"
else
  bad "够门槛的跨区没成功：$BODY"
fi
[ "$(fatigue_of "${IDS[0]}")" = "6000" ] \
  && ok "跨区也不改疲劳（仍是 6000）" || bad "跨区动了疲劳：$(fatigue_of "${IDS[0]}")"

echo "== 9. 改配置重启：门槛跟着配置变，代码里没有第二套数字 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
p = os.environ['TMP'] + '/flight.json'
d = json.load(open(p, encoding='utf-8'))
d['flight_realm'] = '炼体'
for g in d['region_realms']:
    if g['region_id'] == '龙巢门口':
        g['realm'] = '炼体'
json.dump(d, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
stop_pid "$WORLD_PID"
WORLD_PID=""
start_world "$TMP/world2.log"
if wait_ready "$WORLD_LOG" '^ready name=World pid=' "重启后的世界服" \
   && wait_line "$WORLD_LOG" '^position_sync_hz=8$' \
   && grep -qF 'flight_config=ok realms=炼体,筑基,结丹 flight_realm=炼体' "$WORLD_LOG"; then
  ok "世界服带新门槛重启，启动日志读到 flight_realm=炼体"
else
  bad "重启后没读到新门槛"; tail -20 "$WORLD_LOG" | detail; finish
fi
STATE="$("$DOTNET" "$WORLD_DLL" --flight-state --config "$TMP/world.json" --flight-config "$TMP/flight.json" 2>&1)"
if printf '%s' "$STATE" | grep -qF 'flight_realm=炼体' \
   && printf '%s' "$STATE" | grep -qF 'region=龙巢门口 realm=炼体'; then
  ok "只读入口也读到新的飞行门槛与龙巢门口门槛"
else
  bad "只读入口没跟上新配置：${STATE:0:150}"
fi
resp="$(world_enter "$TOKEN" "${IDS[0]}" "栖云城")"
[ "$(json_field "$(body_of "$resp")" line_id)" = "栖云城#1" ] \
  && ok "重启后重新进线（在线状态只在内存，重进即重建）" || bad "重启后进线失败：$(body_of "$resp")"
resp="$(fly "$TOKEN" "${IDS[0]}" true)"
BODY="$(body_of "$resp")"
if [ "$(expect_json "$BODY" ok=true reason= flying=true can_fly=true realm=炼体 required_realm=炼体)" = "ok" ]; then
  ok "门槛降到炼体后，同一个角色飞请求生效（ok=true / flying=true）"
else
  bad "改配置后飞请求仍不生效：$BODY"
fi
resp="$(fly "$TOKEN" "${IDS[0]}" false)"
BODY="$(body_of "$resp")"
if [ "$(expect_json "$BODY" ok=true flying=false can_fly=true y=0)" = "ok" ]; then
  ok "主动落地：flying=false，y 回到地面，权限仍在服务端"
else
  bad "落地回包不对：$BODY"
fi
resp="$(region "$TOKEN" "${IDS[0]}" "龙巢门口")"
BODY="$(body_of "$resp")"
if [ "$(expect_json "$BODY" ok=true region_id=龙巢门口 line_id=龙巢门口#1 required_realm=炼体)" = "ok" ]; then
  ok "门槛改成炼体后，龙巢门口这趟跨区成功"
else
  bad "改配置后跨区仍不生效：$BODY"
fi

echo "== 10. 老规则不回退 =="
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/world/gather?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]},\"node_id\":\"ore_kulan_1\"}")"
[ "$(expect_json "$(body_of "$resp")" ok=false reason=wrong_region)" = "ok" ] \
  && ok "采集仍按节点所在区域判（人在龙巢门口，采枯岚道的矿回 wrong_region）" \
  || bad "采集区域规则回退：$(body_of "$resp")"
[ "$(fatigue_of "${IDS[0]}")" = "6000" ] \
  && ok "飞行、跨区、重启都没动疲劳（6000）" || bad "疲劳被改了：$(fatigue_of "${IDS[0]}")"
if grep -qF 'position_sync_hz=8' "$WORLD_LOG" \
   && grep -qF 'tier_config=ok near=8 far=20' "$WORLD_LOG"; then
  ok "重启后仍是 8Hz 位置同步与 8/20 三档阈值"
else
  bad "重启后老的位置/档位配置回退了"
fi

finish
