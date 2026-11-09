#!/usr/bin/env bash
# P1-3a1 核对：分线加载方案正文 2.5 的几块密区，资源进 Launch AB。
#
# 完成标准（开发架构第 14 节第 30 行 + 第 11 节 P1-3）：
#   人能进入一条分线；地点和人物编号与 2.5 到 2.7 一致，对话在配置里；
#   地表不另打一套 AB。网关鉴权与归属校验沿用前卡，在线状态不落库。
#
# 两种模式：
#   bash tools/check-p1-3a1.sh                    服务端段：编译、静态、连库连服（要有 gamedb）
#   MMO_P13A1_MODE=client bash tools/check-p1-3a1.sh   客户端段：方案原文比对、Launch AB 自检（要有 Unity）
#
# 服务端段在有 gamedb 的机器上跑（笔记本）；客户端段在有 Unity 的机器上跑。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
export ROOT
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
MODE="${MMO_P13A1_MODE:-server}"

WORLD_DLL="$ROOT/src/World/bin/Release/net10.0/World.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"

WORLD_PORT=${MMO_P13A1_WORLD_PORT:-63091}
CHAR_PORT=${MMO_P13A1_CHAR_PORT:-62106}
GATEWAY_PORT=${MMO_P13A1_GATEWAY_PORT:-61107}
ACCOUNT="p13a1_user"
OTHER="p13a1_other"
TEST_PASSWORD="p13a1-Pw-5b82"
CHAR_NAMES=("p13a1_a" "p13a1_b" "p13a1_c" "p13a1_d" "p13a1_e" "p13a1_other")

PASS=0
FAIL=0
TMP="$(mktemp -d)"
WORLD_PID=""
CHARACTER_PID=""
GATEWAY_PID=""
HAVE_DB=""
BG_PIDS=()

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
  for pid in "${BG_PIDS[@]:-}"; do stop_pid "$pid"; done
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

split_response() {
  BODY="${1%$'\n'*}"
  CODE="${1##*$'\n'}"
}

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
  split_response "$resp"
  NEW_ID="$(json_field "$BODY" character_id)"
  [ "$CODE" = "200" ] && [ -n "$NEW_ID" ]
}

world_enter() {
  # $1=令牌（空串不带） $2=角色 $3=区域 $4=额外伪造字段（可空）
  local token="$1" url="http://127.0.0.1:${GATEWAY_PORT}/world/enter"
  [ -n "$token" ] && url="${url}?token=${token}"
  curl -s -m 10 -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2},\"region_id\":\"${3}\"${4:-}}"
}

world_leave() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/world/leave?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2}}"
}

finish() {
  echo
  echo "P1-3a1（${MODE}）核对：$PASS 项通过，$FAIL 项未通过"
  [ "$FAIL" = "0" ] || exit 1
  exit 0
}

if [ "$MODE" = "client" ]; then
  echo "== C1. 方案原文与配置编号逐字一致 =="
  if ROOT="$ROOT" python3 - >"$TMP/doc.out" 2>&1 <<'PY'
import json, os, re, sys
root = os.environ['ROOT']
html = open(root + '/docs/MMO系统架构方案.html', encoding='utf-8').read()

def section(start, end):
    a = html.index('<span class="n">' + start + '</span>')
    b = html.index('<span class="n">' + end + '</span>', a) if end else len(html)
    return html[a:b]

def cells(block):
    return [[re.sub(r'<[^>]+>', '', c).strip() for c in re.findall(r'<td[^>]*>(.*?)</td>', row, re.S)]
            for row in re.findall(r'<tr>(.*?)</tr>', block, re.S)]

# 2.5 的八块地表：地名、位置、发生什么
s25 = section('2.5', '2.6')
rows25 = [r for r in cells(s25) if len(r) == 3]
plan_regions = [r[0] for r in rows25]
assert len(plan_regions) == 8, plan_regions
s26 = section('2.6', '2.7')
plan_factions = [r[0] for r in cells(s26) if len(r) == 3]
assert len(plan_factions) == 5, plan_factions
s27 = section('2.7', '2.8')
rows27 = [r for r in cells(s27) if len(r) == 4]
plan_lines = [(r[0], r[2], r[3]) for r in rows27]
assert len(plan_lines) == 14, len(plan_lines)

