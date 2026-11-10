#!/usr/bin/env bash
# P1-3a2 核对：5–10Hz 位置同步，周围的人能看到移动。
#
# 完成标准（开发架构第 14 节第 31 行 + 第 11 节 P1-3）：
#   两人同分线时一方移动能被另一方看到；广播频率 5–10Hz 只从配置读；
#   分线隔离、离开释放、服务端校验位移、网关沿用令牌与归属复核；老规则不回退。
#
# 两种模式：
#   bash tools/check/check-p1-3a2.sh                    服务端段：编译、静态、连库连服（要有 gamedb）
#   MMO_P13A2_MODE=client bash tools/check/check-p1-3a2.sh  客户端段：静态 + Unity 编辑器自检（要有 Unity）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
MODE="${MMO_P13A2_MODE:-server}"

WORLD_DLL="$ROOT/src/World/bin/Release/net10.0/World.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
PROBE_DLL="$ROOT/tools/probe/WorldProbe/bin/Release/net10.0/WorldProbe.dll"

WORLD_PORT=${MMO_P13A2_WORLD_PORT:-63092}
CHAR_PORT=${MMO_P13A2_CHAR_PORT:-62107}
GATEWAY_PORT=${MMO_P13A2_GATEWAY_PORT:-61108}
ACCOUNT="p13a2_user"
OTHER="p13a2_other"
TEST_PASSWORD="p13a2-Pw-7c19"
CHAR_NAMES=("p13a2_a" "p13a2_b" "p13a2_c" "p13a2_d" "p13a2_other")

PASS=0
FAIL=0
TMP="$(mktemp -d)"
WORLD_PID=""
CHARACTER_PID=""
GATEWAY_PID=""
HAVE_DB=""
TOKEN=""
OTHER_TOKEN=""

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

probe() {
  local name="$1"
  shift
  local out
  out="$("$DOTNET" "$PROBE_DLL" \
    --base "ws://127.0.0.1:${GATEWAY_PORT}/world/stream" \
    --http "http://127.0.0.1:${GATEWAY_PORT}" \
    --token-a "$TOKEN" --token-b "$TOKEN" --token-c "$TOKEN" \
    --char-a "${IDS[0]}" --char-b "${IDS[1]}" --char-c "${IDS[2]}" \
    --line "枯岚道#1" --case "$name" "$@" 2>&1)"
  if printf '%s' "$out" | grep -q '^verdict=ok$'; then
    ok "${name}：$(printf '%s' "$out" | head -1)"
  else
    bad "${name}：${out:0:220}"
  fi
}

finish() {
  echo
  echo "P1-3a2（${MODE}）核对：$PASS 项通过，$FAIL 项未通过"
  [ "$FAIL" = "0" ] || exit 1
  exit 0
}

if [ "$MODE" = "client" ]; then
  echo "== C1. 配置、proto 与客户端静态断言 =="
  if ROOT="$ROOT" python3 - >"$TMP/client-static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/world.json', encoding='utf-8'))
assert 5 <= cfg['sync_hz'] <= 10, cfg['sync_hz']
assert '数值待定' in cfg['note']
for key in ['max_move_speed', 'spawn_x', 'spawn_y', 'spawn_z', 'spawn_facing']:
    assert key in cfg, key
proto = open(root + '/proto/mmo/world.proto', encoding='utf-8').read()
assert 'rpc PositionStream(stream WorldClientFrame) returns (stream WorldServerFrame)' in proto
report = re.search(r'message PositionReport \{(.*?)\}', proto, re.S).group(1)
for forbidden in ['account', 'character_id', 'line_id', 'line_size', 'line_capacity']:
    assert forbidden not in report, forbidden
