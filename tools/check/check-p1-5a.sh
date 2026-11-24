#!/usr/bin/env bash
# P1-5a 核对：拍卖和销毁税（方案第 12 章、开发架构第 14 节第 39 行）。
#
# 完成标准：卖方少掉一笔税，这笔税不进任何背包。第 11 节 P1-5 与第 13 节
# P1-5a 还写定：金币标价、成交销毁配置中 5%–8% 的金币、不做成商品。
#
# 两种模式：
#   bash tools/check/check-p1-5a.sh                          服务端段：编译、静态、连库连服
#   MMO_P15A_MODE=client bash tools/check/check-p1-5a.sh    客户端段：静态 + Unity 编辑器自检
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
AGG_CHARPROC="$(src_agg src/Character/CharacterProcess.cs)"; export AGG_CHARPROC
AGG_CHARGRPC="$(src_agg src/Character/CharacterGrpcService.cs)"; export AGG_CHARGRPC

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
MODE="${MMO_P15A_MODE:-server}"

CHARACTER_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
GLOBAL_DLL="$ROOT/src/Global/bin/Release/net10.0/Global.dll"

CHAR_PORT=${MMO_P15A_CHAR_PORT:-62122}
GATEWAY_PORT=${MMO_P15A_GATEWAY_PORT:-61123}
GLOBAL_PORT=${MMO_P15A_GLOBAL_PORT:-64023}
WORLD_PORT=${MMO_P15A_WORLD_PORT:-63099}
ACCOUNT="p15a_seller"
OTHER="p15a_buyer"
TEST_PASSWORD="p15a-Pw-8d20"
CHAR_NAMES=("p15a_seller" "p15a_buyer")

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

add_item() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/inventory/add?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$2,\"slot\":$3,\"item_id\":\"$4\",\"count\":$5}"
}

add_gold() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/currency/add?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$2,\"kind\":\"gold\",\"delta\":$3}"
}

list_auction() {
  local token="$1" char="$2" slot="$3" count="$4" price="$5" extra="${6:-}"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/auction/list?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"slot\":${slot},\"count\":${count},\"price_gold\":${price}${extra}}"
}

buy_auction() {
  local token="$1" char="$2" listing="$3" extra="${4:-}"
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/auction/buy?token=${token}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${char},\"listing_id\":${listing}${extra}}"
}

# 不看回包，从库里独立读回买卖双方金币、全库金币分布、挂单状态与成交流水。
ledger() {
  "$DOTNET" "$CHARACTER_DLL" --auction-ledger "$BUYER_ID" "$ID" "${1:-0}" "${2:-}" \
    --config "$TMP/character.json" 2>/dev/null | grep '^auction_state=ok '
}

field_of() { printf '%s' "$1" | sed -n 's/.* '"$2"'=\([^ ]*\).*/\1/p'; }

# 只改税率重启角色服。网关到角色服、全局服务到角色服两条 gRPC 子通道都要重连，
# ready 日志不够：用一个会走到角色服的只读业务请求等链路真的通。
restart_character() {
  local rate="$1" out probe
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
      "http://127.0.0.1:${GATEWAY_PORT}/auction/buy?token=${BUYER_TOKEN}" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":${BUYER_ID},\"listing_id\":999999999}")"
    printf '%s' "$probe" | grep -q 'listing_not_found' && return 0
    sleep 0.25
  done
  return 1
}

TITLE="P1-5a（${MODE}）"

# ---------------------------------------------------------------- 客户端段
if [ "$MODE" = "client" ]; then
  echo "== C1. 拍卖配置、契约与客户端静态断言 =="
  if ROOT="$ROOT" python3 - >"$TMP/client-static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/auction.json', encoding='utf-8'))
assert 5 <= cfg['tax_rate_pct'] <= 8
assert cfg['rounding'] == 'down' and cfg['gold_kind'] == 'gold'
assert '待定' in cfg['note'] and '直接销毁' in cfg['note']
assert not any('recipient' in k or 'tax_account' in k for k in cfg)
trade = open(root + '/proto/mmo/trade.proto', encoding='utf-8').read()
chrp = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
fields = lambda b: set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*=', b))
body = lambda s, m: re.search(r'message ' + m + r' \{(.*?)\}', s, re.S).group(1)
assert fields(body(trade, 'ListAuctionRequest')) == {
    'account', 'character_id', 'slot', 'count', 'price_gold', 'txn_id'}
assert fields(body(trade, 'BuyAuctionRequest')) == {
    'account', 'character_id', 'listing_id', 'txn_id'}