cfg = json.load(open(root + '/config/world.json', encoding='utf-8'))
assert cfg['regions'] == plan_regions, (cfg['regions'], plan_regions)
assert cfg['factions'] == plan_factions, (cfg['factions'], plan_factions)

region_files = sorted(f for f in os.listdir(root + '/config/region') if f.endswith('.json'))
assert [f[:-5] for f in region_files] == sorted(plan_regions), region_files
for f in region_files:
    d = json.load(open(root + '/config/region/' + f, encoding='utf-8'))
    row = next(r for r in rows25 if r[0] == d['id'])
    assert d['id'] == d['name'] == f[:-5]
    assert d['position'] == row[1], (d['id'], d['position'], row[1])
    assert d['note'] == row[2], (d['id'], d['note'], row[2])
    assert all(n in plan_regions for n in d['neighbors'])

speaker_files = sorted(f for f in os.listdir(root + '/config/dialogue') if f.endswith('.json'))
assert len(speaker_files) == len(cfg['speakers']) == 9, speaker_files
all_texts = []
for f in speaker_files:
    d = json.load(open(root + '/config/dialogue/' + f, encoding='utf-8'))
    assert d['id'] == d['name'] == f[:-5]
    assert d['id'] in cfg['speakers']
    for line in d['lines']:
        all_texts.append(line['text'])
        assert any(p[1] == line['text'] and p[2] == line['limit'] and p[0] == str(line['chapter']) for p in plan_lines), line
for p in plan_lines:
    assert p[1] in all_texts, p
assert len(all_texts) == len(plan_lines), (len(all_texts), len(plan_lines))

# 编号正本只有一份：客户端地址清单与 config/world.json 逐字相同
aot = open(root + '/client/MMOGame/Assets/AOT/LaunchSession.cs', encoding='utf-8').read()
names = re.findall(r'"([^"]+)"', aot[aot.index('RegionNames'):aot.index('static readonly string[] LaterRealms')])
assert names == plan_regions, (names, plan_regions)

# 八块地表资源只放编号与相连地名，不放对白
for name in plan_regions:
    body = open(root + '/client/MMOGame/Assets/Art/Launch/Region/' + name + '.txt', encoding='utf-8').read()
    assert 'region=' + name in body
    assert not any(t in body for t in all_texts)
    assert not any(r in body for r in ['元婴', '化神', '返虚', '合体', '渡劫', '大乘', '金仙', '散仙', '极境'])

# 对白不写进场景、AOT 或热更脚本
for folder, suffix in [('Scenes', '.unity'), ('AOT', '.cs'), ('HotUpdate', '.cs')]:
    base = root + '/client/MMOGame/Assets/' + folder
    for f in os.listdir(base):
        if f.endswith(suffix):
            body = open(base + '/' + f, encoding='utf-8').read()
            assert not any(t in body for t in all_texts), (folder, f)

setting = open(root + '/client/MMOGame/Assets/AssetBundleCollectorSetting.asset', encoding='utf-8').read()
assert setting.count('PackageName: Launch') == 1
assert setting.count('GroupName: Launch') == 1
assert setting.count('CollectPath:') == 3, setting.count('CollectPath:')
print('doc_compare=ok regions=8 factions=5 speakers=9 lines=14 packages=1 groups=1 collectors=3')
PY
  then ok "方案 2.5/2.6/2.7 与配置、客户端编号逐字一致，对白只在配置里，Launch 仍是一个包一个组"
  else bad "方案原文与配置编号比对失败"; cat "$TMP/doc.out" | detail
  fi

  echo "== C2. Unity 编辑器自检：八块地表进同一组 Launch AB =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    finish
  fi
  PROJ="$ROOT/client/MMOGame"
  if "$UNITY" -batchmode -quit -projectPath "$PROJ" \
      -executeMethod P13a1Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity P13a1Check.Run 退出码 0"
  else
    bad "Unity P13a1Check.Run 失败"
    tail -25 "$TMP/unity.log" | detail
  fi
  for key in config resources dialogue collector check_regions manifest; do
    if grep -q "p13a1_${key}=ok" "$TMP/unity.log"; then
      ok "编辑器自检 p13a1_${key}=ok"
    else
      bad "编辑器自检 p13a1_${key} 没过"
      grep "p13a1_${key}" "$TMP/unity.log" | tail -2 | detail
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
if [ "$BUILD_OK" = "1" ]; then ok "世界服、网关、角色服、战斗服编译通过"; else finish; fi