client = open(root + '/client/MMOGame/Assets/Net/WorldLink.cs', encoding='utf-8').read()
assert 'WorldClientFrame' in client and 'WorldServerFrame' in client and 'WebSocketMessageType.Binary' in client
peers = open(root + '/client/MMOGame/Assets/Net/WorldPeerPositions.cs', encoding='utf-8').read()
assert 'PositionSnapshot' in peers and 'TryGet' in peers
broadcaster = open(root + '/src/World/PositionBroadcaster.cs', encoding='utf-8').read()
assert '1000.0 / config.SyncHz' in broadcaster and 'PeriodicTimer' in broadcaster
config = open(root + '/src/World/WorldConfig.cs', encoding='utf-8').read()
assert 'world.json 缺 sync_hz' in config and '5 到 10' in config
print('client_static=ok hz=' + str(cfg['sync_hz']) + ' stream=1 no_identity=1')
PY
  then ok "频率只在配置里、客户端帧无身份字段、广播间隔只由配置算"
  else bad "客户端静态断言失败"; cat "$TMP/client-static.out" | detail
  fi

  echo "== C2. Unity 编辑器自检：周围的人能看到移动 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    finish
  fi
  if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod P13a2Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity P13a2Check.Run 退出码 0"
  else
    bad "Unity P13a2Check.Run 失败"
    tail -25 "$TMP/unity.log" | detail
  fi
  for key in config frames peers boundary; do
    if grep -q "p13a2_${key}=ok" "$TMP/unity.log"; then
      ok "编辑器自检 p13a2_${key}=ok"
    else
      bad "编辑器自检 p13a2_${key} 没过"
      grep "p13a2_${key}" "$TMP/unity.log" | tail -2 | detail
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
for proj in src/World/World.csproj src/Gateway/Gateway.csproj src/Character/Character.csproj src/Battle/Battle.csproj tools/probe/WorldProbe/WorldProbe.csproj; do
  if ! "$DOTNET" build "$ROOT/$proj" -c Release --nologo -v q >"$TMP/build-$(basename "$proj" .csproj).log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E "error " "$TMP/build-$(basename "$proj" .csproj).log" | head -4 | detail
  fi
done
if [ "$BUILD_OK" = "1" ]; then ok "世界服、网关、角色服、战斗服与核对探针编译通过"; else finish; fi

echo "== 2. 静态断言（服务端）=="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/world.json', encoding='utf-8'))
assert 5 <= cfg['sync_hz'] <= 10 and '数值待定' in cfg['note']
proto = open(root + '/proto/mmo/world.proto', encoding='utf-8').read()
assert 'rpc PositionStream(stream WorldClientFrame) returns (stream WorldServerFrame)' in proto
report = re.search(r'message PositionReport \{(.*?)\}', proto, re.S).group(1)
for forbidden in ['account', 'character_id', 'line_id', 'line_size', 'line_capacity', 'AvatarPosition']:
    assert forbidden not in report, forbidden
snapshot = re.search(r'message PositionSnapshot \{(.*?)\}', proto, re.S).group(1)
assert 'sync_hz' in snapshot and 'avatars' in snapshot and 'server_time_ms' in snapshot
broadcaster = open(root + '/src/World/PositionBroadcaster.cs', encoding='utf-8').read()
assert '1000.0 / config.SyncHz' in broadcaster and 'PeriodicTimer' in broadcaster
config = open(root + '/src/World/WorldConfig.cs', encoding='utf-8').read()
assert 'world.json 缺 sync_hz' in config and '5 到 10' in config
registry = open(root + '/src/World/LineRegistry.cs', encoding='utf-8').read()
assert 'float.IsFinite' in registry and 'config.MaxMoveSpeed' in registry and 'speed_limit' in registry
hub = open(root + '/src/World/PositionHub.cs', encoding='utf-8').read()
assert 'config.SyncHz' in hub and 'LineOf' in hub
grpc = open(root + '/src/World/WorldGrpcService.cs', encoding='utf-8').read()
assert 'TryIdentity' in grpc and 'x-mmo-account' in grpc and 'not_in_line' in grpc
proxy = open(root + '/src/Gateway/WorldStreamProxy.cs', encoding='utf-8').read()
for piece in ['TryResolve', 'ListInventoryAsync', 'expiresAt', 'x-mmo-account', 'config.WorldUpstream', 'WebSocketCloseStatus.PolicyViolation']:
    assert piece in proxy, piece
for path in ['src/World/WorldProcess.cs', 'src/World/LineRegistry.cs', 'src/World/PositionHub.cs', 'src/World/PositionBroadcaster.cs']:
    assert 'Npgsql' not in open(root + '/' + path, encoding='utf-8').read(), path
