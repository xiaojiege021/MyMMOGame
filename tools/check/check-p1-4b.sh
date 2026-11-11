#!/usr/bin/env bash
# P1-4b 核对：保护券只挡掉级，不提高成功率；同一种绑定券，用的时候不看来源。
#
# 完成标准（开发架构第 14 节第 36 行 + 第 11 节 P1-4 + 第 13 节 P1-4b + 方案 9.2 / 12.1）：
#   带券把概率改 0 后失败等级不变且券被用掉；不带券同样概率失败掉 1 级；
#   带券成功时等级 +1、券不消耗；带券与不带券读同一份 config/enhance.json 概率；
#   券到账即绑、不能交易；商城与通行证发货路径不同也能一样用；
#   伪造 use_coupon 之外的字段无效；券与石头 / 金币 / 等级在同一笔角色服事务里；
#   P1-4a 老规则不回退。
#
# 两种模式：
#   bash tools/check/check-p1-4b.sh                          服务端段：编译、静态、连库连服（要有 gamedb）
#   MMO_P14B_MODE=client bash tools/check/check-p1-4b.sh    客户端段：静态 + Unity 编辑器自检（要有 Unity）
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
MODE="${MMO_P14B_MODE:-server}"

CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"

CHAR_PORT=${MMO_P14B_CHAR_PORT:-62114}
GATEWAY_PORT=${MMO_P14B_GATEWAY_PORT:-61115}
WORLD_PORT=${MMO_P14B_WORLD_PORT:-63099}
ACCOUNT="p14b_user"
OTHER="p14b_other"
TEST_PASSWORD="p14b-Pw-7c19"
CHAR_NAMES=("p14b_a" "p14b_other")
COUPON=',"use_coupon":true'

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

# $4 是追加的请求体片段：,"use_coupon":true 或伪造字段。
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

# 用另一条发货路径（发奖事务）发同一种券：服务端只按 item_id 认，不看来源。
grant_coupon() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/grant?token=${TOKEN}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${ID},\"txn_id\":\"p14b-coupon-grant\",\"entries\":[{\"item\":{\"slot\":6,\"item_id\":\"ticket_protect\",\"count\":2}}]}"
}

can_trade() {
  curl -s -m 10 -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/inventory/can-trade?token=${TOKEN}" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${ID},\"slot\":$1}"
}

# 不看回包，从库里独立读回：等级 金币 低级石 中级石 高级石 券
state_line() {
  local levels coupon
  levels="$("$DOTNET" "$CHARACTER_DLL" --enhance-level "$ID" 1 --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^enhance_state=ok .* level=\([0-9-]*\) gold=\([0-9-]*\) low=\([0-9-]*\) mid=\([0-9-]*\) high=\([0-9-]*\)/\1 \2 \3 \4 \5/p')"
  coupon="$("$DOTNET" "$CHARACTER_DLL" --coupon-count "$ID" --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^coupon_count=\([0-9]*\) .*/\1/p')"
  printf '%s %s' "$levels" "$coupon"
}

expect_state() {
  local want="$1 $2 $3 $4 $5 $6" label="$7" got
  got="$(state_line)"
  if [ "$got" = "$want" ]; then ok "$label"; else bad "$label：state=[$got] 期望=[$want]"; fi
}

TITLE="P1-4b（${MODE}）"

# ---------------------------------------------------------------- 客户端段
if [ "$MODE" = "client" ]; then
  echo "== C1. 券定义、契约与客户端静态断言 =="
  if ROOT="$ROOT" python3 - >"$TMP/client-static.out" 2>&1 <<'PY'
