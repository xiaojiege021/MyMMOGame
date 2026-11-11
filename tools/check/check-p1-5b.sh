#!/usr/bin/env bash
# P1-5b 核对：元宝寄售（方案第 12 章、开发架构第 14 节第 40 行）。
#
# 完成标准：元宝换成金币，商城没有金币商品。第 11 节 P1-5 与第 13 节
# P1-5b 还写定：元宝只经寄售换成金币、抽税与 P1-5a 同一套配置、税不进任何背包。
#
# 两种模式：
#   bash tools/check/check-p1-5b.sh                          服务端段：编译、静态、连库连服
#   MMO_P1_5B_MODE=client bash tools/check/check-p1-5b.sh    客户端段：静态 + Unity 编辑器自检
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
MODE="${MMO_P1_5B_MODE:-server}"

CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
GLOBAL_DLL="$ROOT/src/Global/bin/Release/net10.0/Global.dll"

CHAR_PORT=${MMO_P1_5B_CHAR_PORT:-62123}
GATEWAY_PORT=${MMO_P1_5B_GATEWAY_PORT:-61124}
GLOBAL_PORT=${MMO_P1_5B_GLOBAL_PORT:-64024}
WORLD_PORT=${MMO_P1_5B_WORLD_PORT:-63098}
ACCOUNT="p15b_seller"
OTHER="p15b_buyer"
TEST_PASSWORD="p15b-Pw-8d31"
CHAR_NAMES=("p15b_seller" "p15b_buyer")

TMP="$(mktemp -d)"
CHARACTER_PID=""
GLOBAL_PID=""
GATEWAY_PID=""
HAVE_DB=""
TOKEN=""
BUYER_TOKEN=""
ID=""
BUYER_ID=""
NEW_ID=""

cleanup() {
  stop_pid "$GATEWAY_PID"
  stop_pid "$GLOBAL_PID"
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

add_currency() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/currency/add?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$2,\"kind\":\"$3\",\"delta\":$4}"
}

list_ingot() {
  local token="$1" char="$2" amount="$3" price="$4" extra="${5:-}"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/auction/ingot/list?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"ingot_amount\":${amount},\"price_gold\":${price}${extra}}"
}

buy_ingot() {
  local token="$1" char="$2" listing="$3" extra="${4:-}"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/auction/ingot/buy?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"listing_id\":${listing}${extra}}"
}

# 不看回包，从库里独立读回双方元宝、金币与全库总量。
ledger() {
  "$DOTNET" "$CHARACTER_DLL" --ingot-ledger "$BUYER_ID" "$ID" "${1:-0}" "${2:-}" \
    --config "$TMP/character.json" 2>/dev/null | grep '^ingot_state=ok '
}

field_of() { printf '%s' "$1" | sed -n 's/.* '"$2"'=\([^ ]*\).*/\1/p'; }

