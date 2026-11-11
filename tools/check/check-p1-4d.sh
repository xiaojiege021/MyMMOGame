#!/usr/bin/env bash
# P1-4d 核对：能量结晶（方案第 9 章第 3 层、开发架构第 14 节第 38 行）。
#
# 完成标准：装上后可拆下；部位不对的结晶被拒绝。第 13 节 P1-4d 与第 11 节 P1-4
# 还写定：攻击 / 防御 / 功能三类、部位限定、可拆卸复用，结晶不改制作时掷定的
# 基础属性。拆下把结晶放回背包、不消耗金币 / 结晶、不改强化等级与绑定。
#
# 两种模式：
#   bash tools/check/check-p1-4d.sh                          服务端段：编译、静态、连库连服（要有 gamedb）
#   MMO_P14D_MODE=client bash tools/check/check-p1-4d.sh    客户端段：静态 + Unity 编辑器自检（要有 Unity）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
MODE="${MMO_P14D_MODE:-server}"

CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"

CHAR_PORT=${MMO_P14D_CHAR_PORT:-62118}
GATEWAY_PORT=${MMO_P14D_GATEWAY_PORT:-61119}
WORLD_PORT=${MMO_P14D_WORLD_PORT:-63099}
ACCOUNT="p14d_user"
OTHER="p14d_other"
TEST_PASSWORD="p14d-Pw-8d20"
CHAR_NAMES=("p14d_a" "p14d_other")

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

install() {
  local token="$1" char="$2" slot="$3" cslot="$4" socket="$5" extra="${6:-}"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character/crystal/install?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"slot\":${slot},\"crystal_slot\":${cslot},\"socket\":${socket}${extra}}"
}