assert fields(body(chrp, 'AuctionSettleRequest')) == {
    'account', 'character_id', 'listing_id', 'txn_id'}
assert {'tax_gold', 'net_gold', 'price_gold'} <= fields(body(trade, 'BuyAuctionResponse'))
client = open(root + '/client/MMOGame/Assets/Net/CharacterAuction.cs', encoding='utf-8').read()
assert 'ListBody(ulong characterId, uint slot, uint count, long priceGold)' in client
assert 'BuyBody(ulong characterId, ulong listingId)' in client
assert 'AuctionTable' not in client and 'Random' not in client and 'JsonUtility' not in client
assert 'txn_id' not in client and 'auction.json' not in client
repo = open(root + '/src/Character/AuctionRepository.cs', encoding='utf-8').read()
assert 'BeginTransactionAsync' in repo and 'Random' not in repo
assert 'auction_txn' in repo and 'auction_settlement' in repo
print('client_static=ok tax_burn_only=1')
PY
  then ok "写定区间只在配置、请求只带格位与标价、成交不掷点"
  else bad "客户端静态断言失败"; cat "$TMP/client-static.out" | detail
  fi

  echo "== C2. Unity 编辑器自检：拍卖请求与回包拆装 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    finish "$TITLE"
  fi
  if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod P15aCheck.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity P15aCheck.Run 退出码 0"
  else
    bad "Unity P15aCheck.Run 失败"
    tail -25 "$TMP/unity.log" | detail
  fi
  for key in config proto wire boundary; do
    if grep -q "p15a_${key}=ok" "$TMP/unity.log"; then
      ok "编辑器自检 p15a_${key}=ok"
    else
      bad "编辑器自检 p15a_${key} 没过"
      grep "p15a_${key}" "$TMP/unity.log" | tail -2 | detail
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
if [ "$BUILD_OK" = "1" ]; then ok "六个服务端工程编译通过（全局服务第一次开 gRPC）"; else finish "$TITLE"; fi

echo "== 2. 静态断言（服务端） =="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/auction.json', encoding='utf-8'))
assert 5 <= cfg['tax_rate_pct'] <= 8 and cfg['gold_kind'] == 'gold'
assert '待定' in cfg['note'] and '直接销毁' in cfg['note']
assert not any('recipient' in k or 'tax_account' in k for k in cfg)
trade = open(root + '/proto/mmo/trade.proto', encoding='utf-8').read()
chrp = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
fields = lambda b: set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*=', b))
body = lambda s, m: re.search(r'message ' + m + r' \{(.*?)\}', s, re.S).group(1)
assert fields(body(trade, 'ListAuctionRequest')) == {
    'account', 'character_id', 'slot', 'count', 'price_gold', 'txn_id'}
assert fields(body(trade, 'BuyAuctionRequest')) == {
    'account', 'character_id', 'listing_id', 'txn_id'}
assert fields(body(chrp, 'AuctionSettleRequest')) == {
    'account', 'character_id', 'listing_id', 'txn_id'}
assert re.search(r'rpc SettleAuction\(AuctionSettleRequest\) returns \(AuctionSettleResponse\)', chrp)
sql = open(root + '/db/trade/auction.sql', encoding='utf-8').read()
assert 'CREATE TABLE IF NOT EXISTS "交易".auction_listing' in sql
assert 'CREATE TABLE IF NOT EXISTS "交易".auction_txn' in sql
assert 'CREATE TABLE IF NOT EXISTS "交易".auction_settlement' in sql
assert 'CHECK (net_gold + tax_gold = price_gold)' in sql
table = open(root + '/src/Character/AuctionTable.cs', encoding='utf-8').read()
assert 'WrittenMinRatePct = 5' in table and 'WrittenMaxRatePct = 8' in table
assert '不许出现税的收款方字段' in table
repo = open(root + '/src/Character/AuctionRepository.cs', encoding='utf-8').read()
assert 'BeginTransactionAsync' in repo and 'for update' in repo
for code in ('listing_not_found', 'listing_sold', 'same_character', 'bound_item',
             'gold_insufficient', 'bag_full', 'duplicate'):
    assert code in repo, code