echo "== 2. 静态断言（服务端）=="
if ROOT="$ROOT" python3 - >"$TMP/static1.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/world.json', encoding='utf-8'))
for key in ['listen', 'region_dir', 'dialogue_dir', 'line_capacity', 'max_lines_per_region', 'regions', 'speakers', 'factions']:
    assert key in cfg, key
assert cfg['line_capacity'] == 4 and cfg['max_lines_per_region'] == 3
assert '数值待定' in cfg['note']
assert len(cfg['regions']) == 8 and len(cfg['speakers']) == 9 and len(cfg['factions']) == 5
region_files = [f for f in os.listdir(root + '/config/region') if f.endswith('.json')]
assert len(region_files) == 8
by_id = {}
for f in region_files:
    d = json.load(open(root + '/config/region/' + f, encoding='utf-8'))
    assert d['id'] == d['name'] == f[:-5] and d['id'] in cfg['regions']
    assert isinstance(d['open'], bool) and d['chapters'] and d['neighbors']
    assert d['position'] and d['note']
    by_id[d['id']] = d
for rid, d in by_id.items():
    for n in d['neighbors']:
        assert n in by_id and rid in by_id[n]['neighbors'], (rid, n)
assert [i for i in cfg['regions'] if not by_id[i]['open']] == ['旧天城区', '焦原']
dialogue_files = [f for f in os.listdir(root + '/config/dialogue') if f.endswith('.json')]
assert len(dialogue_files) == 9
for f in dialogue_files:
    d = json.load(open(root + '/config/dialogue/' + f, encoding='utf-8'))
    assert d['id'] == d['name'] == f[:-5] and d['id'] in cfg['speakers']
    assert d['lines']
    for line in d['lines']:
        assert 1 <= line['chapter'] <= 12 and line['text'] and line['limit']
        assert not line['region'] or line['region'] in by_id, line
print('static_tables=ok regions=8 dialogues=9 symmetric=1 locked=2')
PY
then ok "配置是唯一编号正本：八块地表、九个人、五个势力、相连关系对称、第 11 场两块未开"
else bad "区域与对话配置断言失败"; cat "$TMP/static1.out" | detail
fi

if ROOT="$ROOT" python3 - >"$TMP/static2.out" 2>&1 <<'PY'
import os, re
root = os.environ['ROOT']
proto = open(root + '/proto/mmo/world.proto', encoding='utf-8').read()
req = re.search(r'message EnterLineRequest \{(.*?)\}', proto, re.S).group(1)
for forbidden in ['line_id', 'line_no', 'line_size', 'line_capacity', 'neighbors', 'dialogue_ids']:
    assert forbidden not in req, forbidden
assert 'service WorldService' in proto and 'rpc EnterLine' in proto and 'rpc LeaveLine' in proto
catalog = open(root + '/src/World/WorldCatalog.cs', encoding='utf-8').read()
assert 'ConfigException' in catalog and '相连关系不对称' in catalog
assert 'speakers 里的 ' in catalog and 'regions 里的 ' in catalog
config = open(root + '/src/World/WorldConfig.cs', encoding='utf-8').read()
assert 'world.json 缺 line_capacity' in config and 'world.json 缺 max_lines_per_region' in config
registry = open(root + '/src/World/LineRegistry.cs', encoding='utf-8').read()
assert 'config.LineCapacity' in registry and 'config.MaxLinesPerRegion' in registry
assert 'Npgsql' not in registry and 'Npgsql' not in open(root + '/src/World/WorldProcess.cs', encoding='utf-8').read()
grpc = open(root + '/src/World/WorldGrpcService.cs', encoding='utf-8').read()
assert 'Damage' not in grpc and 'Skill' not in grpc
api = open(root + '/src/Gateway/WorldApi.cs', encoding='utf-8').read()
assert 'OwnsAsync' in api and 'config.WorldUpstream' in api and 'invalid_token' in api
gcfg = open(root + '/src/Gateway/GatewayConfig.cs', encoding='utf-8').read()
assert 'world_upstream' in gcfg and 'DefaultWorldUpstream' in gcfg
print('static_code=ok request_no_line_state=1 world_no_db=1 gateway_owns=1')
PY
then ok "请求带不进分线状态；世界服不连库、不结算技能；网关先鉴权再复核归属"
else bad "服务端代码断言失败"; cat "$TMP/static2.out" | detail
fi

