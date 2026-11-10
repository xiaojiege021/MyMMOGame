#!/usr/bin/env bash
# P1-3a3 核对：同屏三档。
#
# 完成标准（开发架构第 14 节第 32 行 + 第 11 节 P1-3）：
#   近处完整、远处简模、再远只留名字；档位由世界服按距离算并发给周围的人，
#   客户端只按档位翻译展示，别人的拖尾与光效关掉；分线隔离与 5–10Hz 不回退。
#
# 两种模式：
#   bash tools/check/check-p1-3a3.sh                    服务端段：编译、静态、连库连服（要有 gamedb）
#   MMO_P13A3_MODE=client bash tools/check/check-p1-3a3.sh  客户端段：静态 + Unity 编辑器自检（要有 Unity）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
MODE="${MMO_P13A3_MODE:-server}"

WORLD_DLL="$ROOT/src/World/bin/Release/net10.0/World.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
PROBE_DLL="$ROOT/tools/probe/WorldProbe/bin/Release/net10.0/WorldProbe.dll"

WORLD_PORT=${MMO_P13A3_WORLD_PORT:-63093}
CHAR_PORT=${MMO_P13A3_CHAR_PORT:-62108}
GATEWAY_PORT=${MMO_P13A3_GATEWAY_PORT:-61109}
ACCOUNT="p13a3_user"
OTHER="p13a3_other"
TEST_PASSWORD="p13a3-Pw-7c19"
CHAR_NAMES=("p13a3_a" "p13a3_b" "p13a3_c" "p13a3_d" "p13a3_other")

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
    --line "枯岚道#1" --name-a "p13a3_a" --case "$name" "$@" 2>&1)"
  if printf '%s' "$out" | grep -q '^verdict=ok$'; then
    ok "${name}：$(printf '%s' "$out" | head -1)"
  else
    bad "${name}：${out:0:220}"
  fi
}

finish() {
  echo
  echo "P1-3a3（${MODE}）核对：$PASS 项通过，$FAIL 项未通过"
  [ "$FAIL" = "0" ] || exit 1
  exit 0
}

if [ "$MODE" = "client" ]; then
  echo "== C1. 配置、proto 与客户端静态断言 =="
  if ROOT="$ROOT" python3 - >"$TMP/client-static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/world.json', encoding='utf-8'))
near, far = cfg['tier_near_distance'], cfg['tier_far_distance']
assert 0 < near < far, (near, far)
assert '数值待定' in cfg['note']
assert 5 <= cfg['sync_hz'] <= 10
proto = open(root + '/proto/mmo/world.proto', encoding='utf-8').read()
for value in ['APPEARANCE_TIER_FULL = 1', 'APPEARANCE_TIER_SIMPLE = 2', 'APPEARANCE_TIER_NAME = 3']:
    assert value in proto, value
avatar = re.search(r'message AvatarPosition \{(.*?)\}', proto, re.S).group(1)
assert 'AppearanceTier appearance_tier' in avatar and 'string display_name' in avatar
report = re.search(r'message PositionReport \{(.*?)\}', proto, re.S).group(1)
for forbidden in ['appearance_tier', 'display_name', 'account', 'line_id']:
    assert forbidden not in report, forbidden
tiers = open(root + '/client/MMOGame/Assets/Net/WorldPeerTiers.cs', encoding='utf-8').read()
for piece in ['AppearanceTier.Full', 'AppearanceTier.Simple', 'AppearanceTier.Name']:
    assert piece in tiers, piece
assert 'Math.Sqrt' not in tiers and 'avatar.X' not in tiers and 'peer.X' not in tiers
assert 'TrailsAndEffects = false' in tiers
peers = open(root + '/client/MMOGame/Assets/Net/WorldPeerPositions.cs', encoding='utf-8').read()
assert 'avatar.AppearanceTier' in peers and 'avatar.DisplayName' in peers
table = open(root + '/src/World/AppearanceTierTable.cs', encoding='utf-8').read()
assert 'config.TierNearDistance' in table and 'config.TierFarDistance' in table
print('client_static=ok near=' + str(near) + ' far=' + str(far) + ' tiers=3')
PY
  then ok "两档阈值只在配置里、档位与名字只在服务端帧、客户端不算距离"
  else bad "客户端静态断言失败"; cat "$TMP/client-static.out" | detail
  fi

  echo "== C2. Unity 编辑器自检：三档表现 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    finish
  fi
  if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod P13a3Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity P13a3Check.Run 退出码 0"
  else
    bad "Unity P13a3Check.Run 失败"
    tail -25 "$TMP/unity.log" | detail
  fi
  for key in config proto tiers boundary; do
    if grep -q "p13a3_${key}=ok" "$TMP/unity.log"; then
      ok "编辑器自检 p13a3_${key}=ok"
    else
      bad "编辑器自检 p13a3_${key} 没过"
      grep "p13a3_${key}" "$TMP/unity.log" | tail -2 | detail
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
assert 0 < cfg['tier_near_distance'] < cfg['tier_far_distance'] and '数值待定' in cfg['note']
proto = open(root + '/proto/mmo/world.proto', encoding='utf-8').read()
avatar = re.search(r'message AvatarPosition \{(.*?)\}', proto, re.S).group(1)
assert 'AppearanceTier appearance_tier' in avatar and 'string display_name' in avatar
report = re.search(r'message PositionReport \{(.*?)\}', proto, re.S).group(1)
for forbidden in ['appearance_tier', 'display_name', 'account', 'line_id', 'AvatarPosition']:
    assert forbidden not in report, forbidden