print('static_code=ok hz=' + str(cfg['sync_hz']) + ' no_identity=1 world_no_db=1 gateway_owns=1')
PY
then ok "频率、位移校验、身份头、鉴权与进程边界都落在指定文件里"
else bad "服务端静态断言失败"; cat "$TMP/static.out" | detail
fi

ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
w = json.load(open(os.environ['ROOT'] + '/config/world.json', encoding='utf-8'))
w.pop('sync_hz')
json.dump(w, open(os.environ['TMP'] + '/broken.json', 'w', encoding='utf-8'), ensure_ascii=False)
w2 = json.load(open(os.environ['ROOT'] + '/config/world.json', encoding='utf-8'))
w2['sync_hz'] = 11
json.dump(w2, open(os.environ['TMP'] + '/bad-hz.json', 'w', encoding='utf-8'), ensure_ascii=False)
PY
MISSING="$("$DOTNET" "$WORLD_DLL" --position-state --config "$TMP/broken.json" 2>&1)"
if printf '%s' "$MISSING" | grep -q '^config_error=world.json 缺 sync_hz$'; then
  ok "缺 sync_hz 时起不来（不给兜底频率）"
else
  bad "缺 sync_hz 没有按预期报错：${MISSING:0:120}"
fi
BADHZ="$("$DOTNET" "$WORLD_DLL" --position-state --config "$TMP/bad-hz.json" 2>&1)"
if printf '%s' "$BADHZ" | grep -q '^config_error=sync_hz 必须在方案写定的 5 到 10 之间，现在是 11$'; then
  ok "sync_hz 写成 11 起不来（守住方案写定的 5–10Hz）"
else
  bad "越界频率没有按预期报错：${BADHZ:0:120}"
fi
STATE="$("$DOTNET" "$WORLD_DLL" --position-state --config config/world.json 2>&1)"
if printf '%s' "$STATE" | grep -q '^position_state=ok sync_hz=8 '; then
  ok "只读入口读到配置：sync_hz=8、速度上限与初始站位都在"
else
  bad "只读入口输出不对：${STATE:0:120}"
fi