# 缺字段就起不来：只给核对用的 --region-state 读表入口
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
w = json.load(open(os.environ['ROOT'] + '/config/world.json', encoding='utf-8'))
w.pop('max_lines_per_region')
json.dump(w, open(os.environ['TMP'] + '/broken.json', 'w', encoding='utf-8'), ensure_ascii=False)
PY
BROKEN_OUT="$("$DOTNET" "$WORLD_DLL" --region-state --config "$TMP/broken.json" 2>&1)"
if printf '%s' "$BROKEN_OUT" | grep -q '^config_error=world.json 缺 max_lines_per_region$'; then
  ok "缺 max_lines_per_region 时起不来（不给兜底数值）"
else
  bad "缺字段没有按预期报错：${BROKEN_OUT:0:120}"
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
if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
   && wait_ready "$TMP/world.log" '^ready name=World' "世界服" \
   && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关"; then
  ok "角色服、世界服、网关三个进程都起来"
else
  finish
fi
if grep -q '^region_table_version=1 regions=8 dialogues=9 speakers=9 factions=5$' "$TMP/world.log" \
   && grep -q '^line_capacity=4 max_lines_per_region=3$' "$TMP/world.log" \
   && grep -q '^locked=旧天城区,焦原$' "$TMP/world.log"; then
  ok "世界服按配置读表：八块地表、九个人、五个势力、两块第 11 场才开"
else
  bad "世界服启动日志里的编号表不对"; grep '^region_table\|^line_capacity\|^locked=' "$TMP/world.log" | detail
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then ok "两个账号都拿到令牌"; else bad "登录失败"; finish; fi
IDS=()
for name in p13a1_a p13a1_b p13a1_c p13a1_d p13a1_e; do
  if create_char "$TOKEN" "$name"; then IDS+=("$NEW_ID"); else bad "建角色失败：$name"; fi
done
if create_char "$OTHER_TOKEN" "p13a1_other"; then OTHER_ID="$NEW_ID"; else bad "建别人账号的角色失败"; fi
[ "${#IDS[@]}" = "5" ] && [ -n "${OTHER_ID:-}" ] || finish
ok "主账号五个角色、别人账号一个角色就绪（${IDS[*]} / $OTHER_ID）"

echo "== 4. 人能进入一条分线 =="
resp="$(world_enter "" "${IDS[0]}" "枯岚道")"
[ "$(code_of "$resp")" = "401" ] && ok "没有令牌 401" || bad "没有令牌没被挡：$(code_of "$resp")"
resp="$(world_enter "$TOKEN" "$OTHER_ID" "枯岚道")"
[ "$(code_of "$resp")" = "404" ] && ok "别人的角色 404（角色服复核归属）" || bad "别人的角色没被挡：$(code_of "$resp")"
resp="$(world_enter "$TOKEN" "${IDS[0]}" "根本不在这里的地名")"
split_response "$resp"
if [ "$(json_field "$BODY" reason)" = "unknown_region" ]; then ok "未知地点 unknown_region"; else bad "未知地点没被挡：$BODY"; fi
resp="$(world_enter "$TOKEN" "${IDS[0]}" "旧天城区")"
split_response "$resp"
if [ "$(json_field "$BODY" reason)" = "region_locked" ]; then ok "旧天城区第 11 场才开 region_locked"; else bad "第 11 场区域没锁住：$BODY"; fi
resp="$(world_enter "$TOKEN" "${IDS[0]}" "焦原")"
split_response "$resp"
if [ "$(json_field "$BODY" reason)" = "region_locked" ]; then ok "焦原第 11 场才开 region_locked"; else bad "焦原没锁住：$BODY"; fi