# C# 源码里 SQL 的引号是转义写法，按裸引号查会落空；用不依赖引号的片段。
assert 'delete from' in repo and '.auction_listing' in repo
assert 'set state = ' in repo and 'auction_settlement' in repo
assert 'Random' not in repo
svc = open(os.environ["AGG_CHARGRPC"], encoding='utf-8').read()
assert 'SettleAuction(' in svc and 'auctions.SettleAsync' in svc
api = open(root + '/src/Gateway/AuctionApi.cs', encoding='utf-8').read()
assert 'MapPost("/auction/list"' in api and 'MapPost("/auction/buy"' in api
assert 'OwnsAsync(' in api and 'Guid.NewGuid()' in api and 'Npgsql' not in api
gl = open(root + '/src/Global/GlobalGrpcService.cs', encoding='utf-8').read()
assert 'CanTradeAsync' in gl and 'SettleAuctionAsync' in gl
program = open(root + '/src/Character/Program.cs', encoding='utf-8').read() + ''.join(
    open(p, encoding='utf-8').read()
    for p in sorted(__import__('glob').glob(root + '/tools/probe/CliProbe/*.cs')))
process = open(os.environ["AGG_CHARPROC"], encoding='utf-8').read()
assert 'case "--auction-state"' in program and 'case "--auction-ledger"' in program
# 启动日志与只读入口必须同形，否则 wait_line 等不到。收窄到拍卖那两段
# Console.WriteLine：全文件数 gold_kind= 会被强化/转移的旧日志撞到。
def block(text, anchor):
    i = text.index(anchor)
    return text[i:text.index(');', i)]
pblock = block(program, '"auction_table_state=ok version="')
cblock = block(process, '"auction_table_version="')
for name in ('tax_rate_pct=', 'tax_min=', 'tax_max=', 'gold_kind=', 'rounding='):
    assert pblock.count(name) == 1 and cblock.count(name) == 1, name
print('static_code=ok tax_burn_only=1 one_transaction=1')
PY
then ok "配置、契约、事务、进程边界与只读入口都落在指定文件里"
else bad "服务端静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 3. 拍卖配置校验：写定区间被改 / 税有收款方都起不来 =="
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
w = copy.deepcopy(base); w['tax_recipient'] = 'system'; dump('a-recipient.json', w)
# 坏配置的 note 里不能出现「待定」二字，否则恰好撞上校验用的关键词。
w = copy.deepcopy(base); w['note'] = '所有数值均已写定，没有需要说明的项'; dump('a-no-note.json', w)
w = copy.deepcopy(base); w['schema'] = 'auction-table/2'; dump('a-schema.json', w)
PY
# Windows 上 dotnet 把 stdout 重定向到管道时走 GBK，中文坏配置断言按平台转码。
# 只转编码，不改任何一条断言；非 Windows 原样透传（与 check-p3-1a2.sh 同一做法）。
utf8() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      python3 -c 'import sys; sys.stdout.buffer.write(
          sys.stdin.buffer.read().decode("gbk", "replace").encode("utf-8"))' ;;
    *) cat ;;
  esac
}
config_reject() {
  local label="$1" file="$2" expect="$3" out
  out="$("$DOTNET" "$CHARACTER_DLL" --auction-state --auction-config "$TMP/$file" \
    --config config/character.json 2>&1 | utf8)"
  if printf '%s' "$out" | grep -qF "$expect"; then ok "$label"; else bad "${label}：${out:0:180}"; fi
}
config_reject "缺 tax_rate_pct 起不来" a-no-rate.json "config_error=auction.json 缺 tax_rate_pct"
config_reject "税率 4% 低于写定 5% 起不来" a-low-rate.json "必须落在写定的 5–8 之间"
config_reject "税率 9% 高于写定 8% 起不来" a-high-rate.json "必须落在写定的 5–8 之间"
config_reject "取整方式未支持就起不来" a-rounding.json "rounding 目前只认 down"
config_reject "标价币种不是金币起不来" a-gold-kind.json "gold_kind 必须是 gold"
config_reject "给税配收款方起不来" a-recipient.json "不许出现税的收款方字段"
config_reject "没写待定说明起不来" a-no-note.json "note 要写明哪些数值待定"
config_reject "schema 被改起不来" a-schema.json "schema 必须是 auction-table/1"
if "$DOTNET" "$CHARACTER_DLL" --auction-state --config config/character.json 2>&1 \
    | grep -qF 'auction_table_state=ok version=1 tax_rate_pct=6 tax_min=5 tax_max=8 gold_kind=gold rounding=down'; then
  ok "只读入口与启动共用同一套校验：好配置读出 6% 与写定区间"
else
  bad "好配置的只读入口没输出税率信息"
fi