# 只改税率重启角色服。网关到角色服、全局服务到角色服两条 gRPC 子通道都要重连，
# ready 日志不够：用一个会走到角色服的只读业务请求等链路真的通。
restart_character() {
  local rate="$1" probe
  ROOT="$ROOT" TMP="$TMP" RATE="$rate" python3 - <<'PY'
import json, os
p = os.environ['TMP'] + '/auction.json'
e = json.load(open(p, encoding='utf-8'))
e['tax_rate_pct'] = int(os.environ['RATE'])
json.dump(e, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
  stop_pid "$CHARACTER_PID"
  "$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & CHARACTER_PID=$!
  wait_ready "$TMP/character.log" "auction_table_version=1 tax_rate_pct=${rate}" "角色服（重启）" || return 1
  for _ in $(seq 1 40); do
    probe="$(curl -s -m 3 -X POST \
      "http://127.0.0.1:${GATEWAY_PORT}/auction/ingot/buy?token=${BUYER_TOKEN}" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":${BUYER_ID},\"listing_id\":999999999}")"
    printf '%s' "$probe" | grep -q 'listing_not_found' && return 0
    sleep 0.25
  done
  return 1
}

TITLE="P1-5b（${MODE}）"

# ---------------------------------------------------------------- 客户端段
if [ "$MODE" = "client" ]; then
  echo "== C1. 元宝寄售配置、契约与客户端静态断言 =="
  if ROOT="$ROOT" python3 - >"$TMP/client-static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/auction.json', encoding='utf-8'))
assert 5 <= cfg['tax_rate_pct'] <= 8
assert cfg['gold_kind'] == 'gold' and cfg['ingot_kind'] == 'yuanbao'
assert '待定' in cfg['note'] and '只经寄售换成金币' in cfg['note']
assert not any('recipient' in k or 'tax_account' in k for k in cfg)
currency = json.load(open(root + '/config/currency.json', encoding='utf-8'))
by_id = {c['id']: c for c in currency['currencies']}
assert '只经寄售换成金币' in by_id['yuanbao']['flow']
assert '拍卖与寄售' in by_id['gold']['flow']
trade = open(root + '/proto/mmo/trade.proto', encoding='utf-8').read()
chrp = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
fields = lambda b: set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*=', b))
body = lambda s, m: re.search(r'message ' + m + r' \{(.*?)\}', s, re.S).group(1)
assert fields(body(trade, 'ListIngotRequest')) == {
    'account', 'character_id', 'ingot_amount', 'price_gold', 'txn_id'}
assert fields(body(trade, 'BuyIngotRequest')) == {
    'account', 'character_id', 'listing_id', 'txn_id'}
assert fields(body(chrp, 'AuctionIngotSettleRequest')) == {
    'account', 'character_id', 'listing_id', 'txn_id'}
assert {'tax_gold', 'net_gold', 'ingot_amount', 'buyer_ingot'} <= fields(body(trade, 'BuyIngotResponse'))
client = open(root + '/client/MMOGame/Assets/Net/CharacterAuction.cs', encoding='utf-8').read()
assert 'IngotListBody(ulong characterId, long ingotAmount, long priceGold)' in client
assert 'IngotBuyBody(ulong characterId, ulong listingId)' in client
assert 'ParseIngot(string json)' in client
assert 'AuctionTable' not in client and 'Random' not in client and 'JsonUtility' not in client
assert 'txn_id' not in client and 'auction.json' not in client
repo = open(root + '/src/Character/IngotRepository.cs', encoding='utf-8').read()
assert 'BeginTransactionAsync' in repo and 'Random' not in repo
assert 'auction_ingot_txn' in repo and 'auction_ingot_settlement' in repo
print('client_static=ok ingot_only=1')
PY
  then ok "寄售元宝结算金币、请求只带元宝与标价、成交不掷点"
  else bad "客户端静态断言失败"; cat "$TMP/client-static.out" | detail
  fi

  echo "== C2. Unity 编辑器自检：元宝寄售请求与回包拆装 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    finish "$TITLE"
  fi
  if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod P15bCheck.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity P15bCheck.Run 退出码 0"
  else
    bad "Unity P15bCheck.Run 失败"
    tail -25 "$TMP/unity.log" | detail
  fi
  for key in config proto wire boundary; do
    if grep -q "p15b_${key}=ok" "$TMP/unity.log"; then
      ok "编辑器自检 p15b_${key}=ok"
    else
      bad "编辑器自检 p15b_${key} 没过"
      grep "p15b_${key}" "$TMP/unity.log" | tail -2 | detail
    fi
  done
  finish "$TITLE"
fi

# ---------------------------------------------------------------- 服务端段
echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then bad "找不到 dotnet"; finish "$TITLE"; fi
BUILD_OK=1
for proj in src/Shared/Shared.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj \
            src/Global/Global.csproj src/World/World.csproj src/Battle/Battle.csproj; do
  if ! "$DOTNET" build "$ROOT/$proj" -c Release --nologo -v q >"$TMP/build-$(basename "$proj" .csproj).log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E "error " "$TMP/build-$(basename "$proj" .csproj).log" | head -4 | detail
  fi
done
if [ "$BUILD_OK" = "1" ]; then ok "六个服务端工程编译通过（元宝寄售走拍卖行的同一套边界）"; else finish "$TITLE"; fi

echo "== 2. 静态断言（服务端） =="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/auction.json', encoding='utf-8'))
assert 5 <= cfg['tax_rate_pct'] <= 8
assert cfg['gold_kind'] == 'gold' and cfg['ingot_kind'] == 'yuanbao'
assert '待定' in cfg['note'] and '只经寄售换成金币' in cfg['note']
assert not any('recipient' in k or 'tax_account' in k for k in cfg)
currency = json.load(open(root + '/config/currency.json', encoding='utf-8'))
by_id = {c['id']: c for c in currency['currencies']}
assert '只经寄售换成金币' in by_id['yuanbao']['flow']
assert '拍卖与寄售' in by_id['gold']['flow']
trade = open(root + '/proto/mmo/trade.proto', encoding='utf-8').read()
chrp = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
fields = lambda b: set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*=', b))
body = lambda s, m: re.search(r'message ' + m + r' \{(.*?)\}', s, re.S).group(1)
assert fields(body(trade, 'ListIngotRequest')) == {
    'account', 'character_id', 'ingot_amount', 'price_gold', 'txn_id'}
assert fields(body(trade, 'BuyIngotRequest')) == {
    'account', 'character_id', 'listing_id', 'txn_id'}
assert fields(body(chrp, 'AuctionIngotSettleRequest')) == {
    'account', 'character_id', 'listing_id', 'txn_id'}
assert re.search(r'rpc SettleIngot\(AuctionIngotSettleRequest\) returns \(AuctionIngotSettleResponse\)', chrp)
assert re.search(r'rpc ListIngot\(ListIngotRequest\)', trade)
assert re.search(r'rpc BuyIngot\(BuyIngotRequest\)', trade)
sql = open(root + '/db/trade/ingot.sql', encoding='utf-8').read()
assert 'CREATE TABLE IF NOT EXISTS "交易".auction_ingot_listing' in sql
assert 'CREATE TABLE IF NOT EXISTS "交易".auction_ingot_txn' in sql
assert 'CREATE TABLE IF NOT EXISTS "交易".auction_ingot_settlement' in sql
assert 'CHECK (net_gold + tax_gold = price_gold)' in sql
table = open(root + '/src/Character/AuctionTable.cs', encoding='utf-8').read()
assert 'WrittenMinRatePct = 5' in table and 'WrittenMaxRatePct = 8' in table
assert 'WrittenIngotKind = "yuanbao"' in table
assert '不许出现税的收款方字段' in table
repo = open(root + '/src/Character/IngotRepository.cs', encoding='utf-8').read()
assert 'BeginTransactionAsync' in repo and 'for update' in repo
for code in ('listing_not_found', 'listing_sold', 'same_character',
             'ingot_insufficient', 'gold_insufficient', 'duplicate'):
    assert code in repo, code
# C# 源码里 SQL 的引号是转义写法，按裸引号查会落空；用不依赖引号的片段。
assert 'auction_ingot_listing' in repo and 'auction_ingot_txn' in repo
assert 'auction_ingot_settlement' in repo and 'set state = ' in repo
assert 'Random' not in repo
svc = open(root + '/src/Character/CharacterGrpcService.cs', encoding='utf-8').read()
assert 'SettleIngot(' in svc and 'ingots.SettleAsync' in svc
api = open(root + '/src/Gateway/AuctionApi.cs', encoding='utf-8').read()
assert 'MapPost("/auction/ingot/list"' in api and 'MapPost("/auction/ingot/buy"' in api
assert 'OwnsAsync(' in api and 'Guid.NewGuid()' in api and 'Npgsql' not in api
gl = open(root + '/src/Global/GlobalGrpcService.cs', encoding='utf-8').read()
assert 'ListIngot(' in gl and 'BuyIngot(' in gl and 'SettleIngotAsync' in gl
listing = open(root + '/src/Global/IngotListingRepository.cs', encoding='utf-8').read()
assert 'auction_ingot_listing' in listing and 'on conflict (list_txn)' in listing
program = open(root + '/src/Character/Program.cs', encoding='utf-8').read()
process = open(root + '/src/Character/CharacterProcess.cs', encoding='utf-8').read()
assert 'case "--ingot-ledger"' in program and 'new IngotRepository(' in process
# 启动日志与只读入口必须同形，否则 wait_line 等不到。收窄到拍卖那两段
# Console.WriteLine：全文件数 gold_kind= 会被强化/转移的旧日志撞到。
def block(text, anchor):
    i = text.index(anchor)
    return text[i:text.index(');', i)]
pblock = block(program, '"auction_table_state=ok version="')
cblock = block(process, '"auction_table_version="')
for name in ('tax_rate_pct=', 'tax_min=', 'tax_max=', 'gold_kind=', 'rounding=', 'ingot_kind='):
    assert pblock.count(name) == 1 and cblock.count(name) == 1, name
print('static_code=ok ingot_only=1 one_transaction=1')
PY
then ok "配置、契约、事务、进程边界与只读入口都落在指定文件里"
else bad "服务端静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 3. 拍卖配置校验：写定区间、寄售币种被改 / 税有收款方都起不来 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
base = json.load(open(root + '/config/auction.json', encoding='utf-8'))
def dump(name, obj):
    json.dump(obj, open(tmp + '/' + name, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
dump('a-good.json', base)
w = copy.deepcopy(base); w.pop('tax_rate_pct'); dump('a-no-rate.json', w)
w = copy.deepcopy(base); w['tax_rate_pct'] = 4; dump('a-low-rate.json', w)
w = copy.deepcopy(base); w['tax_rate_pct'] = 9; dump('a-high-rate.json', w)
w = copy.deepcopy(base); w['rounding'] = 'up'; dump('a-rounding.json', w)
w = copy.deepcopy(base); w['gold_kind'] = 'yuanbao'; dump('a-gold-kind.json', w)
w = copy.deepcopy(base); w['ingot_kind'] = 'gold'; dump('a-ingot-kind.json', w)
w = copy.deepcopy(base); w['ingot_kind'] = 'stone_1'; dump('a-unknown-ingot.json', w)
w = copy.deepcopy(base); w['tax_recipient'] = 'system'; dump('a-recipient.json', w)
# 坏配置的 note 里不能出现「待定」二字，否则恰好撞上校验用的关键词。
w = copy.deepcopy(base); w['note'] = '所有数值均已写定，没有需要说明的项'; dump('a-no-note.json', w)
w = copy.deepcopy(base); w['schema'] = 'auction-table/2'; dump('a-schema.json', w)
PY
config_reject() {
  local label="$1" file="$2" expect="$3" out
  out="$("$DOTNET" "$CHARACTER_DLL" --auction-state --auction-config "$TMP/$file" \
    --config config/character.json 2>&1)"
  if printf '%s' "$out" | grep -qF "$expect"; then ok "$label"; else bad "$label：${out:0:180}"; fi
}
config_reject "缺 tax_rate_pct 起不来" a-no-rate.json "config_error=auction.json 缺 tax_rate_pct"
config_reject "税率 4% 低于写定 5% 起不来" a-low-rate.json "必须落在写定的 5–8 之间"
config_reject "税率 9% 高于写定 8% 起不来" a-high-rate.json "必须落在写定的 5–8 之间"
config_reject "取整方式未支持就起不来" a-rounding.json "rounding 目前只认 down"
config_reject "结算币种不是金币起不来" a-gold-kind.json "gold_kind 必须是 gold"
config_reject "寄售币种不是元宝起不来" a-ingot-kind.json "ingot_kind 必须是 yuanbao"
config_reject "寄售币种不在币种表里起不来" a-unknown-ingot.json "ingot_kind 必须是 yuanbao"
config_reject "给税配收款方起不来" a-recipient.json "不许出现税的收款方字段"
config_reject "没写待定说明起不来" a-no-note.json "note 要写明哪些数值待定"
config_reject "schema 被改起不来" a-schema.json "schema 必须是 auction-table/1"
if "$DOTNET" "$CHARACTER_DLL" --auction-state --config config/character.json 2>&1 \
    | grep -qF 'auction_table_state=ok version=1 tax_rate_pct=6 tax_min=5 tax_max=8 gold_kind=gold rounding=down ingot_kind=yuanbao'; then
  ok "只读入口与启动共用同一套校验：好配置读出 6%、写定区间与寄售元宝"
else
  bad "好配置的只读入口没输出税率与寄售币种"
fi

if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 gamedb 凭据，连服段跑不了（这一段要在笔记本上跑）"
  finish "$TITLE"
fi

echo "== 4. 建表、播种与临时配置 =="
MIGRATE_OK=1
for sql in account.sql character.sql dungeon_entry.sql inventory.sql currency.sql transaction.sql \
           exp_settlement.sql enhance.sql transfer.sql crystal.sql; do
  if [ "$sql" = "account.sql" ]; then
    if ! "$DOTNET" "$GATEWAY_DLL" --migrate --sql "db/character/$sql" --config config/gateway.json >"$TMP/migrate-$sql.log" 2>&1; then
      MIGRATE_OK=0; bad "建表失败：$sql"; tail -8 "$TMP/migrate-$sql.log" | detail
    fi
  elif ! "$DOTNET" "$CHARACTER_DLL" --migrate --sql "db/character/$sql" --config config/character.json >"$TMP/migrate-$sql.log" 2>&1; then
    MIGRATE_OK=0; bad "建表失败：$sql"; tail -8 "$TMP/migrate-$sql.log" | detail
  fi
done
for sql in db/trade/schema.sql db/trade/auction.sql db/trade/ingot.sql; do
  if ! "$DOTNET" "$CHARACTER_DLL" --migrate --sql "$sql" --config config/character.json >"$TMP/migrate-$(basename "$sql").log" 2>&1; then
    MIGRATE_OK=0; bad "建表失败：$sql"; tail -8 "$TMP/migrate-$(basename "$sql").log" | detail
  fi
done
[ "$MIGRATE_OK" = "1" ] && ok "角色 schema 与交易 schema 的拍卖、寄售表就位"
[ "$MIGRATE_OK" = "1" ] || finish "$TITLE"
HAVE_DB=1
for acct in "$ACCOUNT" "$OTHER"; do
  printf '%s\n' "$TEST_PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$acct" --config config/gateway.json >>"$TMP/seed.log" 2>&1
done
ok "两个测试账号已播种"

ROOT="$ROOT" TMP="$TMP" CHAR_PORT="$CHAR_PORT" GATEWAY_PORT="$GATEWAY_PORT" \
GLOBAL_PORT="$GLOBAL_PORT" WORLD_PORT="$WORLD_PORT" python3 - <<'PY'
import json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
a = json.load(open(root + '/config/auction.json', encoding='utf-8'))
json.dump(a, open(tmp + '/auction.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
c = {
    'schema': 'character-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'db_pool_max': 6, 'bag_slots': 40,
    'dungeon_table': 'config/dungeons.json', 'exp_table': 'config/exp.json',
    'enhance_table': 'config/enhance.json', 'transfer_table': 'config/transfer.json',
    'crystal_table': 'config/crystal.json', 'auction_table': tmp + '/auction.json',
}
json.dump(c, open(tmp + '/character.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
g = {
    'schema': 'global-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['GLOBAL_PORT'],
    'db_pool_max': 6,
    'character_upstream': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
}
json.dump(g, open(tmp + '/global.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
gw = {
    'schema': 'gateway-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['GATEWAY_PORT'],
    'token_ttl_s': 7200, 'pbkdf2_iterations': 210000,
    'battle_upstream': 'ws://127.0.0.1:60001/battle',
    'character_upstream': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'world_upstream': 'http://127.0.0.1:' + os.environ['WORLD_PORT'],
    'global_upstream': 'http://127.0.0.1:' + os.environ['GLOBAL_PORT'],
}
json.dump(gw, open(tmp + '/gateway.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY

echo "== 5. 起角色服、全局服务与网关 =="
"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & CHARACTER_PID=$!
"$DOTNET" "$GLOBAL_DLL" --config "$TMP/global.json" >"$TMP/global.log" 2>&1 & GLOBAL_PID=$!
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >"$TMP/gateway.log" 2>&1 & GATEWAY_PID=$!
if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
   && wait_ready "$TMP/global.log" '^ready name=Global' "全局服务" \
   && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关" \
   && wait_line "$TMP/character.log" 'auction_table_version=1 tax_rate_pct=6 tax_min=5 tax_max=8'; then
  ok "三个进程起来，拍卖税率与寄售币种启动日志与只读入口同形"
else
  bad "进程没起来或启动日志缺拍卖税率"; tail -12 "$TMP/character.log" | detail; finish "$TITLE"
fi

TOKEN="$(login_token "$ACCOUNT")"
BUYER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$BUYER_TOKEN" ]; then ok "两个账号都拿到令牌"; else bad "登录失败"; finish "$TITLE"; fi
if create_char "$TOKEN" "p15b_seller" && [ -n "$NEW_ID" ]; then ID="$NEW_ID"; ok "建好卖方角色 id=$ID"; else bad "建卖方角色失败"; finish "$TITLE"; fi
if create_char "$BUYER_TOKEN" "p15b_buyer" && [ -n "$NEW_ID" ]; then BUYER_ID="$NEW_ID"; ok "建好买方角色 id=$BUYER_ID"; else bad "建买方角色失败"; finish "$TITLE"; fi

echo "== 6. 鉴权、归属与请求形状 =="
resp="$(list_ingot "" "$ID" 100 1000)"
[ "$(code_of "$resp")" = "401" ] && ok "挂单没有令牌 401" || bad "挂单没挡无令牌：$(code_of "$resp")"
resp="$(list_ingot forged "$ID" 100 1000)"
[ "$(code_of "$resp")" = "401" ] && ok "挂单伪造令牌 401" || bad "挂单没挡伪造令牌：$(code_of "$resp")"
resp="$(list_ingot "$TOKEN" "$BUYER_ID" 100 1000)"
[ "$(code_of "$resp")" = "404" ] && ok "挂别人的角色 404（网关复核归属）" || bad "挂单没核归属：$(code_of "$resp")"
resp="$(buy_ingot "" "$BUYER_ID" 1)"
[ "$(code_of "$resp")" = "401" ] && ok "购买没有令牌 401" || bad "购买没挡无令牌：$(code_of "$resp")"
resp="$(buy_ingot "$TOKEN" "$BUYER_ID" 1)"
[ "$(code_of "$resp")" = "404" ] && ok "购买别人的角色 404" || bad "购买没核归属：$(code_of "$resp")"
resp="$(list_ingot "$TOKEN" "$ID" 0 1000)"
[ "$(code_of "$resp")" = "400" ] && ok "挂单元宝数量 0 被挡：400" || bad "元宝数量形状没校验：$(body_of "$resp")"
resp="$(list_ingot "$TOKEN" "$ID" 100 0)"
[ "$(code_of "$resp")" = "400" ] && ok "标价 0 被挡" || bad "标价形状没校验：$(body_of "$resp")"

echo "== 7. 发放元宝与金币 =="
SEED_OK=1
r="$(add_currency "$TOKEN" "$ID" yuanbao 100)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "给卖方发元宝失败：$(body_of "$r")"; }
r="$(add_currency "$BUYER_TOKEN" "$BUYER_ID" gold 100000)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "给买方发金币失败：$(body_of "$r")"; }
[ "$SEED_OK" = "1" ] && ok "卖方 100 元宝，买方 100000 金币"
[ "$SEED_OK" = "1" ] || finish "$TITLE"

echo "== 8. 挂单：卖元宝、标金币 =="
resp="$(list_ingot "$TOKEN" "$ID" 100 1000)"
BODY="$(body_of "$resp")"
LISTING="$(json_field "$BODY" listing_id)"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true reason= seller_id="$ID" ingot_amount=100 price_gold=1000 state=listed duplicate=false)" = "ok" ] \
   && [ -n "$LISTING" ] && [ "$LISTING" != "0" ]; then
  ok "卖方挂单成功：listing=${LISTING}，100 元宝标价 1000 金币"
else
  bad "挂单没按预期：$BODY"; finish "$TITLE"
fi
BEFORE="$(ledger "$LISTING")"
if [ -n "$BEFORE" ] && [ "$(field_of "$BEFORE" state)" = "listed" ]; then
  ok "库里挂单是 listed，成交前没有任何流水"
else
  bad "成交前库状态读不出来：$BEFORE"; finish "$TITLE"
fi

echo "== 9. 完成标准一：元宝换成金币，卖方少掉一笔税 =="
resp="$(buy_ingot "$BUYER_TOKEN" "$BUYER_ID" "$LISTING")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true reason= listing_id="$LISTING" ingot_amount=100 price_gold=1000 tax_gold=60 net_gold=940 buyer_gold=99000 seller_gold=940 buyer_ingot=100 seller_ingot=0 tax_rate_pct=6 state=sold duplicate=false)" = "ok" ]; then
  ok "成交：买方 -1000 金币 +100 元宝，卖方 -100 元宝 +940 金币，销毁 60"
else
  bad "成交没按预期：$BODY"; finish "$TITLE"
fi
TXN="$(grep -o 'txn=auction_ingot_buy:[a-f0-9]*' "$TMP/gateway.log" | tail -1 | cut -d= -f2)"
AFTER="$(ledger "$LISTING" "$TXN")"
if [ -n "$AFTER" ] \
   && [ "$(field_of "$AFTER" price)" = "1000" ] \
   && [ "$(field_of "$AFTER" tax)" = "60" ] \
   && [ "$(field_of "$AFTER" net)" = "940" ] \
   && [ "$(field_of "$AFTER" state)" = "sold" ] \
   && [ "$(field_of "$AFTER" settlements)" = "1" ] \
   && [ "$(field_of "$AFTER" txn)" = "1" ]; then
  ok "库里流水恰好一行：price=1000 tax=60 net=940，事务号恰好一行"
else
  bad "库里成交流水不对：$AFTER"
fi

echo "== 10. 完成标准二：元宝等量转移，税不进任何背包 =="
if BEFORE="$BEFORE" AFTER="$AFTER" python3 - >"$TMP/burn.out" 2>&1 <<'PY'
import os, re
b, a = os.environ['BEFORE'], os.environ['AFTER']
def f(line, key):
    m = re.search(r'\b' + key + r'=([^ ]*)', line)
    assert m, (key, line)
    return m.group(1)
def accounts(line):
    return {int(k): int(v) for k, v in
            (p.split(':') for p in re.search(r'accounts=(.*)$', line).group(1).split(',') if p)}
price, tax, net = int(f(a, 'price')), int(f(a, 'tax')), int(f(a, 'net'))
assert price == 1000 and tax == 60 and net == 940
assert net + tax == price, '净额与税额要正好凑回标价'
# 元宝等量转移，全库总量不变。
assert int(f(a, 'total_ingot')) == int(f(b, 'total_ingot')), '元宝总量必须不变'
assert int(f(a, 'buyer_ingot')) - int(f(b, 'buyer_ingot')) == 100
assert int(f(a, 'seller_ingot')) - int(f(b, 'seller_ingot')) == -100
# 金币只少这一笔税。
assert int(f(a, 'total_gold')) == int(f(b, 'total_gold')) - tax, '全库金币只少了这一笔税'
ba, aa = accounts(b), accounts(a)
buyer, seller = int(f(a, 'buyer')), int(f(a, 'seller'))
# 卖方成交前没有金币行，成交后会多出一行；多出来的只能是卖方。
assert set(aa) <= set(ba) | {seller}, '不许冒出系统/税账户'
for who in set(ba) | set(aa):
    diff = aa.get(who, 0) - ba.get(who, 0)
    if who == buyer:
        assert diff == -price, ('买方', diff)
    elif who == seller:
        assert diff == net, ('卖方', diff)
    else:
        assert diff == 0, ('第三方收到钱了', who, diff)
assert net == price - tax, '税不能进任何角色，只能销毁'
print('ingot_transfer=ok tax_burn=ok no_recipient=1 gold_delta=%d' % -tax)
PY
then ok "元宝总量不变、买卖双方等量一进一出；全库金币只少 60，没冒出系统/税账户"
else bad "元宝没等量转移或税进了别的背包"; cat "$TMP/burn.out" | detail
fi

echo "== 11. 业务拒绝：重放、自己买、不存在、元宝/金币不足 =="
SAME_BEFORE="$(ledger "$LISTING" "$TXN")"
resp="$(buy_ingot "$BUYER_TOKEN" "$BUYER_ID" "$LISTING")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=false reason=listing_sold price_gold=0 tax_gold=0 net_gold=0 buyer_gold=0 seller_gold=0 state= duplicate=false)" = "ok" ] \
   && [ "$(ledger "$LISTING" "$TXN")" = "$SAME_BEFORE" ]; then
  ok "同一挂单再买被拒：ok=false + listing_sold，未生效数值为 0，账户一格没动"
else
  bad "重复成交没被挡：$BODY"
fi
resp="$(buy_ingot "$BUYER_TOKEN" "$BUYER_ID" 999999999)"
[ "$(expect_json "$(body_of "$resp")" ok=false reason=listing_not_found tax_gold=0)" = "ok" ] \
  && ok "不存在的挂单被拒 listing_not_found" || bad "不存在的挂单没被挡：$(body_of "$resp")"
resp="$(list_ingot "$TOKEN" "$ID" 10 500)"
L2="$(json_field "$(body_of "$resp")" listing_id)"
resp="$(buy_ingot "$TOKEN" "$ID" "$L2")"
[ "$(expect_json "$(body_of "$resp")" ok=false reason=same_character tax_gold=0)" = "ok" ] \
  && ok "买卖同一人被拒 same_character" || bad "自己买自己没被挡：$(body_of "$resp")"
# 卖方成交后元宝已清零：挂 1000 元宝能挂上，成交时元宝不足整笔回滚。
resp="$(list_ingot "$TOKEN" "$ID" 1000 500)"
L3="$(json_field "$(body_of "$resp")" listing_id)"
INGOT_BEFORE="$(field_of "$(ledger "$L3")" seller_ingot)"
resp="$(buy_ingot "$BUYER_TOKEN" "$BUYER_ID" "$L3")"
BODY="$(body_of "$resp")"
if [ "$(expect_json "$BODY" ok=false reason=ingot_insufficient tax_gold=0 net_gold=0)" = "ok" ] \
   && [ "$(field_of "$(ledger "$L3")" seller_ingot)" = "$INGOT_BEFORE" ] \
   && [ "$(field_of "$(ledger "$L3")" state)" = "listed" ]; then
  ok "卖方元宝不足整笔回滚：挂单仍 listed，元宝、金币、流水都不动"
else
  bad "元宝不足没整笔回滚：$BODY"
fi
# 给卖方补 10 元宝，验金币不足：卖方出得起元宝，买方出不起标价。
r="$(add_currency "$TOKEN" "$ID" yuanbao 10)"
[ "$(code_of "$r")" = "200" ] || bad "给卖方补元宝失败：$(body_of "$r")"
resp="$(list_ingot "$TOKEN" "$ID" 10 99999999)"
L4="$(json_field "$(body_of "$resp")" listing_id)"
GOLD_BEFORE="$(field_of "$(ledger "$L4")" buyer_gold)"
resp="$(buy_ingot "$BUYER_TOKEN" "$BUYER_ID" "$L4")"
BODY="$(body_of "$resp")"
if [ "$(expect_json "$BODY" ok=false reason=gold_insufficient tax_gold=0 net_gold=0)" = "ok" ] \
   && [ "$(field_of "$(ledger "$L4")" buyer_gold)" = "$GOLD_BEFORE" ] \
   && [ "$(field_of "$(ledger "$L4")" state)" = "listed" ]; then
  ok "买方金币不足整笔回滚：挂单仍 listed，元宝、金币、流水都不动"
else
  bad "金币不足没整笔回滚：$BODY"
fi

echo "== 12. 改税率重启生效，伪造字段不影响结果 =="
if restart_character 8; then ok "把税率改成 8% 重启角色服，两条 gRPC 子通道已重连"; \
else bad "改税率重启后链路没通"; tail -12 "$TMP/global.log" | detail; fi
resp="$(list_ingot "$TOKEN" "$ID" 10 1000)"
L5="$(json_field "$(body_of "$resp")" listing_id)"
FAKE=',"tax_gold":999999,"net_gold":999999,"price_gold":1,"tax_rate_pct":1,"seller_id":1,"state":"sold","txn_id":"forged","account":"forged","duplicate":true,"ingot_amount":999999'
resp="$(buy_ingot "$BUYER_TOKEN" "$BUYER_ID" "$L5" "$FAKE")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true ingot_amount=10 price_gold=1000 tax_gold=80 net_gold=920 tax_rate_pct=8 duplicate=false state=sold)" = "ok" ]; then
  ok "伪造税率 / 税额 / 元宝数 / 到账 / 事务号 / 账号全无效：按服务端 8% 算 80 税"
else
  bad "伪造字段影响了结果：$BODY"
fi
if restart_character 6; then ok "税率改回 6% 重启恢复现场"; else bad "税率没恢复"; fi

echo "== 13. 老规则不回退 =="
# P1-5a 物品拍卖仍能挂单成交（元宝寄售是新增，不改旧表与旧规则）。
r="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/inventory/add?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID},\"slot\":1,\"item_id\":\"ore_iron\",\"count\":10}")"
[ "$(code_of "$r")" = "200" ] && ok "P1-5a：物品拍卖所需材料仍能发放" || bad "物品发放失败：$(body_of "$r")"
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/auction/list?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID},\"slot\":1,\"count\":2,\"price_gold\":1000}")"
L6="$(json_field "$(body_of "$resp")" listing_id)"
resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/auction/buy?token=${BUYER_TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${BUYER_ID},\"listing_id\":${L6}}")"
[ "$(expect_json "$(body_of "$resp")" ok=true tax_gold=60 net_gold=940)" = "ok" ] \
  && ok "P1-5a：物品拍卖成交仍按 6% 抽税，4 表规则不回退" || bad "物品拍卖回退：$(body_of "$resp")"
resp="$(add_currency "$BUYER_TOKEN" "$BUYER_ID" gold -999999999)"
[ "$(code_of "$resp" | tr -d '\n')" != "200" ] \
  && ok "P1-1b4：金币扣成负数仍被拒" || bad "金币可以扣成负数：$(body_of "$resp")"

finish "$TITLE"