resp="$(world_enter "$TOKEN" "${IDS[0]}" "枯岚道" ',"line_id":"栖云城#9","line_size":99,"line_capacity":99')"
split_response "$resp"
B1="$(json_field "$BODY" line_id)"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" entered)" = "True" ] \
   && [ "$B1" = "枯岚道#1" ] && [ "$(json_field "$BODY" line_no)" = "1" ] \
   && [ "$(json_field "$BODY" line_size)" = "1" ] \
   && [ "$(json_field "$BODY" line_capacity)" = "4" ] \
   && [ "$(json_field "$BODY" duplicate)" = "False" ] \
   && [ "$(json_field "$BODY" neighbors)" = "承露门" ] \
   && [ "$(json_field "$BODY" dialogue_ids)" = "乔小满" ]; then
  ok "第一人进入 枯岚道#1；伪造 line_id/line_size/line_capacity 无效"
else
  bad "第一人进线回包不对：$BODY"
fi
resp="$(world_enter "$TOKEN" "${IDS[0]}" "枯岚道")"
split_response "$resp"
if [ "$(json_field "$BODY" duplicate)" = "True" ] && [ "$(json_field "$BODY" line_id)" = "$B1" ] \
   && [ "$(json_field "$BODY" line_size)" = "1" ]; then
  ok "同一人重复进不另占名额，仍是 枯岚道#1"
else
  bad "重复进线没幂等：$BODY"
fi

SIZES=()
for i in 1 2 3; do
  resp="$(world_enter "$TOKEN" "${IDS[$i]}" "枯岚道")"
  split_response "$resp"
  SIZES+=("$(json_field "$BODY" line_size)/$(json_field "$BODY" line_id)")
done
if [ "${SIZES[0]}" = "2/枯岚道#1" ] && [ "${SIZES[1]}" = "3/枯岚道#1" ] && [ "${SIZES[2]}" = "4/枯岚道#1" ]; then
  ok "第 2、3、4 人同一条线，人数 2→3→4"
else
  bad "人数递增不对：${SIZES[*]}"
fi
resp="$(world_enter "$TOKEN" "${IDS[4]}" "枯岚道")"
split_response "$resp"
if [ "$(json_field "$BODY" line_no)" = "2" ] && [ "$(json_field "$BODY" line_id)" = "枯岚道#2" ] \
   && [ "$(json_field "$BODY" line_size)" = "1" ]; then
  ok "第 5 人开第二条线 枯岚道#2"
else
  bad "容量满了没开新线：$BODY"
fi

resp="$(world_leave "$TOKEN" "${IDS[0]}")"
split_response "$resp"
if [ "$(json_field "$BODY" left)" = "True" ] && [ "$(json_field "$BODY" line_size)" = "3" ]; then
  ok "离开释放名额：枯岚道#1 从 4 回到 3"
else
  bad "离开没释放名额：$BODY"
fi
resp="$(world_enter "$TOKEN" "${IDS[0]}" "栖云城")"
split_response "$resp"
if [ "$(json_field "$BODY" line_id)" = "栖云城#1" ] \
   && [ "$(json_field "$BODY" neighbors)" = "听谣镇,旧天城区,焦原,龙巢门口" ]; then
  ok "同一个人换区进 栖云城#1，回包带相连地点"
else
  bad "换区不对：$BODY"
fi
resp="$(world_enter "$TOKEN" "${IDS[1]}" "枯岚道")"
split_response "$resp"
if [ "$(json_field "$BODY" line_size)" = "3" ]; then
  ok "换区后原来那条线的名额也释放了"
else
  bad "换区没释放旧线名额：$BODY"
fi