import json, os, re
root = os.environ['ROOT']
cfg = json.load(open(root + '/config/enhance.json', encoding='utf-8'))
items = {i['id']: i for i in json.load(open(root + '/config/items.json', encoding='utf-8'))['items']}
assert cfg['protect_item_id'] == 'ticket_protect'
assert items['ticket_protect']['kind'] == 'voucher'
assert items['ticket_protect']['bind'] == 'always'
assert sum('success_rate' in t for t in cfg['tiers']) == 3
assert not any('coupon_rate' in t or 'protect_rate' in t for t in cfg['tiers'])
proto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
req = re.search(r'message EnhanceRequest \{(.*?)\}', proto, re.S).group(1)
resp = re.search(r'message EnhanceResponse \{(.*?)\}', proto, re.S).group(1)
fields = set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*=', req))
assert fields == {'account', 'character_id', 'slot', 'stone_id', 'txn_id', 'use_coupon'}, fields
for name in ('success_rate', 'protected_level', 'coupon_used', 'coupon_left'):
    assert re.search(r'\b' + name + r'\s*=', resp), name
client = open(root + '/client/MMOGame/Assets/Net/CharacterEnhance.cs', encoding='utf-8').read()
assert 'RequestBody(ulong characterId, uint slot, string stoneId, bool useCoupon)' in client
# 源码里是 C# 转义写法 \"use_coupon\"，C# 运行时拼出的请求体由 Unity 自检覆盖。
assert 'use_coupon' in client and 'protected_level' in client
assert 'coupon_used' in client and 'coupon_left' in client
assert 'ticket_protect' not in client and 'success_rate' not in client
assert 'EnhanceTable' not in client and 'Random' not in client and 'JsonUtility' not in client
repo = open(root + '/src/Character/EnhanceRepository.cs', encoding='utf-8').read()
assert repo.count('Random.Shared.NextDouble() < tier.SuccessRate') == 1
assert 'var protectedLevel = !success && useCoupon' in repo
assert 'coupon_insufficient' in repo and 'Math.Max(0, levelBefore - 1)' in repo
print('client_static=ok coupon=1 rate_source=1')
PY
  then ok "只有一种绑定券、请求只多一个用券开关、掷点仍只用同一档概率"
  else bad "客户端静态断言失败"; cat "$TMP/client-static.out" | detail
  fi

  echo "== C2. Unity 编辑器自检：带券 / 不带券的请求与回包拆装 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    finish "$TITLE"
  fi
  if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod P14bCheck.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity P14bCheck.Run 退出码 0"
  else
    bad "Unity P14bCheck.Run 失败"
    tail -25 "$TMP/unity.log" | detail
  fi
  for key in config proto wire boundary; do
    if grep -q "p14b_${key}=ok" "$TMP/unity.log"; then
      ok "编辑器自检 p14b_${key}=ok"
    else
      bad "编辑器自检 p14b_${key} 没过"
      grep "p14b_${key}" "$TMP/unity.log" | tail -2 | detail
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
cfg = json.load(open(root + '/config/enhance.json', encoding='utf-8'))
items = {i['id']: i for i in json.load(open(root + '/config/items.json', encoding='utf-8'))['items']}
assert cfg['protect_item_id'] == 'ticket_protect'
coupon = items['ticket_protect']
assert coupon['kind'] == 'voucher' and coupon['bind'] == 'always'
assert coupon['id'] not in {t['stone_id'] for t in cfg['tiers']}
assert sum('success_rate' in t for t in cfg['tiers']) == 3
assert not any('coupon_rate' in t or 'protect_rate' in t for t in cfg['tiers'])
proto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read()
req = re.search(r'message EnhanceRequest \{(.*?)\}', proto, re.S).group(1)
resp = re.search(r'message EnhanceResponse \{(.*?)\}', proto, re.S).group(1)
fields = set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*=', req))
assert fields == {'account', 'character_id', 'slot', 'stone_id', 'txn_id', 'use_coupon'}, fields
for name in ('ok', 'reason', 'level', 'success_rate', 'protected_level', 'coupon_used', 'coupon_left'):
    assert re.search(r'\b' + name + r'\s*=', resp), name