echo "== 3b. B5 摆摊：配置、契约与进程边界 =="
if ROOT="$ROOT" python3 - >"$TMP/stall-static.out" 2>&1 <<'PY'
import glob, json, os
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/stall.json', encoding='utf-8'))
assert cfg['schema'] == 'stall-table/1'
assert 5 <= cfg['tax_rate_pct'] <= 8
assert cfg['slots'] == 8 and cfg['min_level'] == 20 and cfg['cross_server'] is False
assert '待定' in cfg['note']
assert not any('recipient' in k or 'tax_account' in k for k in cfg)
man = json.load(open(root + '/db/migrations.json', encoding='utf-8'))
trade = [x for x in man['schemas'] if x['key'] == 'trade'][0]
assert any(e['file'] == 'db/trade/stall.sql' for e in trade['entries'])
sql = open(root + '/db/trade/stall.sql', encoding='utf-8').read()
assert 'ADD COLUMN IF NOT EXISTS channel' in sql
assert 'ADD COLUMN IF NOT EXISTS stall_id' in sql
assert 'CREATE TABLE IF NOT EXISTS "交易".stall' in sql
main = open(root + '/db/trade/schema.sql', encoding='utf-8').read()
assert 'CREATE TABLE IF NOT EXISTS "交易".stall' in main
table = open(root + '/src/Character/StallTable.cs', encoding='utf-8').read()
assert 'WrittenMinRatePct = 5' in table and 'WrittenMaxRatePct = 8'
assert 'cross_server 只能是 false' in table
repo = open(root + '/src/Character/StallRepository.cs', encoding='utf-8').read()
assert 'BeginTransactionAsync' in repo and 'for update' in repo
for code in ('level_too_low', 'stall_exists', 'not_owner', 'stall_closed',
             'stall_full', 'slot_empty', 'bound_item', 'slot_listed'):
    assert code in repo, code
assert '.auction_listing' in repo and "'stall'" in repo
auction = open(root + '/src/Character/AuctionRepository.cs', encoding='utf-8').read()
assert 'stalls.TaxRatePct' in auction and 'table.TaxRatePct' in auction
assert 'channel' in auction
api = open(root + '/src/Gateway/StallApi.cs', encoding='utf-8').read()
for route in ('/stall/open', '/stall/close', '/stall/list', '/stall/view',
              '/stalls', '/stall/buy'):
    assert '"' + route + '"' in api, route
# 成交仍提交同一交易事务：网关调的是拍卖那条链，不是自己写库。
assert 'BuyAuctionAsync(' in api and 'Npgsql' not in api
chrp = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
for rpc in ('OpenStall', 'CloseStall', 'ListStallItem', 'ViewStall'):
    assert 'rpc ' + rpc + '(' in chrp, rpc
worldp = open(root + '/proto/mmo/world.proto', encoding='utf-8').read()
for rpc in ('OpenStall', 'RenewStall', 'CloseStall', 'ListStalls'):
    assert 'rpc ' + rpc + '(' in worldp, rpc
# 世界服不碰角色库、不改背包：整个 src/World 不许出现 Npgsql 或背包写入。
world = ''.join(open(x, encoding='utf-8').read()
                for x in sorted(glob.glob(root + '/src/World/**/*.cs', recursive=True))
                if '/obj/' not in x and '/bin/' not in x)
assert 'Npgsql' not in world
assert '"角色".inventory' not in world and 'update "角色"' not in world
assert 'StallRegistry' in world and 'ListStalls' in world
print('stall_static=ok one_transaction=1 world_no_db=1')
PY
then ok "摆摊：配置区间、库表、四条 rpc、成交复用拍卖事务、世界服不碰库"
else bad "摆摊静态断言失败"; cat "$TMP/stall-static.out" | detail
fi