remove() {
  local token="$1" char="$2" slot="$3" socket="$4" extra="${5:-}"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character/crystal/remove?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"slot\":${slot},\"socket\":${socket}${extra}}"
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

enhance() {
  local token="$1" char="$2" stone="$3" slot="$4"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character/enhance?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"slot\":${slot},\"stone_id\":\"${stone}\"}"
}

transfer() {
  local token="$1" char="$2" from="$3" to="$4"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character/transfer?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"from_slot\":${from},\"to_slot\":${to}}"
}

# 不看回包，从库里独立读回：已装数 已装明细 结晶余量 金币 强化等级 绑定
state_line() {
  "$DOTNET" "$CHARACTER_DLL" --crystal-sockets "$1" "$2" "${3:-crystal_attack}" \
    --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^crystal_state=ok .* filled=\([0-9-]*\) installed=\([^ ]*\) left=\([0-9-]*\) gold=\([0-9-]*\) enhance=\([0-9-]*\) bound=\([A-Za-z]*\)/\1 \2 \3 \4 \5 \6/p'
}

# $1=角色 $2=装备格 $3=结晶编号，后面 6 列是期望值，「-」表示这一列不检查。
expect_state() {
  local char="$1" slot="$2" item="$3" label="${10:-}" got want i
  shift 3
  got="$(state_line "$char" "$slot" "$item")"
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

# 只改结晶配置重启角色服：把某一类允许的部位换掉，不动代码。
restart_character() {
  local category="$1" part="$2" log="$3"
  ROOT="$ROOT" TMP="$TMP" CATEGORY="$category" PART="$part" python3 - <<'PY'
import json, os
p = os.environ['TMP'] + '/crystal.json'
e = json.load(open(p, encoding='utf-8'))
e['category_parts'][os.environ['CATEGORY']] = [os.environ['PART']]
json.dump(e, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
  stop_pid "$CHARACTER_PID"
  CHARACTER_LOG="$log"
  "$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$CHARACTER_LOG" 2>&1 & CHARACTER_PID=$!
  wait_ready "$CHARACTER_LOG" '^ready name=Character' "角色服（重启）" || return 1
  # 网关到新角色服的 gRPC 子通道要重新连一次。只用 ready 日志不够：进程在听，
  # 但老连接还没换掉，紧随其后的请求会撞上瞬时 "Error connecting to subchannel"。
  # 拿一个只读请求等链路真的通了再往下跑用例（总控连着跑十几张卡时更容易撞）。
  local probe
  for _ in $(seq 1 40); do
    probe="$(curl -s -m 3 -o /dev/null -w '%{http_code}' -X POST \
      "http://127.0.0.1:${GATEWAY_PORT}/inventory/can-trade?token=${TOKEN}" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":${ID},\"slot\":1}")"
    [ "$probe" = "200" ] && return 0
    sleep 0.25
  done
  return 1
}

TITLE="P1-4d（${MODE}）"

# ---------------------------------------------------------------- 客户端段
if [ "$MODE" = "client" ]; then
  echo "== C1. 结晶配置、契约与客户端静态断言 =="
  if ROOT="$ROOT" python3 - >"$TMP/client-static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/crystal.json', encoding='utf-8'))
items = {i['id']: i for i in json.load(open(root + '/config/items.json', encoding='utf-8'))['items']}
assert cfg['categories'] == ['attack', 'defense', 'utility']
assert cfg['socket_count'] > 0 and '待定' in cfg['note']
assert set(cfg['category_parts']) == set(cfg['categories'])
assert set(cfg['equipment_parts']) >= {'sword_trial', 'sword_trade', 'robe_trial', 'ring_trial'}
assert {c['category'] for c in cfg['crystals']} == set(cfg['categories'])
assert all(items[c['id']]['kind'] == 'crystal' for c in cfg['crystals'])
assert items['robe_trial']['kind'] == 'equipment' and items['ring_trial']['kind'] == 'equipment'
assert 'success_rate' not in cfg
proto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
put = re.search(r'message InstallCrystalRequest \{(.*?)\}', proto, re.S).group(1)
take = re.search(r'message RemoveCrystalRequest \{(.*?)\}', proto, re.S).group(1)
resp = re.search(r'message CrystalResponse \{(.*?)\}', proto, re.S).group(1)
fields = lambda b: set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*=', b))
assert fields(put) == {'account', 'character_id', 'slot', 'crystal_slot', 'socket', 'txn_id'}
assert fields(take) == {'account', 'character_id', 'slot', 'socket', 'txn_id'}
assert {'category', 'part', 'installed', 'duplicate', 'socket_count'} <= fields(resp)
client = open(root + '/client/MMOGame/Assets/Net/CharacterCrystal.cs', encoding='utf-8').read()
assert 'InstallBody(ulong characterId, uint slot, uint crystalSlot, uint socket)' in client
assert 'RemoveBody(ulong characterId, uint slot, uint socket)' in client
assert 'CrystalTable' not in client and 'Random' not in client and 'JsonUtility' not in client
assert 'crystal.json' not in client
repo = open(root + '/src/Character/CrystalRepository.cs', encoding='utf-8').read()
assert 'Random' not in repo and 'BeginTransactionAsync' in repo and 'wrong_part' in repo
print('client_static=ok three_categories=1 no_random=1')
PY
  then ok "三类与部位限定只在配置、请求只带格位、装上拆下不掷点"
  else bad "客户端静态断言失败"; cat "$TMP/client-static.out" | detail
  fi

  echo "== C2. Unity 编辑器自检：结晶请求与回包拆装 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    finish "$TITLE"
  fi
  if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod P14dCheck.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity P14dCheck.Run 退出码 0"
  else
    bad "Unity P14dCheck.Run 失败"
    tail -25 "$TMP/unity.log" | detail
  fi
  for key in config proto wire boundary; do
    if grep -q "p14d_${key}=ok" "$TMP/unity.log"; then
      ok "编辑器自检 p14d_${key}=ok"
    else
      bad "编辑器自检 p14d_${key} 没过"
      grep "p14d_${key}" "$TMP/unity.log" | tail -2 | detail
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
cfg = json.load(open(root + '/config/crystal.json', encoding='utf-8'))
items = {i['id']: i for i in json.load(open(root + '/config/items.json', encoding='utf-8'))['items']}
assert cfg['categories'] == ['attack', 'defense', 'utility']
assert cfg['socket_count'] == 2 and '待定' in cfg['note']
assert all(items[c['id']]['kind'] == 'crystal' for c in cfg['crystals'])
assert cfg['equipment_parts']['robe_trial'] == 'armor'
assert cfg['equipment_parts']['ring_trial'] == 'accessory'
proto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
put = re.search(r'message InstallCrystalRequest \{(.*?)\}', proto, re.S).group(1)
take = re.search(r'message RemoveCrystalRequest \{(.*?)\}', proto, re.S).group(1)
fields = lambda b: set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*=', b))
assert fields(put) == {'account', 'character_id', 'slot', 'crystal_slot', 'socket', 'txn_id'}, fields(put)
assert fields(take) == {'account', 'character_id', 'slot', 'socket', 'txn_id'}, fields(take)
assert re.search(r'rpc InstallCrystal\(InstallCrystalRequest\) returns \(CrystalResponse\)', proto)
assert re.search(r'rpc RemoveCrystal\(RemoveCrystalRequest\) returns \(CrystalResponse\)', proto)
sql = open(root + '/db/character/crystal.sql', encoding='utf-8').read()
assert 'CREATE TABLE IF NOT EXISTS "角色".crystal_socket' in sql
assert 'CREATE TABLE IF NOT EXISTS "角色".crystal_txn' in sql
assert 'txn_id       text        PRIMARY KEY' in sql
assert 'FOREIGN KEY (character_id, slot)' in sql
table = open(root + '/src/Character/CrystalTable.cs', encoding='utf-8').read()
assert '必须正好是方案写定的 attack / defense / utility' in table
assert 'equipment_parts 漏了装备部位' in table
assert 'kind=crystal' in table and 'socket_count 必须大于 0' in table
repo = open(root + '/src/Character/CrystalRepository.cs', encoding='utf-8').read()
assert 'BeginTransactionAsync' in repo and 'for update' in repo
for code in ('wrong_part', 'not_crystal', 'socket_occupied', 'socket_empty',
             'invalid_socket', 'bag_full', 'slot_empty', 'not_equipment'):
    assert code in repo, code
assert 'Random' not in repo, '装上拆下不掷点'
# C# 源码里 SQL 的引号是转义写法，按裸引号查会落空；用不依赖引号的片段。
assert 'delete from' in repo and '.crystal_socket' in repo
assert 'set count = count + 1' in repo
assert '一行都不碰强化等级、绑定、金币和装备本身' in repo
svc = open(root + '/src/Character/CharacterGrpcService.cs', encoding='utf-8').read()
assert 'InstallCrystal(' in svc and 'RemoveCrystal(' in svc
assert 'crystals.InstallAsync' in svc and 'crystals.RemoveAsync' in svc
api = open(root + '/src/Gateway/CharacterApi.cs', encoding='utf-8').read()
assert 'public sealed record InstallCrystalBody(' in api
assert 'public sealed record RemoveCrystalBody(' in api
assert 'MapPost("/character/crystal/install"' in api
assert 'MapPost("/character/crystal/remove"' in api
assert 'OwnsAsync(client, account' in api and 'Guid.NewGuid()' in api and 'Npgsql' not in api
program = open(root + '/src/Character/Program.cs', encoding='utf-8').read()
process = open(root + '/src/Character/CharacterProcess.cs', encoding='utf-8').read()
assert 'case "--crystal-state"' in program and 'case "--crystal-sockets"' in program
# 启动日志与只读入口必须同形，否则 wait_line 等不到。
for name in ('crystal_sockets=', 'crystal_categories=', 'crystal_allowed='):
    assert program.count(name) == 1 and process.count(name) == 1, name
print('static_code=ok gate_server_side=1 no_random=1')
PY
then ok "配置、契约、事务、进程边界与只读入口都落在指定文件里"
else bad "服务端静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 3. 结晶表的配置校验：写定规则被改 / 漏配都起不来 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
base = json.load(open(root + '/config/crystal.json', encoding='utf-8'))
def dump(name, obj):
    json.dump(obj, open(tmp + '/' + name, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
w = copy.deepcopy(base); w.pop('categories'); dump('c-no-categories.json', w)
w = copy.deepcopy(base); w['categories'] = ['attack', 'defense']; dump('c-short-categories.json', w)
w = copy.deepcopy(base); w['categories'] = ['attack', 'defense', 'lucky']; dump('c-rename-category.json', w)
w = copy.deepcopy(base); w.pop('socket_count'); dump('c-no-sockets.json', w)
w = copy.deepcopy(base); w['socket_count'] = 0; dump('c-zero-sockets.json', w)
w = copy.deepcopy(base); w.pop('equipment_parts'); dump('c-no-parts.json', w)
w = copy.deepcopy(base); w['equipment_parts'].pop('robe_trial'); dump('c-missing-equip.json', w)
w = copy.deepcopy(base); w['equipment_parts']['ore_iron'] = 'weapon'; dump('c-part-not-equip.json', w)
w = copy.deepcopy(base); w['equipment_parts']['sword_trial'] = ''; dump('c-empty-part.json', w)
w = copy.deepcopy(base); w.pop('category_parts'); dump('c-no-category-parts.json', w)
w = copy.deepcopy(base); w['category_parts'].pop('utility'); dump('c-missing-category.json', w)
w = copy.deepcopy(base); w['category_parts']['attack'] = ['helmet']; dump('c-unknown-part.json', w)
w = copy.deepcopy(base); w['category_parts']['lucky'] = ['weapon']; dump('c-extra-category.json', w)
w = copy.deepcopy(base); w.pop('crystals'); dump('c-no-crystals.json', w)
w = copy.deepcopy(base); w['crystals'][0]['id'] = 'stone_transfer'; dump('c-crystal-kind.json', w)
w = copy.deepcopy(base); w['crystals'][0]['category'] = 'lucky'; dump('c-crystal-category.json', w)
w = copy.deepcopy(base); w['crystals'][0]['fixed_value'] = 0; dump('c-zero-value.json', w)
w = copy.deepcopy(base); w['crystals'] = [c for c in w['crystals'] if c['category'] != 'utility']; dump('c-missing-utility.json', w)
w = copy.deepcopy(base); w['crystals'][1]['id'] = w['crystals'][0]['id']; dump('c-duplicate-crystal.json', w)
PY
config_reject() {
  local label="$1" file="$2" expect="$3" out
  out="$("$DOTNET" "$CHARACTER_DLL" --crystal-state --crystal-config "$TMP/$file" \
    --config config/character.json 2>&1)"
  if printf '%s' "$out" | grep -qF "$expect"; then ok "$label"; else bad "$label：${out:0:180}"; fi
}
config_reject "缺 categories 起不来" c-no-categories.json "config_error=crystal.json 缺 categories"
config_reject "少一类结晶起不来" c-short-categories.json "config_error=crystal.json 的 categories 必须正好是方案写定的"
config_reject "类别被改名起不来" c-rename-category.json "config_error=crystal.json 的 categories 必须正好是方案写定的"
config_reject "缺 socket_count 起不来" c-no-sockets.json "config_error=crystal.json 缺 socket_count"
config_reject "socket_count=0 起不来" c-zero-sockets.json "config_error=crystal.json 的 socket_count 必须大于 0"
config_reject "缺 equipment_parts 起不来" c-no-parts.json "config_error=crystal.json 缺 equipment_parts"
config_reject "漏配一件装备的部位起不来" c-missing-equip.json "config_error=crystal.json 的 equipment_parts 漏了装备部位"
config_reject "把材料登记成装备部位起不来" c-part-not-equip.json "config_error=crystal.json 的 equipment_parts 只能登记装备"
config_reject "部位写成空串起不来" c-empty-part.json "config_error=crystal.json 的 equipment_parts 有空键或空部位"
config_reject "缺 category_parts 起不来" c-no-category-parts.json "config_error=crystal.json 缺 category_parts"
config_reject "某一类没写允许部位起不来" c-missing-category.json "config_error=crystal.json 的 category_parts 缺 utility"
config_reject "允许部位没有装备归属起不来" c-unknown-part.json "config_error=crystal.json 的 attack 写了没有装备归属的部位"
config_reject "多出写定三类之外的类别起不来" c-extra-category.json "config_error=crystal.json 的 category_parts 必须正好三类"
config_reject "缺 crystals 起不来" c-no-crystals.json "config_error=crystal.json 缺 crystals"
config_reject "结晶指向非结晶物品起不来" c-crystal-kind.json "config_error=crystal.json 的结晶必须是 kind=crystal"
config_reject "结晶类别不在写定三类里起不来" c-crystal-category.json "config_error=crystal.json 的结晶类别不在写定三类里"
config_reject "固定值写成 0 起不来" c-zero-value.json "config_error=crystal.json 的结晶 fixed_value 必须大于 0"
config_reject "功能类一颗结晶都没有起不来" c-missing-utility.json "config_error=crystal.json 的 utility 类一颗结晶都没有"
config_reject "结晶编号重复起不来" c-duplicate-crystal.json "config_error=crystal.json 的结晶编号重复"
if "$DOTNET" "$CHARACTER_DLL" --crystal-state --config config/character.json 2>&1 \
    | grep -qF 'crystal_table_state=ok version=1 crystal_sockets=2 crystal_categories=attack,defense,utility'; then
  ok "只读入口与启动共用同一套校验：好配置读出三类与两个镶嵌位"
else
  bad "好配置的只读入口没输出三类信息"
fi

if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 gamedb 凭据，连服段跑不了（这一段要在笔记本上跑）"
  finish "$TITLE"
fi

echo "== 4. 建表、播种与临时配置 =="
MIGRATE_OK=1
for sql in account.sql character.sql dungeon_entry.sql inventory.sql currency.sql transaction.sql exp_settlement.sql enhance.sql transfer.sql crystal.sql; do
  if [ "$sql" = "account.sql" ]; then
    if ! "$DOTNET" "$GATEWAY_DLL" --migrate --sql "db/character/$sql" --config config/gateway.json >"$TMP/migrate-$sql.log" 2>&1; then
      MIGRATE_OK=0; bad "建表失败：$sql"; tail -8 "$TMP/migrate-$sql.log" | detail
    fi
  elif ! "$DOTNET" "$CHARACTER_DLL" --migrate --sql "db/character/$sql" --config config/character.json >"$TMP/migrate-$sql.log" 2>&1; then
    MIGRATE_OK=0; bad "建表失败：$sql"; tail -8 "$TMP/migrate-$sql.log" | detail
  fi
done
[ "$MIGRATE_OK" = "1" ] && ok "角色 schema 就位（crystal 排在 inventory 之后）"
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
for name in ('transfer', 'crystal'):
    t = json.load(open(root + '/config/' + name + '.json', encoding='utf-8'))
    json.dump(t, open(tmp + '/' + name + '.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
c = {
    'schema': 'character-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'db_pool_max': 6, 'bag_slots': 40,
    'dungeon_table': 'config/dungeons.json', 'exp_table': 'config/exp.json',
    'enhance_table': tmp + '/enhance.json', 'transfer_table': tmp + '/transfer.json',
    'crystal_table': tmp + '/crystal.json',
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
   && wait_line "$CHARACTER_LOG" 'crystal_table_version=1 crystal_sockets=2' \
   && wait_line "$CHARACTER_LOG" 'crystal_categories=attack,defense,utility'; then
  ok "角色服与网关起来，结晶表与只读入口同形"
else
  bad "进程没起来或启动日志缺结晶表信息"; tail -12 "$CHARACTER_LOG" | detail; finish "$TITLE"
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then ok "两个账号都拿到令牌"; else bad "登录失败"; finish "$TITLE"; fi
if create_char "$TOKEN" "p14d_a" && [ -n "$NEW_ID" ]; then ID="$NEW_ID"; ok "建好主角色 id=$ID"; else bad "建主角色失败"; finish "$TITLE"; fi
if create_char "$OTHER_TOKEN" "p14d_other" && [ -n "$NEW_ID" ]; then OTHER_ID="$NEW_ID"; ok "建好别人的角色 id=$OTHER_ID"; else bad "建别人的角色失败"; finish "$TITLE"; fi

echo "== 6. 鉴权、归属与请求形状 =="
resp="$(install "" "$ID" 1 5 1)"
[ "$(code_of "$resp")" = "401" ] && ok "没有令牌 401" || bad "没有令牌没被挡：$(code_of "$resp")"
resp="$(install forged "$ID" 1 5 1)"
[ "$(code_of "$resp")" = "401" ] && ok "伪造令牌 401" || bad "伪造令牌没被挡：$(code_of "$resp")"
resp="$(install "$TOKEN" "$OTHER_ID" 1 5 1)"
[ "$(code_of "$resp")" = "404" ] && ok "别人的角色 404（网关复核归属）" || bad "别人的角色没被挡：$(code_of "$resp")"
resp="$(install "$TOKEN" "$ID" 0 5 1)"
[ "$(code_of "$resp")" = "400" ] && ok "slot=0 被挡：400 + bad_request" || bad "slot 形状没校验：$(body_of "$resp")"
resp="$(install "$TOKEN" "$ID" 1 0 1)"
[ "$(code_of "$resp")" = "400" ] && ok "crystal_slot=0 被挡：400 + bad_request" || bad "crystal_slot 形状没校验：$(body_of "$resp")"
resp="$(install "$TOKEN" "$ID" 1 5 0)"
[ "$(code_of "$resp")" = "400" ] && ok "socket=0 被挡：400 + bad_request" || bad "socket 形状没校验：$(body_of "$resp")"
resp="$(remove "$TOKEN" "$ID" 1 0)"
[ "$(code_of "$resp")" = "400" ] && ok "拆下 socket=0 被挡：400 + bad_request" || bad "拆下形状没校验：$(body_of "$resp")"
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/character/crystal/install?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID},\"slot\":1,\"crystal_slot\":5,\"socket\":\"1\"}")"
[ "$(code_of "$resp")" = "400" ] && ok "socket 传字符串被挡：400 + bad_request" || bad "位号字段形状没校验：$(body_of "$resp")"

echo "== 7. 发放装备、三类结晶、金币与老卡材料 =="
SEED_OK=1
# 1/2 是武器 / 护甲（拾取即绑），3/13 是饰品（不绑），4/11 是另一把武器；
# 5/6/7 是三类结晶，12 是普通材料（验 not_crystal）。
for spec in "1 sword_trial 1" "2 robe_trial 1" "3 ring_trial 1" "4 sword_trade 1" \
            "5 crystal_attack 100" "6 crystal_defense 100" "7 crystal_utility 100" \
            "8 ticket_protect 2" "9 stone_enhance_low 60" "10 stone_transfer 20" \
            "11 sword_trade 1" "12 ore_iron 5" "13 ring_trial 1"; do
  set -- $spec
  r="$(add_item "$TOKEN" "$ID" "$1" "$2" "$3")"
  [ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "发 $2 失败：$(body_of "$r")"; }
done
r="$(add_gold 100000)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "发金币失败：$(body_of "$r")"; }
for spec in "1 sword_trial 1" "5 crystal_attack 10"; do
  set -- $spec
  r="$(add_item "$OTHER_TOKEN" "$OTHER_ID" "$1" "$2" "$3")"
  [ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "给别人的角色发 $2 失败"; }
done
[ "$SEED_OK" = "1" ] && ok "四种部位装备、三类结晶、金币与老卡材料到位"
[ "$SEED_OK" = "1" ] || finish "$TITLE"
level "$ID" 1 5
expect_state "$ID" 1 crystal_attack 0 "1:-,2:-" 100 100000 5 true "库里初始状态：两镶嵌位都空、攻击结晶 100、装备 +5 且已绑定"

echo "== 8. 完成标准一：装上后可拆下，结晶回背包复用 =="
resp="$(install "$TOKEN" "$ID" 1 5 1)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true reason= installed=true slot=1 socket=1 crystal_item=crystal_attack category=attack part=weapon socket_count=2 filled_before=0 filled=1 crystal_left=99 gold=100000 enhance_level=5 bound=true duplicate=false)" = "ok" ]; then
  ok "攻击结晶装上武器第 1 位：回包带类别 / 部位，金币、强化、绑定都不变"
else
  bad "装上没按预期：$BODY"
fi
expect_state "$ID" 1 crystal_attack 1 "1:crystal_attack,2:-" 99 100000 5 true "库里第 1 位有结晶、背包少一颗，其它一格没动"
resp="$(remove "$TOKEN" "$ID" 1 1)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true reason= installed=false slot=1 socket=1 crystal_item=crystal_attack category=attack part=weapon filled_before=1 filled=0 crystal_left=100 gold=100000 enhance_level=5 bound=true duplicate=false)" = "ok" ]; then
  ok "拆下：第 1 位清空、结晶回背包 100 颗，金币与强化等级不变"
else
  bad "拆下没按预期：$BODY"
fi
expect_state "$ID" 1 crystal_attack 0 "1:-,2:-" 100 100000 5 true "库里拆下后两镶嵌位都空"
resp="$(install "$TOKEN" "$ID" 4 5 2)"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$(body_of "$resp")" ok=true installed=true part=weapon filled=1 crystal_left=99)" = "ok" ]; then
  ok "拆卸带走：同一颗结晶能再装到另一把武器的第 2 位（可拆卸复用）"
else
  bad "复用失败：$(body_of "$resp")"
fi
expect_state "$ID" 4 crystal_attack 1 "1:-,2:crystal_attack" 99 100000 0 false "库里格 4 的第 2 位有结晶，非绑装备仍非绑"

echo "== 9. 完成标准二：部位不对的结晶被拒绝，装备与背包都不动 =="
BEFORE="$(state_line "$ID" 2 crystal_attack)"
resp="$(install "$TOKEN" "$ID" 2 5 1)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=false reason=wrong_part installed=true socket_count=2 filled=0 crystal_left=0 gold=0 enhance_level=0 bound=false duplicate=false)" = "ok" ]; then
  ok "攻击结晶装到护甲：200 + ok=false + wrong_part，未生效数值字段都是 0"
else
  bad "部位不对没被挡：$BODY"
fi
[ "$(state_line "$ID" 2 crystal_attack)" = "$BEFORE" ] \
  && ok "被拒后护甲镶嵌位、背包结晶、金币、强化与绑定一格没动" \
  || bad "被拒后库里状态变了：before=[$BEFORE] after=[$(state_line "$ID" 2 crystal_attack)]"
resp="$(install "$TOKEN" "$ID" 1 6 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "wrong_part" ] \
  && ok "防御结晶装到武器也被拒绝" || bad "防御类部位限定没生效：$(body_of "$resp")"
resp="$(install "$TOKEN" "$ID" 1 7 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "wrong_part" ] \
  && ok "功能结晶装到武器也被拒绝" || bad "功能类部位限定没生效：$(body_of "$resp")"
resp="$(install "$TOKEN" "$ID" 2 6 1)"
[ "$(expect_json "$(body_of "$resp")" ok=true category=defense part=armor filled=1)" = "ok" ] \
  && ok "防御结晶装到护甲成功" || bad "防御类正当部位没放行：$(body_of "$resp")"
resp="$(install "$TOKEN" "$ID" 3 7 1)"
[ "$(expect_json "$(body_of "$resp")" ok=true category=utility part=accessory filled=1)" = "ok" ] \
  && ok "功能结晶装到饰品成功" || bad "功能类正当部位没放行：$(body_of "$resp")"
expect_state "$ID" 2 crystal_defense 1 "1:crystal_defense,2:-" 99 100000 0 true "护甲与饰品各自装上对应类别，金币仍为 100000"
expect_state "$ID" 3 crystal_utility 1 "1:crystal_utility,2:-" 99 100000 0 false "饰品非绑装备装上后仍非绑"

echo "== 10. 其它业务拒绝：不是结晶 / 空格 / 位号越界 / 位占用 / 空位 =="
resp="$(install "$TOKEN" "$ID" 1 12 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "not_crystal" ] \
  && ok "拿铁矿当结晶装：not_crystal" || bad "非结晶物品没被挡：$(body_of "$resp")"
resp="$(install "$TOKEN" "$ID" 1 30 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "crystal_slot_empty" ] \
  && ok "结晶格空：crystal_slot_empty" || bad "结晶空格没被挡：$(body_of "$resp")"
resp="$(install "$TOKEN" "$ID" 30 5 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "slot_empty" ] \
  && ok "装备格空：slot_empty" || bad "装备空格没被挡：$(body_of "$resp")"
resp="$(install "$TOKEN" "$ID" 5 6 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "not_equipment" ] \
  && ok "把结晶格当装备格：not_equipment" || bad "非装备格没被挡：$(body_of "$resp")"
resp="$(install "$TOKEN" "$ID" 1 5 3)"
[ "$(json_field "$(body_of "$resp")" reason)" = "invalid_socket" ] \
  && ok "socket=3 超过配置的两个镶嵌位：invalid_socket" || bad "位号越界没被挡：$(body_of "$resp")"
resp="$(install "$TOKEN" "$ID" 2 6 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "socket_occupied" ] \
  && ok "同一位重复装：socket_occupied" || bad "位占用没被挡：$(body_of "$resp")"
resp="$(remove "$TOKEN" "$ID" 1 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "socket_empty" ] \
  && ok "拆空位：socket_empty" || bad "拆空位没被挡：$(body_of "$resp")"
expect_state "$ID" 2 crystal_defense 1 "1:crystal_defense,2:-" 99 100000 0 true "七类被拒后护甲状态一格没动"

echo "== 11. 装上 / 拆下都不改强化等级、绑定与金币，也不消耗结晶 =="
level "$ID" 11 7
BEFORE="$(state_line "$ID" 11 crystal_attack)"
resp="$(install "$TOKEN" "$ID" 11 5 1)"
[ "$(expect_json "$(body_of "$resp")" ok=true enhance_level=7 bound=false gold=100000)" = "ok" ] \
  && ok "+7 非绑武器装上结晶：回包里的强化等级、绑定、金币都没变" || bad "装上改了不该改的字段：$(body_of "$resp")"
resp="$(remove "$TOKEN" "$ID" 11 1)"
[ "$(expect_json "$(body_of "$resp")" ok=true enhance_level=7 bound=false gold=100000)" = "ok" ] \
  && ok "拆下后仍是 +7、非绑、金币 100000" || bad "拆下改了不该改的字段：$(body_of "$resp")"
AFTER="$(state_line "$ID" 11 crystal_attack)"
[ "$AFTER" = "$BEFORE" ] && ok "库里装上再拆下后状态完全回到起点（结晶也没少）" \
  || bad "装上拆下不是无损：before=[$BEFORE] after=[$AFTER]"
r="$(can_trade 11)"
[ "$(json_field "$r" tradable)" = "true" ] && ok "非绑装备装过结晶再拆下，仍可交易" || bad "绑定被误改：$r"
r="$(can_trade 1)"
[ "$(json_field "$r" tradable)" = "false" ] && ok "拾取即绑装备仍是绑定" || bad "绑定被解开：$r"

echo "== 12. 改配置重启生效：部位限定只认配置，代码里没有第二套 =="
if restart_character attack armor "$TMP/character2.log" \
   && wait_line "$CHARACTER_LOG" 'crystal_allowed=attack:armor,defense:armor,utility:accessory'; then
  ok "把攻击类允许部位从 weapon 改成 armor 重启，启动日志读到新映射"
else
  bad "重启后没读到新部位映射"; grep 'crystal_table_version' "$CHARACTER_LOG" | detail; finish "$TITLE"
fi
resp="$(install "$TOKEN" "$ID" 13 5 1)"
[ "$(json_field "$(body_of "$resp")" reason)" = "wrong_part" ] \
  && ok "新配置下攻击结晶装到饰品被拒" || bad "改配置没生效：$(body_of "$resp")"
resp="$(install "$TOKEN" "$ID" 2 5 2)"
[ "$(expect_json "$(body_of "$resp")" ok=true category=attack part=armor filled=2)" = "ok" ] \
  && ok "新配置下攻击结晶装到护甲第 2 位成功" || bad "改配置后正当部位没放行：$(body_of "$resp")"
if restart_character attack weapon "$TMP/character3.log" \
   && wait_line "$CHARACTER_LOG" 'crystal_allowed=attack:weapon,defense:armor,utility:accessory'; then
  ok "改回配置重启，部位映射恢复"
else
  bad "改回配置后没读到原映射"
fi

echo "== 13. 伪造字段无效、事务号恰好一行 =="
FAKE=',"category":"utility","part":"accessory","crystal_item":"crystal_utility","fixed_value":999,"socket_count":9,"filled":9,"filled_before":9,"crystal_left":999,"gold":999999,"enhance_level":99,"bound":true,"installed":false,"result":true,"txn_id":"fake","account":"p14d_other","duplicate":true'
resp="$(install "$TOKEN" "$ID" 11 5 1 "$FAKE")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true installed=true category=attack part=weapon socket_count=2 filled_before=0 filled=1 enhance_level=7 bound=false duplicate=false)" = "ok" ]; then
  ok "伪造类别 / 部位 / 结晶编号 / 位号数 / 结果 / 事务号 / 账号全无效"
else
  bad "伪造字段影响了结果：$BODY"
fi
LAST_TXN="$(grep -o 'txn=crystal_install:[0-9a-f]*' "$TMP/gateway.log" | tail -1 | cut -d= -f2)"
TXN_ROWS="$("$DOTNET" "$CHARACTER_DLL" --crystal-txn-count "$LAST_TXN" --config "$TMP/character.json" 2>/dev/null \
  | sed -n 's/^crystal_txn_count=\([0-9]*\) .*/\1/p')"
[ "$TXN_ROWS" = "1" ] && ok "这一笔的服务端事务号在库里恰好一行（防重放）" \
  || bad "事务号行数不对：txn=$LAST_TXN rows=$TXN_ROWS"

echo "== 14. P1-4a / P1-4b / P1-4c / P1-1b3 老规则不回退 =="
level "$ID" 11 0
resp="$(enhance "$TOKEN" "$ID" stone_enhance_mid 11)"
[ "$(json_field "$(body_of "$resp")" reason)" = "wrong_stone" ] \
  && ok "P1-4a：低级档目标用中级石仍回 wrong_stone" || bad "三档分界回退了：$(body_of "$resp")"
level "$ID" 1 3
level "$ID" 4 5
resp="$(transfer "$TOKEN" "$ID" 1 4)"
[ "$(json_field "$(body_of "$resp")" reason)" = "source_not_higher" ] \
  && ok "P1-4c：来源 +3 目标 +5 的强化转移仍被拒绝" || bad "转移回归了：$(body_of "$resp")"
r="$(can_trade 8)"
[ "$(json_field "$r" tradable)" = "false" ] && ok "P1-4b / P1-1b3：保护券到账即绑、不可交易" || bad "绑定判定回退了：$r"
r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"dungeon_id\":\"main\",\"party_size\":1}")"
[ "$(json_field "$r" allowed)" = "true" ] && ok "P1-2a：主线单人仍能进本" || bad "进本回退了：$r"
r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/exp/settle?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"txn_id\":\"p14d-exp-1\",\"source\":\"task\",\"party_size\":1}")"
[ "$(json_field "$r" level_cap)" = "100" ] && ok "P1-2d：经验结算仍按 level_cap=100" || bad "经验结算回退了：$r"
for _ in 1 2; do
  r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/grant?token=${TOKEN}" \
    -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"txn_id\":\"p14d-grant-repeat\",\"entries\":[{\"currency\":{\"kind\":\"gold\",\"delta\":1}}]}")"
done
[ "$(json_field "$r" duplicate)" = "true" ] && ok "P1-1b5：同一发奖事务号仍只发一次" || bad "发奖幂等回退了：$r"

finish "$TITLE"