enter = re.search(r'message EnterLineRequest \{(.*?)\}', proto, re.S).group(1)
assert 'string display_name' in enter
config = open(root + '/src/World/WorldConfig.cs', encoding='utf-8').read()
assert 'Distance(wire.TierNearDistance, "tier_near_distance")' in config
assert 'Distance(wire.TierFarDistance, "tier_far_distance")' in config
assert 'tier_near_distance 必须小于 tier_far_distance' in config
table = open(root + '/src/World/AppearanceTierTable.cs', encoding='utf-8').read()
assert 'config.TierNearDistance' in table and 'config.TierFarDistance' in table
assert 'Math.Sqrt' in table and '8.0' not in table and '20.0' not in table
hub = open(root + '/src/World/PositionHub.cs', encoding='utf-8').read()
assert 'tiers.For(self, avatar)' in hub and 'AppearanceTier = tier' in hub
assert 'DisplayName = state.DisplayName' in hub
registry = open(root + '/src/World/LineRegistry.cs', encoding='utf-8').read()
assert 'string DisplayName' in registry and 'previous.DisplayName' in registry
api = open(root + '/src/Gateway/WorldApi.cs', encoding='utf-8').read()
assert 'ResolveAsync' in api and 'DisplayName = displayName' in api and 'OwnsAsync' in api
svc = open(root + '/src/Character/CharacterGrpcService.cs', encoding='utf-8').read()
assert 'DisplayNameAsync' in svc and 'CharacterName = displayName' in svc
proxy = open(root + '/src/Gateway/WorldStreamProxy.cs', encoding='utf-8').read()
assert 'appearance_tier' not in proxy and 'TryResolve' in proxy
for path in ['src/World/WorldProcess.cs', 'src/World/LineRegistry.cs', 'src/World/PositionHub.cs', 'src/World/AppearanceTierTable.cs']:
    assert 'Npgsql' not in open(root + '/' + path, encoding='utf-8').read(), path
print('static_code=ok near=' + str(cfg['tier_near_distance']) + ' far=' + str(cfg['tier_far_distance'])
      + ' server_owns_tier=1 name_from_character=1 world_no_db=1')
PY
then ok "档位只在世界服算、名字由角色服给、进程边界与防改包字段都落在指定文件里"
else bad "服务端静态断言失败"; cat "$TMP/static.out" | detail
fi

ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
base = json.load(open(root + '/config/world.json', encoding='utf-8'))
w = dict(base); w.pop('tier_near_distance')
json.dump(w, open(tmp + '/no-near.json', 'w', encoding='utf-8'), ensure_ascii=False)
w = dict(base); w['tier_near_distance'] = 30.0; w['tier_far_distance'] = 20.0
json.dump(w, open(tmp + '/bad-order.json', 'w', encoding='utf-8'), ensure_ascii=False)
w = dict(base); w['tier_near_distance'] = 0.0
json.dump(w, open(tmp + '/zero-near.json', 'w', encoding='utf-8'), ensure_ascii=False)
PY
MISSING="$("$DOTNET" "$WORLD_DLL" --tier-state --config "$TMP/no-near.json" 2>&1)"
if printf '%s' "$MISSING" | grep -q '^config_error=world.json 缺 tier_near_distance$'; then
  ok "缺 tier_near_distance 时起不来（不给兜底距离）"
else
  bad "缺字段没有按预期报错：${MISSING:0:120}"
fi
ORDER="$("$DOTNET" "$WORLD_DLL" --tier-state --config "$TMP/bad-order.json" 2>&1)"
if printf '%s' "$ORDER" | grep -q '^config_error=tier_near_distance 必须小于 tier_far_distance，现在是 30 / 20$'; then
  ok "近档不小于远档时起不来（守住方案写定的三档顺序）"
else
  bad "档位顺序没有按预期报错：${ORDER:0:120}"
fi
ZERO="$("$DOTNET" "$WORLD_DLL" --tier-state --config "$TMP/zero-near.json" 2>&1)"
if printf '%s' "$ZERO" | grep -q '^config_error=tier_near_distance 必须是正的有限数，现在是 0$'; then
  ok "非正距离时起不来"
else
  bad "非正距离没有按预期报错：${ZERO:0:120}"