echo "== 3c. B5 摆摊配置校验：默认值与写定口径被改都起不来 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
base = json.load(open(root + '/config/stall.json', encoding='utf-8'))
def dump(name, obj):
    json.dump(obj, open(tmp + '/' + name, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
dump('s-good.json', base)
w = copy.deepcopy(base); w.pop('tax_rate_pct'); dump('s-no-rate.json', w)
w = copy.deepcopy(base); w['tax_rate_pct'] = 4; dump('s-low-rate.json', w)
w = copy.deepcopy(base); w['slots'] = 9; dump('s-slots.json', w)
w = copy.deepcopy(base); w['min_level'] = 101; dump('s-level.json', w)
w = copy.deepcopy(base); w['cross_server'] = True; dump('s-cross.json', w)
w = copy.deepcopy(base); w['tax_account'] = 'system'; dump('s-recipient.json', w)
w = copy.deepcopy(base); w['note'] = '所有数值均已写定，没有需要说明的项'; dump('s-no-note.json', w)
w = copy.deepcopy(base); w['schema'] = 'stall-table/2'; dump('s-schema.json', w)
PY
stall_reject() {
  local label="$1" file="$2" expect="$3" out
  out="$("$DOTNET" "$CHARACTER_DLL" --stall-config --stall-table-path "$TMP/$file" \
    --config config/character.json 2>&1 | utf8)"
  if printf '%s' "$out" | grep -qF "$expect"; then ok "$label"; else bad "${label}：${out:0:180}"; fi
}
stall_reject "B5 缺 tax_rate_pct 起不来" s-no-rate.json "config_error=stall.json 缺 tax_rate_pct"
stall_reject "B5 税率 4% 低于写定 5% 起不来" s-low-rate.json "必须落在写定的 5–8 之间"
stall_reject "B5 格数 9 超过写定 8 起不来" s-slots.json "slots 必须是 1–8"
stall_reject "B5 等级门槛 101 越界起不来" s-level.json "min_level 必须是 1–100"
stall_reject "B5 改成跨服起不来" s-cross.json "cross_server 只能是 false"
stall_reject "B5 给税配收款方起不来" s-recipient.json "不许出现税的收款方字段"
stall_reject "B5 没写待定说明起不来" s-no-note.json "note 要写明哪些数值待定"
stall_reject "B5 schema 被改起不来" s-schema.json "schema 必须是 stall-table/1"
if "$DOTNET" "$CHARACTER_DLL" --stall-config --config config/character.json 2>&1 \
    | grep -qF 'stall_config=ok version=1 pending=true tax_rate_pct=5'; then
  ok "B5 只读入口与启动同一套校验：默认 5% / 8 格 / 20 级 / 不跨服"
else
  bad "B5 只读入口没输出默认值"
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
for sql in db/trade/schema.sql db/trade/auction.sql; do
  if ! "$DOTNET" "$CHARACTER_DLL" --migrate --sql "$sql" --config config/character.json >"$TMP/migrate-$(basename "$sql").log" 2>&1; then
    MIGRATE_OK=0; bad "建表失败：$sql"; tail -8 "$TMP/migrate-$(basename "$sql").log" | detail
  fi
done
[ "$MIGRATE_OK" = "1" ] && ok "角色 schema 与交易 schema 的拍卖表就位"
[ "$MIGRATE_OK" = "1" ] || finish "$TITLE"
HAVE_DB=1
for acct in "$ACCOUNT" "$OTHER"; do
  printf '%s\n' "$TEST_PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$acct" --seed-slots 8 --config config/gateway.json >>"$TMP/seed.log" 2>&1
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
a = {
    'schema': 'gateway-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['GATEWAY_PORT'],
    'token_ttl_s': 7200, 'pbkdf2_iterations': 210000,
    'battle_upstream': 'ws://127.0.0.1:60001/battle',
    'character_upstream': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'world_upstream': 'http://127.0.0.1:' + os.environ['WORLD_PORT'],
    'global_upstream': 'http://127.0.0.1:' + os.environ['GLOBAL_PORT'],
}
json.dump(a, open(tmp + '/gateway.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY

echo "== 5. 起角色服、全局服务与网关 =="
"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & CHARACTER_PID=$!
"$DOTNET" "$GLOBAL_DLL" --config "$TMP/global.json" >"$TMP/global.log" 2>&1 & GLOBAL_PID=$!
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >"$TMP/gateway.log" 2>&1 & GATEWAY_PID=$!
if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
   && wait_ready "$TMP/global.log" '^ready name=Global' "全局服务" \
   && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关" \
   && wait_line "$TMP/character.log" 'auction_table_version=1 tax_rate_pct=6 tax_min=5 tax_max=8'; then
  ok "三个进程起来，拍卖税率启动日志与只读入口同形"
else
  bad "进程没起来或启动日志缺拍卖税率"; tail -12 "$TMP/character.log" | detail; finish "$TITLE"
fi

TOKEN="$(login_token "$ACCOUNT")"
BUYER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$BUYER_TOKEN" ]; then ok "两个账号都拿到令牌"; else bad "登录失败"; finish "$TITLE"; fi
if create_char "$TOKEN" "p15a_seller" && [ -n "$NEW_ID" ]; then ID="$NEW_ID"; ok "建好卖方角色 id=$ID"; else bad "建卖方角色失败"; finish "$TITLE"; fi
if create_char "$BUYER_TOKEN" "p15a_buyer" && [ -n "$NEW_ID" ]; then BUYER_ID="$NEW_ID"; ok "建好买方角色 id=$BUYER_ID"; else bad "建买方角色失败"; finish "$TITLE"; fi

echo "== 6. 鉴权、归属与请求形状 =="
resp="$(list_auction "" "$ID" 1 2 1000)"
[ "$(code_of "$resp")" = "401" ] && ok "挂单没有令牌 401" || bad "挂单没挡无令牌：$(code_of "$resp")"
resp="$(list_auction forged "$ID" 1 2 1000)"
[ "$(code_of "$resp")" = "401" ] && ok "挂单伪造令牌 401" || bad "挂单没挡伪造令牌：$(code_of "$resp")"
resp="$(list_auction "$TOKEN" "$BUYER_ID" 1 2 1000)"
[ "$(code_of "$resp")" = "404" ] && ok "挂别人的角色 404（网关复核归属）" || bad "挂单没核归属：$(code_of "$resp")"
resp="$(buy_auction "" "$BUYER_ID" 1)"
[ "$(code_of "$resp")" = "401" ] && ok "购买没有令牌 401" || bad "购买没挡无令牌：$(code_of "$resp")"
resp="$(buy_auction "$TOKEN" "$BUYER_ID" 1)"
[ "$(code_of "$resp")" = "404" ] && ok "购买别人的角色 404" || bad "购买没核归属：$(code_of "$resp")"
resp="$(list_auction "$TOKEN" "$ID" 0 2 1000)"
[ "$(code_of "$resp")" = "400" ] && ok "挂单 slot=0 被挡：400 + bad_request" || bad "挂单格位形状没校验：$(body_of "$resp")"
resp="$(list_auction "$TOKEN" "$ID" 1 0 1000)"
[ "$(code_of "$resp")" = "400" ] && ok "挂单 count=0 被挡" || bad "挂单数量形状没校验：$(body_of "$resp")"
resp="$(list_auction "$TOKEN" "$ID" 1 2 0)"
[ "$(code_of "$resp")" = "400" ] && ok "标价 0 被挡" || bad "标价形状没校验：$(body_of "$resp")"

echo "== 7. 发放物品与金币 =="
SEED_OK=1
r="$(add_item "$TOKEN" "$ID" 1 ore_iron 100)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "给卖方发矿石失败：$(body_of "$r")"; }
r="$(add_item "$TOKEN" "$ID" 2 sword_trial 1)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "给卖方发绑定装备失败：$(body_of "$r")"; }
# 每个用例用独立格位：同一卖方的同一格同时只能挂一单（部分唯一索引），
# 拒绝用例留下的 listed 单不会挡住后面的用例。
for slot in 3 4 5; do
  r="$(add_item "$TOKEN" "$ID" "$slot" ore_iron 100)"
  [ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "给卖方 slot $slot 发矿石失败：$(body_of "$r")"; }
done
r="$(add_gold "$BUYER_TOKEN" "$BUYER_ID" 100000)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "给买方发金币失败：$(body_of "$r")"; }
[ "$SEED_OK" = "1" ] && ok "卖方四格非绑矿石、1 件绑定装备，买方 100000 金币"
[ "$SEED_OK" = "1" ] || finish "$TITLE"

echo "== 8. 挂单：金币标价，物品编号由服务端读 =="
resp="$(list_auction "$TOKEN" "$ID" 1 2 1000)"
BODY="$(body_of "$resp")"
LISTING="$(json_field "$BODY" listing_id)"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true reason= seller_id="$ID" slot=1 count=2 price_gold=1000 state=listed duplicate=false)" = "ok" ] \
   && [ -n "$LISTING" ] && [ "$LISTING" != "0" ]; then
  ok "卖方挂单成功：listing=${LISTING}，标价 1000 金币"
else
  bad "挂单没按预期：$BODY"; finish "$TITLE"
fi
BEFORE="$(ledger "$LISTING")"
if [ -n "$BEFORE" ] && [ "$(field_of "$BEFORE" state)" = "listed" ]; then
  ok "库里挂单是 listed，成交前没有任何流水"
else
  bad "成交前库状态读不出来：$BEFORE"; finish "$TITLE"
fi

echo "== 9. 完成标准一：卖方少掉一笔税 =="
resp="$(buy_auction "$BUYER_TOKEN" "$BUYER_ID" "$LISTING")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true reason= listing_id="$LISTING" price_gold=1000 tax_gold=60 net_gold=940 buyer_gold=99000 seller_gold=940 tax_rate_pct=6 item_id=ore_iron count=2 state=sold duplicate=false)" = "ok" ]; then
  ok "成交：标价 1000、销毁 60、卖方到账 940，买方扣 1000"
else
  bad "成交没按预期：$BODY"; finish "$TITLE"
fi
TXN="$(grep -o 'txn=auction_buy:[a-f0-9]*' "$TMP/gateway.log" | tail -1 | cut -d= -f2)"
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

echo "== 10. 完成标准二：这笔税不进任何背包 =="
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
assert int(f(a, 'total_items')) == int(f(b, 'total_items')), '税不生成物品'
assert int(f(a, 'buyer_items')) - int(f(b, 'buyer_items')) == 2
assert int(f(a, 'seller_items')) - int(f(b, 'seller_items')) == -2
print('tax_burn=ok no_recipient=1 gold_delta=%d' % -tax)
PY
then ok "全库金币只少 60，除买方 -1000 / 卖方 +940 外每个角色都没动，物品总量不变"
else bad "税进了别的背包或金币总量不对"; cat "$TMP/burn.out" | detail
fi

echo "== 11. 业务拒绝：重放、自己买、不存在、余额不足、绑定与空格 =="
SAME_BEFORE="$(ledger "$LISTING" "$TXN")"
resp="$(buy_auction "$BUYER_TOKEN" "$BUYER_ID" "$LISTING")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=false reason=listing_sold price_gold=0 tax_gold=0 net_gold=0 buyer_gold=0 seller_gold=0 state= duplicate=false)" = "ok" ] \
   && [ "$(ledger "$LISTING" "$TXN")" = "$SAME_BEFORE" ]; then
  ok "同一挂单再买被拒：ok=false + listing_sold，未生效数值为 0，账户一格没动"
else
  bad "重复成交没被挡：$BODY"
fi
resp="$(buy_auction "$BUYER_TOKEN" "$BUYER_ID" 999999999)"
[ "$(expect_json "$(body_of "$resp")" ok=false reason=listing_not_found tax_gold=0)" = "ok" ] \
  && ok "不存在的挂单被拒 listing_not_found" || bad "不存在的挂单没被挡：$(body_of "$resp")"
resp="$(list_auction "$TOKEN" "$ID" 3 2 2000)"
L2="$(json_field "$(body_of "$resp")" listing_id)"
resp="$(buy_auction "$TOKEN" "$ID" "$L2")"
[ "$(expect_json "$(body_of "$resp")" ok=false reason=same_character tax_gold=0)" = "ok" ] \
  && ok "买卖同一人被拒 same_character" || bad "自己买自己没被挡：$(body_of "$resp")"
resp="$(list_auction "$TOKEN" "$ID" 2 1 500)"
L3="$(json_field "$(body_of "$resp")" listing_id)"
[ "$(expect_json "$(body_of "$resp")" ok=false reason=bound_item listing_id=0)" = "ok" ] \
  && ok "绑定物品不能上架（沿用 P1-1b3 的 CanTrade）" || bad "绑定物品没被挡：$(body_of "$resp")"
resp="$(list_auction "$TOKEN" "$ID" 9 1 500)"
[ "$(expect_json "$(body_of "$resp")" ok=false reason=slot_empty listing_id=0)" = "ok" ] \
  && ok "空格不能上架 slot_empty" || bad "空格没被挡：$(body_of "$resp")"
# 余额不足：标价高于买方现有金币，整笔回滚。
resp="$(list_auction "$TOKEN" "$ID" 4 3 99999999)"
L4="$(json_field "$(body_of "$resp")" listing_id)"
GOLD_BEFORE="$(field_of "$(ledger "$L4")" buyer_gold)"
resp="$(buy_auction "$BUYER_TOKEN" "$BUYER_ID" "$L4")"
BODY="$(body_of "$resp")"
if [ "$(expect_json "$BODY" ok=false reason=gold_insufficient tax_gold=0 net_gold=0)" = "ok" ] \
   && [ "$(field_of "$(ledger "$L4")" buyer_gold)" = "$GOLD_BEFORE" ] \
   && [ "$(field_of "$(ledger "$L4")" state)" = "listed" ]; then
  ok "买方金币不足整笔回滚：挂单仍 listed，金币、物品、流水都不动"
else
  bad "余额不足没整笔回滚：$BODY"
fi

echo "== 12. 改税率重启生效，伪造字段不影响结果 =="
if restart_character 8; then ok "把税率改成 8% 重启角色服，两条 gRPC 子通道已重连"; \
else bad "改税率重启后链路没通"; tail -12 "$TMP/global.log" | detail; fi
resp="$(list_auction "$TOKEN" "$ID" 5 2 1000)"
L5="$(json_field "$(body_of "$resp")" listing_id)"
FAKE=',"tax_gold":999999,"net_gold":999999,"price_gold":1,"tax_rate_pct":1,"seller_id":1,"state":"sold","txn_id":"forged","account":"forged","duplicate":true'
resp="$(buy_auction "$BUYER_TOKEN" "$BUYER_ID" "$L5" "$FAKE")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true price_gold=1000 tax_gold=80 net_gold=920 tax_rate_pct=8 duplicate=false state=sold)" = "ok" ]; then
  ok "伪造税率 / 税额 / 到账 / 事务号 / 账号全无效：按服务端 8% 算 80 税"