table = open(root + '/src/Character/EnhanceTable.cs', encoding='utf-8').read()
assert 'protectItem.Bind != BindRule.Always' in table
assert 'enhance.json 缺 protect_item_id' in table
assert '必须是 voucher' in table and '不能和强化石是同一种' in table
repo = open(root + '/src/Character/EnhanceRepository.cs', encoding='utf-8').read()
assert repo.count('Random.Shared.NextDouble() < tier.SuccessRate') == 1, '掷点只有一行'
assert 'var protectedLevel = !success && useCoupon' in repo
assert 'table.ProtectItemId' in repo and 'coupon_insufficient' in repo
assert 'BeginTransactionAsync' in repo and 'Math.Max(0, levelBefore - 1)' in repo
assert 'protect_rate' not in repo and 'coupon_rate' not in repo
svc = open(root + '/src/Character/CharacterGrpcService.cs', encoding='utf-8').read()
assert 'request.UseCoupon' in svc and 'SuccessRate = outcome.Tier.SuccessRate' in svc
api = open(root + '/src/Gateway/CharacterApi.cs', encoding='utf-8').read()
assert 'bool? UseCoupon' in api and 'UseCoupon = body.UseCoupon ?? false' in api
assert 'MapPost("/character/enhance"' in api and 'OwnsAsync(client, account' in api
assert 'Guid.NewGuid()' in api and 'Npgsql' not in api
program = open(root + '/src/Character/Program.cs', encoding='utf-8').read()
process = open(root + '/src/Character/CharacterProcess.cs', encoding='utf-8').read()
assert 'case "--coupon-count"' in program and 'CouponCountAsync' in program
# 启动日志与只读入口必须同形，否则 wait_line 等不到。
assert program.count('coupon_bind=always') == 1
assert process.count('coupon_bind=always') == 1
for path in ('src/Character/EnhanceRepository.cs', 'src/Character/CharacterGrpcService.cs',
             'src/Gateway/CharacterApi.cs'):
    body = open(root + '/' + path, encoding='utf-8').read()
    # success_rate = result.SuccessRate 只是回包字段映射，不算第二套数字；
    # 要挡的是把概率写成常量的那种赋值。
    assert '0.8' not in body and not re.search(r'success_rate\s*=\s*[0-9]', body), path