fi
STATE="$("$DOTNET" "$WORLD_DLL" --tier-state --config config/world.json 2>&1)"
if printf '%s' "$STATE" | grep -q '^tier_state=ok near=8 far=20 at_0=1 at_mid=2 at_far=3$'; then
  ok "只读入口读到配置：8 / 20 对应完整、简模、只留名字"
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
# 核对用小阈值，走的仍是同一份配置、同一套代码，不碰仓库里那份。
w['tier_near_distance'] = 2.0
w['tier_far_distance'] = 5.0
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
if wait_line "$TMP/world.log" '^position_sync_hz=8$' \
   && grep -q '^tier_config=ok near=2 far=5$' "$TMP/world.log"; then
  ok "世界服按配置起 8Hz 广播，两档距离也只从配置读"
else
  bad "世界服启动日志里的档位配置不对"; grep -E '^(tier_|position_)' "$TMP/world.log" | detail
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then ok "两个账号都拿到令牌"; else bad "登录失败"; finish; fi
IDS=()
for name in p13a3_a p13a3_b p13a3_c p13a3_d; do
  if create_char "$TOKEN" "$name"; then IDS+=("$NEW_ID"); else bad "建角色失败：$name"; fi
done
if create_char "$OTHER_TOKEN" "p13a3_other"; then OTHER_ID="$NEW_ID"; else bad "建别人账号的角色失败"; fi
[ "${#IDS[@]}" = "4" ] && [ -n "${OTHER_ID:-}" ] || finish
ok "主账号四个角色、别人账号一个角色就绪（${IDS[*]} / $OTHER_ID）"

echo "== 4. 网关鉴权与归属复核（沿用前卡）=="
BASE_HTTP="http://127.0.0.1:${GATEWAY_PORT}/world/stream"
resp="$(curl -s -m 10 -w '\n%{http_code}' "$BASE_HTTP?character_id=${IDS[0]}")"
[ "$(code_of "$resp")" = "401" ] && ok "没有令牌 401" || bad "没有令牌没被挡：$(code_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' "$BASE_HTTP?token=forged&character_id=${IDS[0]}")"
[ "$(code_of "$resp")" = "401" ] && ok "伪造令牌 401" || bad "伪造令牌没被挡：$(code_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' "$BASE_HTTP?token=$TOKEN")"
[ "$(code_of "$resp")" = "400" ] && ok "缺 character_id 400" || bad "缺角色编号没被挡：$(code_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' "$BASE_HTTP?token=$TOKEN&character_id=$OTHER_ID")"
[ "$(code_of "$resp")" = "404" ] && ok "别人的角色 404（角色服复核归属）" || bad "别人的角色没被挡：$(code_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/world/enter?token=$TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]},\"region_id\":\"枯岚道\",\"display_name\":\"hacked\",\"appearance_tier\":3}")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(json_field "$(body_of "$resp")" line_id)" = "枯岚道#1" ]; then
  ok "A 进 枯岚道#1；请求体里伪造的名字与档位字段不被读取"
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

echo "== 5. 连服：三档、改包、隔离与老位置流 =="
probe tier_spoof
probe tier_isolate
probe speed
probe move
probe rate --hz 8 --window-ms 2000
probe no_line --char-a "${IDS[3]}"
probe tier_stages --stage-x "0,3.5,6" --expect "1,2,3"

echo "== 6. 改配置重启：同一段距离的档位跟着变 =="
stop_pid "$WORLD_PID"
WORLD_PID=""
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
p = os.environ['TMP'] + '/world.json'
w = json.load(open(p, encoding='utf-8'))
w['tier_near_distance'] = 1.0
w['tier_far_distance'] = 10.0
json.dump(w, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
"$DOTNET" "$WORLD_DLL" --config "$TMP/world.json" >"$TMP/world2.log" 2>&1 & WORLD_PID=$!
if wait_ready "$TMP/world2.log" '^ready name=World' "重启后的世界服"; then ok "改配置后世界服重启成功"; else finish; fi
if wait_line "$TMP/world2.log" '^position_sync_hz=8$' \
   && grep -q '^tier_config=ok near=1 far=10$' "$TMP/world2.log"; then
  ok "世界服按新阈值 1 / 10 起广播，频率仍是 8Hz"
else
  bad "重启后的档位配置不对"; grep -E '^(tier_|position_)' "$TMP/world2.log" | detail
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
probe tier_stages --stage-x "0,3.5,6" --expect "1,2,2"
probe rate --hz 8 --window-ms 2000

echo "== 7. 离开分线后不再被看到 =="
probe leave

echo "== 8. 老规则不回退 =="
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
  -d "{\"character_id\":${IDS[0]},\"txn_id\":\"p13a3-oldrules-1\",\"party_size\":1,\"source\":\"dungeon\"}")"
if [ "$(json_field "$(body_of "$resp")" exp_gain)" = "120" ] \
   && [ "$(json_field "$(body_of "$resp")" party_bonus)" = "0" ]; then
  ok "P1-2d 单人经验仍是 120、没有组队加成"
else
  bad "经验结算规则被改了：$(body_of "$resp")"
fi

finish