else
  bad "伪造字段影响了结果：$BODY"
fi
if restart_character 6; then ok "税率改回 6% 重启恢复现场"; else bad "税率没恢复"; fi

echo "== 13. 老规则不回退 =="
resp="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/inventory/can-trade?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"slot\":2}")"
[ "$(expect_json "$resp" tradable=false reason=bound_item)" = "ok" ] \
  && ok "P1-1b3：绑定装备仍不可交易" || bad "绑定判定回退：$resp"
resp="$(add_gold "$BUYER_TOKEN" "$BUYER_ID" -999999999)"
[ "$(code_of "$resp" | tr -d '\n')" != "200" ] \
  && ok "P1-1b4：金币扣成负数仍被拒" || bad "金币可以扣成负数：$(body_of "$resp")"

# B5 有库段：脚本自己的种子卖方 / 买方，先把卖方等级抬到 20（门槛 20 级），
# 再走一遍开摊 → 上架 → 看摊 → 成交 → 关摊。只在 MMO_B5_DB=1 时跑。
if [ "${MMO_B5_DB:-0}" = "1" ]; then
  echo "== 14. B5 摆摊连库段 =="
  B5_PRICE=1000
  B5_TAX=$((B5_PRICE * 5 / 100))
  b5() { "$DOTNET" "$CHARACTER_DLL" "$@" --config "$TMP/character.json" 2>&1; }
  b5 --raw-level-write "$ID" 20 >/dev/null 2>&1
  out="$(b5 --stall-open "$ID" kulan_dao cli)"
  STALL="$(field_of "$out" stall)"
  if printf '%s' "$out" | grep -q '^stall_open=ok' && [ "${STALL:-none}" != "none" ]; then
    ok "B5 开摊成功（20 级门槛、本服地表）stall=${STALL}"
  else
    bad "B5 开摊失败：$out"
  fi
  out="$(b5 --stall-list "$ID" "$STALL" 1 "$B5_PRICE")"
  B5_LISTING="$(field_of "$out" listing)"
  if printf '%s' "$out" | grep -q '^stall_list=ok' && [ "$(field_of "$out" live)" = "1" ]; then
    ok "B5 上架一件：落在 auction_listing（channel=stall），在售 1 / 8"
  else
    bad "B5 上架失败：$out"
  fi
  out="$(b5 --stall-view "$STALL")"
  printf '%s' "$out" | grep -q '^stall_item=slot' \
    && ok "B5 看摊读得到刚挂的那一件" || bad "B5 看摊没读到挂单：$out"
  out="$(b5 --stall-buy "$BUYER_ID" "$B5_LISTING")"
  if printf '%s' "$out" | grep -q '^stall_buy=ok' \
     && [ "$(field_of "$out" tax)" = "$B5_TAX" ] \
     && [ "$(field_of "$out" net)" = "$((B5_PRICE - B5_TAX))" ] \
     && [ "$(field_of "$out" tax_rate_pct)" = "5" ]; then
    ok "B5 成交走同一笔事务：5% 税 ${B5_TAX}、卖方净额 $((B5_PRICE - B5_TAX))"
  else
    bad "B5 成交不对：$out"
  fi
  out="$(b5 --stall-view "$STALL")"
  printf '%s' "$out" | grep -q 'live=0' \
    && ok "B5 成交后在售归零（世界服只靠这个数刷新显示）" || bad "B5 成交后在售没归零：$out"
  out="$(b5 --stall-close "$ID" "$STALL")"
  printf '%s' "$out" | grep -q '^stall_close=ok' \
    && ok "B5 关摊撤单成功" || bad "B5 关摊失败：$out"
fi

finish "$TITLE"