print('static_code=ok coupon=1 rate_source=1 gate_server_side=1')
PY
then ok "券定义、契约字段、同一处掷点、进程边界与只读入口都落在指定文件里"
else bad "服务端静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 3. 券的配置校验：缺字段 / 物品不存在 / 不是券都起不来 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
base = json.load(open(root + '/config/enhance.json', encoding='utf-8'))
def dump(name, obj):
    json.dump(obj, open(tmp + '/' + name, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
w = copy.deepcopy(base); w.pop('protect_item_id'); dump('bad-no-coupon.json', w)
w = copy.deepcopy(base); w['protect_item_id'] = 'coupon_not_exist'; dump('bad-coupon-missing.json', w)
w = copy.deepcopy(base); w['protect_item_id'] = 'stone_enhance_low'; dump('bad-coupon-kind.json', w)
w = copy.deepcopy(base); w['protect_item_id'] = 'ore_iron'; dump('bad-coupon-material.json', w)
PY
config_reject() {
  local label="$1" file="$2" expect="$3" out
  out="$("$DOTNET" "$CHARACTER_DLL" --enhance-state --enhance-config "$TMP/$file" --config config/character.json 2>&1)"
  if printf '%s' "$out" | grep -qF "$expect"; then ok "$label"; else bad "$label：${out:0:160}"; fi
}
config_reject "缺 protect_item_id 起不来" bad-no-coupon.json "config_error=enhance.json 缺 protect_item_id"
config_reject "券编号不在物品表里起不来" bad-coupon-missing.json "config_error=enhance.json 的 protect_item_id 不在 items.json 里"
config_reject "券编号指向强化石起不来" bad-coupon-kind.json "config_error=enhance.json 的 protect_item_id 必须是 voucher"
config_reject "券编号指向普通材料起不来" bad-coupon-material.json "config_error=enhance.json 的 protect_item_id 必须是 voucher"
if "$DOTNET" "$CHARACTER_DLL" --enhance-state --config config/character.json 2>&1 \
    | grep -qF 'coupon=ticket_protect coupon_bind=always'; then
  ok "只读入口与启动共用同一套校验：好配置读出唯一一种绑定券"
else
  bad "好配置的只读入口没输出券信息"
fi

if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 gamedb 凭据，连服段跑不了（这一段要在笔记本上跑）"
  finish "$TITLE"
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
[ "$MIGRATE_OK" = "1" ] && ok "角色 schema 就位（enhance 排在 inventory 之后）"
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
   && wait_line "$CHARACTER_LOG" 'rates=1,1,1' \
   && wait_line "$CHARACTER_LOG" 'coupon=ticket_protect coupon_bind=always'; then
  ok "角色服与网关起来，强化表读成全成功、券定义与只读入口同形"
else
  bad "进程没起来或启动日志缺券信息"; tail -12 "$CHARACTER_LOG" | detail; finish "$TITLE"
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then ok "两个账号都拿到令牌"; else bad "登录失败"; finish "$TITLE"; fi
if create_char "$TOKEN" "p14b_a" && [ -n "$NEW_ID" ]; then ID="$NEW_ID"; ok "建好主角色 id=$ID"; else bad "建主角色失败"; finish "$TITLE"; fi
if create_char "$OTHER_TOKEN" "p14b_other" && [ -n "$NEW_ID" ]; then OTHER_ID="$NEW_ID"; ok "建好别人的角色 id=$OTHER_ID"; else bad "建别人的角色失败"; finish "$TITLE"; fi

echo "== 6. 鉴权、归属与用券开关的形状 =="
resp="$(enhance "" "$ID" stone_enhance_low)"
[ "$(code_of "$resp")" = "401" ] && ok "没有令牌 401" || bad "没有令牌没被挡：$(code_of "$resp")"
resp="$(enhance forged "$ID" stone_enhance_low "$COUPON")"
[ "$(code_of "$resp")" = "401" ] && ok "带券伪造令牌 401" || bad "带券伪造令牌没被挡：$(code_of "$resp")"
resp="$(enhance "$TOKEN" "$OTHER_ID" stone_enhance_low "$COUPON")"
[ "$(code_of "$resp")" = "404" ] && ok "别人的角色 404（网关复核归属）" || bad "别人的角色没被挡：$(code_of "$resp")"
resp="$(enhance "$TOKEN" "$ID" stone_enhance_low ',"use_coupon":"true"')"
[ "$(code_of "$resp")" = "400" ] && ok "use_coupon 传字符串被挡：400 + bad_request" || bad "用券开关形状没校验：$(body_of "$resp")"

echo "== 7. 发放装备、石头、金币与保护券（券到账即绑） =="
SEED_OK=1
for spec in "1 sword_trial 1" "2 stone_enhance_low 60" "3 stone_enhance_mid 60" "4 stone_enhance_high 60" "5 ticket_protect 2"; do
  set -- $spec
  r="$(add_item "$TOKEN" "$ID" "$1" "$2" "$3")"
  [ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "发 $2 失败：$(body_of "$r")"; }
done
r="$(add_gold 100000)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "发金币失败：$(body_of "$r")"; }
r="$(add_item "$OTHER_TOKEN" "$OTHER_ID" 1 sword_trial 1)"
[ "$(code_of "$r")" = "200" ] || { SEED_OK=0; bad "给别人的角色发装备失败"; }
[ "$SEED_OK" = "1" ] && ok "装备、三档石头、2 张券与 100000 金币到位"
[ "$SEED_OK" = "1" ] || finish "$TITLE"
expect_state 0 100000 60 60 60 2 "库里初始状态：+0，券 2 张"
r="$(can_trade 5)"
[ "$(json_field "$r" tradable)" = "false" ] && [ "$(json_field "$r" reason)" = "bound_item" ] \
  && ok "券不可交易：到账即绑，can-trade 回 bound_item" || bad "券的交易判定不对：$r"

echo "== 8. 带券成功：等级 +1、券不消耗、成功率仍读同一份配置 =="
resp="$(enhance "$TOKEN" "$ID" stone_enhance_low "$COUPON")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true success=true level_before=0 level=1 max_level=12 tier=low success_rate=1 protected_level=false coupon_used=0 coupon_left=2)" = "ok" ]; then
  ok "概率 1 时带券成功 +1，券没被扣、成功率就是这一档的配置值"
else
  bad "带券成功没按预期：$(code_of "$resp") $BODY"
fi
expect_state 1 99900 59 60 60 2 "库里 +1、扣 1 石 100 金币、券仍是 2 张"

echo "== 9. 概率改 0 重启：不带券掉 1 级，带券等级不变且用掉 1 张 =="
# 只把等级构造回 +4，好让后续每一手的目标都落在 low 档（1-6），材料与金币好算。
"$DOTNET" "$CHARACTER_DLL" --raw-enhance-write "$ID" 1 4 --config "$TMP/character.json" >/dev/null 2>&1
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
  wait_ready "$CHARACTER_LOG" '^ready name=Character' "角色服（重启）" || return 1
  # ready 只说明进程在听，网关手里的旧 gRPC 子通道还没换掉；紧随其后的请求
  # 会撞 "Error connecting to subchannel"。用一个只读请求等链路真的通再跑用例。
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
if restart_character "0,0,0" "$TMP/character2.log" && wait_line "$CHARACTER_LOG" 'rates=0,0,0'; then
  ok "改概率后重启角色服即生效：三档全 0（只改配置，没改代码）"
else
  bad "重启后没读到新概率"; grep 'enhance_table_version' "$CHARACTER_LOG" | detail; finish "$TITLE"
fi

resp="$(enhance "$TOKEN" "$ID" stone_enhance_low)"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true success=false level_before=4 level=3 success_rate=0 protected_level=false coupon_used=0 coupon_left=2)" = "ok" ]; then
  ok "不带券同样概率失败：+4 → +3，券 2 张没动"