echo "== 3. 环境与启动 =="
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
c = {
    'schema': 'character-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'db_pool_max': 6, 'bag_slots': 40,
    'dungeon_table': tmp + '/dungeons.json', 'exp_table': tmp + '/exp.json',
}
json.dump(c, open(tmp + '/character.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
g = {
    'schema': 'gateway-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['GATEWAY_PORT'],
    'token_ttl_s': 7200, 'pbkdf2_iterations': 210000,
    'battle_upstream': 'ws://127.0.0.1:60001/battle',
    'character_upstream': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'world_upstream': 'http://127.0.0.1:' + os.environ['WORLD_PORT'],
}
json.dump(g, open(tmp + '/gateway.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY

"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & CHARACTER_PID=$!
"$DOTNET" "$WORLD_DLL" --config "$TMP/world.json" >"$TMP/world.log" 2>&1 & WORLD_PID=$!
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
# 广播服务是托管后台服务，ready 之后才打这一行，断言要等它。
if wait_line "$TMP/world.log" '^position_sync_hz=8$' \
   && grep -q '^position_config=ok sync_hz=8 ' "$TMP/world.log"; then
  ok "世界服按配置起 8Hz 广播，速度上限与初始站位也从配置读"
else
  bad "世界服启动日志里的位置配置不对"; grep '^position_' "$TMP/world.log" | detail
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then ok "两个账号都拿到令牌"; else bad "登录失败"; finish; fi
IDS=()
for name in p13a2_a p13a2_b p13a2_c p13a2_d; do
  if create_char "$TOKEN" "$name"; then IDS+=("$NEW_ID"); else bad "建角色失败：$name"; fi
done
if create_char "$OTHER_TOKEN" "p13a2_other"; then OTHER_ID="$NEW_ID"; else bad "建别人账号的角色失败"; fi
[ "${#IDS[@]}" = "4" ] && [ -n "${OTHER_ID:-}" ] || finish
ok "主账号四个角色、别人账号一个角色就绪（${IDS[*]} / $OTHER_ID）"

echo "== 4. 网关鉴权与归属复核 =="
BASE_HTTP="http://127.0.0.1:${GATEWAY_PORT}/world/stream"
resp="$(curl -s -m 10 -w '\n%{http_code}' "$BASE_HTTP?character_id=${IDS[0]}")"
[ "$(code_of "$resp")" = "401" ] && ok "没有令牌 401" || bad "没有令牌没被挡：$(code_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' "$BASE_HTTP?token=forged&character_id=${IDS[0]}")"
[ "$(code_of "$resp")" = "401" ] && ok "伪造令牌 401" || bad "伪造令牌没被挡：$(code_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' "$BASE_HTTP?token=$TOKEN")"
[ "$(code_of "$resp")" = "400" ] && ok "缺 character_id 400" || bad "缺角色编号没被挡：$(code_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' "$BASE_HTTP?token=$TOKEN&character_id=$OTHER_ID")"
[ "$(code_of "$resp")" = "404" ] && ok "别人的角色 404（角色服复核归属）" || bad "别人的角色没被挡：$(code_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' "$BASE_HTTP?token=$TOKEN&character_id=${IDS[0]}")"
[ "$(code_of "$resp")" = "400" ] && [ "$(json_field "$(body_of "$resp")" error)" = "websocket_required" ] \
  && ok "不是 WebSocket 握手的普通请求 400" \
  || bad "普通请求没按预期拒绝：$(body_of "$resp") / $(code_of "$resp")"

echo "== 5. 进分线（沿用 P1-3a1）=="
resp="$(world_enter "$TOKEN" "${IDS[0]}" "枯岚道")"
if [ "$(code_of "$resp")" = "200" ] && [ "$(json_field "$(body_of "$resp")" line_id)" = "枯岚道#1" ]; then
  ok "A 进 枯岚道#1"
else
  bad "A 进线失败：$(body_of "$resp")"; finish
fi
resp="$(world_enter "$TOKEN" "${IDS[1]}" "枯岚道")"
if [ "$(json_field "$(body_of "$resp")" line_id)" = "枯岚道#1" ] \
   && [ "$(json_field "$(body_of "$resp")" line_size)" = "2" ]; then
  ok "B 进同一条分线，人数 2"
else
  bad "B 进线失败：$(body_of "$resp")"; finish
fi
resp="$(world_enter "$TOKEN" "${IDS[2]}" "栖云城")"
if [ "$(json_field "$(body_of "$resp")" line_id)" = "栖云城#1" ]; then
  ok "C 在另一区域的另一条分线，供隔离核对"
else
  bad "C 进线失败：$(body_of "$resp")"; finish
fi

echo "== 6. 周围的人能看到移动 =="
probe stream
probe move
probe rate --hz 8 --window-ms 2000
probe isolate
probe speed
probe no_line --char-a "${IDS[3]}"

echo "== 7. 改配置重启生效：频率只有一个正本 =="
stop_pid "$WORLD_PID"
WORLD_PID=""
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
p = os.environ['TMP'] + '/world.json'
w = json.load(open(p, encoding='utf-8'))
w['sync_hz'] = 5
json.dump(w, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
"$DOTNET" "$WORLD_DLL" --config "$TMP/world.json" >"$TMP/world2.log" 2>&1 & WORLD_PID=$!
if wait_ready "$TMP/world2.log" '^ready name=World' "重启后的世界服"; then ok "改配置后世界服重启成功"; else finish; fi
if wait_line "$TMP/world2.log" '^position_sync_hz=5$' \
   && grep -q '^position_config=ok sync_hz=5 ' "$TMP/world2.log"; then
  ok "世界服按新配置起 5Hz 广播"
else
  bad "重启后的频率日志不对"; grep '^position_' "$TMP/world2.log" | detail
fi
resp="$(world_enter "$TOKEN" "${IDS[0]}" "枯岚道")"
resp2="$(world_enter "$TOKEN" "${IDS[1]}" "枯岚道")"
world_enter "$TOKEN" "${IDS[2]}" "栖云城" >/dev/null 2>&1
if [ "$(json_field "$(body_of "$resp")" line_id)" = "枯岚道#1" ] \
   && [ "$(json_field "$(body_of "$resp2")" line_size)" = "2" ]; then
  ok "重启后分线重新分配，网关到世界服的通道照常工作"
else
  bad "重启后进线不对：$(body_of "$resp") / $(body_of "$resp2")"; finish
fi
probe rate --hz 5 --window-ms 2000

echo "== 8. 离开分线后不再被看到 =="
probe leave

echo "== 9. 老规则不回退 =="
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
   && [ "$(json_field "$(body_of "$resp")" fatigue)" = "0" ]; then
  ok "P1-2a 主线单人进本仍是 allowed、不扣疲劳"
else
  bad "主线进本规则被改了：$(body_of "$resp")"
fi
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/exp/settle?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]},\"txn_id\":\"p13a2-oldrules-1\",\"party_size\":1,\"source\":\"dungeon\"}")"
if [ "$(json_field "$(body_of "$resp")" exp_gain)" = "120" ] \
   && [ "$(json_field "$(body_of "$resp")" party_bonus)" = "0" ]; then
  ok "P1-2d 单人经验仍是 120、没有组队加成"
else
  bad "经验结算规则被改了：$(body_of "$resp")"
fi

finish