echo "== 5. 地点和人物编号与方案 2.5 到 2.7 一致 =="
resp="$(curl -s -m 10 -w '\n%{http_code}' "http://127.0.0.1:${GATEWAY_PORT}/world/regions?token=${TOKEN}")"
split_response "$resp"
REGIONS_BODY="$BODY"
if [ "$CODE" = "200" ] && ROOT="$ROOT" BODY="$REGIONS_BODY" python3 - <<'PY'
import json, os, sys
body = json.loads(os.environ['BODY'])
cfg = json.load(open(os.environ['ROOT'] + '/config/world.json', encoding='utf-8'))
assert body['version'] == cfg['version'] and len(body['regions']) == 8
assert [r['id'] for r in body['regions']] == cfg['regions'], body['regions']
assert [r['name'] for r in body['regions']] == cfg['regions']
assert body['speakers'] == cfg['speakers'] and body['factions'] == cfg['factions']
assert [r['id'] for r in body['regions'] if not r['open']] == ['旧天城区', '焦原']
qy = next(r for r in body['regions'] if r['id'] == '栖云城')
assert qy['neighbors'] == ['听谣镇', '旧天城区', '焦原', '龙巢门口']
assert qy['dialogue_ids'] == ['周晚棠'], qy['dialogue_ids']
print('region_reply=ok')
PY
then ok "/world/regions 回包：八块地名、九个人、五个势力、开放标记与相连关系同配置"
else bad "/world/regions 回包与配置不一致：${REGIONS_BODY:0:200}"; fi
STATE="$("$DOTNET" "$WORLD_DLL" --region-state --config "$TMP/world.json" 2>&1)"
if printf '%s' "$STATE" | grep -q '^region_state=summary version=1 regions=8 dialogues=9 speakers=9 factions=5 line_capacity=4 max_lines=3$' \
   && printf '%s' "$STATE" | grep -q '^region_state=ok id=枯岚道 chapters=1,2 neighbors=承露门 dialogues=乔小满 faction= open=True$' \
   && printf '%s' "$STATE" | grep -q '^region_state=ok id=栖云城 chapters=12 neighbors=听谣镇,旧天城区,焦原,龙巢门口 dialogues=周晚棠 faction=栖云城 open=True$'; then
  ok "只读入口读表结果与网关回包一致（编号能指到地表与开口的人）"
else
  bad "只读入口输出不对"; printf '%s\n' "$STATE" | head -12 | detail
fi

echo "== 6. 改配置重启生效，数值只有一个正本 =="
stop_pid "$WORLD_PID"
WORLD_PID=""
ROOT="$ROOT" TMP="$TMP" WORLD_PORT="$WORLD_PORT" python3 - <<'PY'
import json, os
p = os.environ['TMP'] + '/world.json'
w = json.load(open(p, encoding='utf-8'))
w['line_capacity'] = 2
w['max_lines_per_region'] = 2
json.dump(w, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
"$DOTNET" "$WORLD_DLL" --config "$TMP/world.json" >"$TMP/world2.log" 2>&1 & WORLD_PID=$!
if wait_ready "$TMP/world2.log" '^ready name=World' "重启后的世界服"; then ok "改配置后世界服重启成功"; else finish; fi
NEW_LINES=()
for i in 0 1 2 3 4; do
  resp="$(world_enter "$TOKEN" "${IDS[$i]}" "栖云城")"
  split_response "$resp"
  NEW_LINES+=("$(json_field "$BODY" reason)/$(json_field "$BODY" line_id)/$(json_field "$BODY" line_size)/$(json_field "$BODY" line_capacity)")
done
if [ "${NEW_LINES[0]}" = "/栖云城#1/1/2" ] && [ "${NEW_LINES[1]}" = "/栖云城#1/2/2" ] \
   && [ "${NEW_LINES[2]}" = "/栖云城#2/1/2" ] && [ "${NEW_LINES[3]}" = "/栖云城#2/2/2" ] \
   && [ "${NEW_LINES[4]}" = "lines_full//0/0" ]; then
  ok "容量改成 2 / 最多 2 条线后：第 3 人开 #2，第 5 人 lines_full，代码里没有第二套数字"
else
  bad "改配置没生效或分线分配不对：${NEW_LINES[*]}"
fi

echo "== 7. 老规则不回退 =="
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]},\"dungeon_id\":\"main\",\"party_size\":1}")"
split_response "$resp"
if [ "$(json_field "$BODY" allowed)" = "True" ] && [ "$(json_field "$BODY" fatigue)" = "0" ]; then
  ok "P1-2a 主线单人进本仍是 allowed、不扣疲劳"
else
  bad "主线进本规则被改了：$BODY"
fi
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/exp/settle?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${IDS[0]},\"txn_id\":\"p13a1-oldrules-1\",\"party_size\":1,\"source\":\"dungeon\"}")"
split_response "$resp"
if [ "$(json_field "$BODY" exp_gain)" = "120" ] && [ "$(json_field "$BODY" party_bonus)" = "0" ]; then
  ok "P1-2d 单人经验仍是 120、没有组队加成"
else
  bad "经验结算规则被改了：$BODY"
fi

finish