else
  bad "不带券失败没按预期：$BODY"
fi
expect_state 3 99800 58 60 60 2 "库里 +3、扣 1 石 100 金币、券仍是 2 张"

resp="$(enhance "$TOKEN" "$ID" stone_enhance_low "$COUPON")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true success=false level_before=3 level=3 success_rate=0 protected_level=true coupon_used=1 coupon_left=1)" = "ok" ]; then
  ok "带券失败：等级不变、券用掉 1 张，回包成功率与不带券同为 0"
else
  bad "带券失败没按预期：$BODY"
fi
expect_state 3 99700 57 60 60 1 "库里等级仍 +3，石头与金币照扣，券剩 1 张"

resp="$(enhance "$TOKEN" "$ID" stone_enhance_low "$COUPON")"
BODY="$(body_of "$resp")"
[ "$(code_of "$resp")" = "200" ] \
  && [ "$(expect_json "$BODY" ok=true success=false level=3 level_before=3 protected_level=true coupon_used=1 coupon_left=0 success_rate=0)" = "ok" ] \
  && ok "再带券失败：等级还是不变，券扣到 0" || bad "第二次带券失败不对：$BODY"
expect_state 3 99600 56 60 60 0 "库里 +3、券 0 张"

resp="$(enhance "$TOKEN" "$ID" stone_enhance_low "$COUPON")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=false reason=coupon_insufficient level=0 level_before=0 success=false coupon_used=0 protected_level=false)" = "ok" ]; then
  ok "券不够：200 + ok=false + coupon_insufficient，未生效数值字段都是 0"
else
  bad "券不够没被挡：$BODY"
fi
expect_state 3 99600 56 60 60 0 "券不足整笔回滚：等级、金币、石头一格没动"

echo "== 10. 概率改回 1、换一条发货路径：同一种券照样能用 =="
if restart_character "1,1,1" "$TMP/character3.log" && wait_line "$CHARACTER_LOG" 'rates=1,1,1'; then
  ok "概率改回全 1 并重启即时生效"
else
  bad "第三次重启没读到新概率"; finish "$TITLE"
fi
resp="$(enhance "$TOKEN" "$ID" stone_enhance_low)"
BODY="$(body_of "$resp")"
[ "$(code_of "$resp")" = "200" ] \
  && [ "$(expect_json "$BODY" ok=true success=true level_before=3 level=4 success_rate=1 coupon_used=0 coupon_left=0)" = "ok" ] \
  && ok "不带券成功 +3 → +4，回包成功率与同一份配置一致" || bad "改回 1 后强化没生效：$BODY"
expect_state 4 99500 55 60 60 0 "库里 +4、券 0 张"

r="$(grant_coupon)"
if [ "$(code_of "$r")" = "200" ]; then ok "用发奖事务（商城 / 通行证走的路径）又发同一种券"; else bad "发奖发券失败：$(body_of "$r")"; fi
expect_state 4 99500 55 60 60 2 "库里券 2 张，物品编号仍是 ticket_protect"
r="$(can_trade 6)"
[ "$(json_field "$r" tradable)" = "false" ] && [ "$(json_field "$r" reason)" = "bound_item" ] \
  && ok "发奖发的券同样到账即绑、不可交易" || bad "发奖发的券绑定判定不对：$r"

if restart_character "0,0,0" "$TMP/character4.log" && wait_line "$CHARACTER_LOG" 'rates=0,0,0'; then
  ok "再次把概率改 0 重启，带券与不带券仍共用这一份配置"
else
  bad "第四次重启没读到新概率"; finish "$TITLE"
fi
resp="$(enhance "$TOKEN" "$ID" stone_enhance_low "$COUPON")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true success=false level_before=4 level=4 protected_level=true coupon_used=1 coupon_left=1 success_rate=0)" = "ok" ]; then
  ok "另一条发货路径来的券一样挡住掉级：用的时候只看物品编号，不看来源"
else
  bad "发奖发的券没能挡掉级：$BODY"
fi
expect_state 4 99400 54 60 60 1 "库里 +4、券 1 张"

echo "== 11. 金币不足时券跟着回滚、伪造字段无效 =="
GOLD_BEFORE="$(state_line | awk '{print $2}')"
add_gold "-$((GOLD_BEFORE - 50))" >/dev/null 2>&1
resp="$(enhance "$TOKEN" "$ID" stone_enhance_low "$COUPON")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=false reason=gold_insufficient level=0 coupon_used=0)" = "ok" ]; then
  ok "带券但金币不够：整笔回滚，券也没被扣"
else
  bad "金币不足没被挡：$BODY"
fi
expect_state 4 50 54 60 60 1 "钱不够时券、石头与等级都没变（同一笔事务）"
add_gold "$((GOLD_BEFORE - 50))" >/dev/null 2>&1

FAKE=',"coupon_id":"ticket_protect","coupon_count":99,"coupon_source":"mall","protected_level":true,"coupon_used":1,"coupon_left":99,"success_rate":1,"level":12,"txn_id":"fake","account":"p14b_other","max_level":99'
resp="$(enhance "$TOKEN" "$ID" stone_enhance_low "$FAKE")"
BODY="$(body_of "$resp")"
if [ "$(code_of "$resp")" = "200" ] \
   && [ "$(expect_json "$BODY" ok=true success=false level_before=4 level=3 protected_level=false coupon_used=0 coupon_left=1 success_rate=0 max_level=12)" = "ok" ]; then
  ok "伪造券编号 / 数量 / 来源 / 结果 / 成功率 / 等级 / 事务号 / 账号全无效"
else
  bad "伪造字段影响了结果：$BODY"
fi
expect_state 3 99300 53 60 60 1 "库里按服务端配置只掉 1 级，券仍是 1 张"

LAST_TXN="$(grep -o 'txn=enhance:[0-9a-f]*' "$TMP/gateway.log" | tail -1 | cut -d= -f2)"
TXN_ROWS="$("$DOTNET" "$CHARACTER_DLL" --enhance-txn-count "$LAST_TXN" --config "$TMP/character.json" 2>/dev/null | sed -n 's/^enhance_txn_count=\([0-9]*\) .*/\1/p')"
[ "$TXN_ROWS" = "1" ] && ok "这一笔的服务端事务号在库里恰好一行（P1-4a 幂等不回退）" \
  || bad "事务号行数不对：txn=$LAST_TXN rows=$TXN_ROWS"

echo "== 12. P1-4a 老规则不回退 =="
resp="$(enhance "$TOKEN" "$ID" stone_enhance_mid)"
BODY="$(body_of "$resp")"
[ "$(code_of "$resp")" = "200" ] && [ "$(expect_json "$BODY" ok=false reason=wrong_stone level=0)" = "ok" ] \
  && ok "低级档目标用中级石：wrong_stone，三档分界没变" || bad "三档分界回退了：$BODY"
expect_state 3 99300 53 60 60 1 "被拒后库里状态一格没动"
"$DOTNET" "$CHARACTER_DLL" --raw-enhance-write "$ID" 1 12 --config "$TMP/character.json" >/dev/null 2>&1
resp="$(enhance "$TOKEN" "$ID" stone_enhance_high "$COUPON")"
BODY="$(body_of "$resp")"
[ "$(code_of "$resp")" = "200" ] && [ "$(expect_json "$BODY" ok=false reason=at_cap level=0 coupon_used=0)" = "ok" ] \
  && ok "+12 仍然封顶，带券也点不动，券没被扣" || bad "+12 没封顶：$BODY"
r="$(can_trade 1)"
[ "$(json_field "$r" tradable)" = "false" ] && [ "$(json_field "$r" reason)" = "bound_item" ] \
  && ok "P1-1b3 不回退：拾取即绑的装备仍不可交易" || bad "绑定判定回退了：$r"
r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"dungeon_id\":\"main\",\"party_size\":1}")"
[ "$(json_field "$r" allowed)" = "true" ] && ok "P1-2a 不回退：主线单人仍能进本" || bad "进本回退了：$r"
r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/exp/settle?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"txn_id\":\"p14b-exp-1\",\"source\":\"task\",\"party_size\":1}")"
[ "$(json_field "$r" level_cap)" = "100" ] && ok "P1-2d 不回退：经验结算仍按 level_cap=100" || bad "经验结算回退了：$r"
for _ in 1 2; do
  r="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/grant?token=${TOKEN}" \
    -H 'Content-Type: application/json' -d "{\"character_id\":${ID},\"txn_id\":\"p14b-grant-repeat\",\"entries\":[{\"currency\":{\"kind\":\"gold\",\"delta\":1}}]}")"
done
[ "$(json_field "$r" duplicate)" = "true" ] && ok "P1-1b5 不回退：同一发奖事务号仍只发一次" || bad "发奖幂等回退了：$r"
r="$(add_gold -999999999)"
[ "$(code_of "$r")" != "200" ] && ok "P1-1b4 不回退：金币减成负数被拒" || bad "余额能写成负数：$(body_of "$r")"

finish "$TITLE"
