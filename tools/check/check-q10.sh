#!/usr/bin/env bash
# Q10 核对：背包操作（移动 / 丢弃 / 排序 / 批量使用）。
#
# 完成标准（未覆盖排期 Q10 + 任务队列 #33）：
#   移动不丢、不串强化；丢弃二次确认；排序不改绑定与强化；RPC 与 UI 都有。
#
# 两种模式：
#   bash tools/check/check-q10.sh                        静态段（不连库，CI 跑这一段）
#   MMO_Q10_HAS_DB=1 bash tools/check/check-q10.sh       加真库段（要 gamedb）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
GLOBAL_DLL="$ROOT/src/Global/bin/Release/net10.0/Global.dll"
HAS_DB="${MMO_Q10_HAS_DB:-0}"

latest_version() {
  python3 - "$ROOT/db/migrations.json" "$1" <<'PY2'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
print([s for s in d["schemas"] if s["key"] == sys.argv[2]][0]["entries"][-1]["version"])
PY2
}

TITLE="Q10"

CHAR_PORT=${MMO_Q10_CHAR_PORT:-62140}
GATEWAY_PORT=${MMO_Q10_GATEWAY_PORT:-61141}
GLOBAL_PORT=${MMO_Q10_GLOBAL_PORT:-64041}
WORLD_PORT=${MMO_Q10_WORLD_PORT:-63108}
ACCOUNT="p4q10_bag"
TEST_PASSWORD="p4q10-Pw-9f21"
CHAR_NAME="p4q10_a"
CHARACTER_PID=""
GATEWAY_PID=""
GLOBAL_PID=""
TOKEN=""
ID=""
TMP="$(mktemp -d)"

cleanup() {
  stop_pid "$GATEWAY_PID"
  stop_pid "$GLOBAL_PID"
  stop_pid "$CHARACTER_PID"
  if [ -n "${HAVE_DB:-}" ]; then
    "$DOTNET" "$PROBE_DLL" --delete-character "$CHAR_NAME" --config "$TMP/character.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

echo "== 1. 编译与静态断言 =="
BUILD_OK=1
for proj in src/Character/Character.csproj src/Global/Global.csproj src/Gateway/Gateway.csproj \
            tools/probe/CliProbe/CliProbe.csproj; do
  name="$(basename "$proj" .csproj)"
  "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$name.log" 2>&1 || {
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E 'error ' "$TMP/build-$name.log" | head -4 | detail
  }
done
[ "$BUILD_OK" = "1" ] && ok "角色服、全局服务、网关与探针编译通过"

if ROOT="$ROOT" python3 "$ROOT/tools/check/check-q10-static.py" >"$TMP/static.out" 2>&1; then
  ok "四个 RPC 在、写入只改格号、排序键与二次确认都对、结晶外键跟着格号走"
  sed 's/^/       /' "$TMP/static.out"
else
  bad "静态断言失败"
  tail -8 "$TMP/static.out" | detail
fi

# 排序落位是这张卡最容易写错的一处（链条 / 纯环 / 满格），抽成纯函数后脱库跑一遍：
# 每一步都要落在当时空着的格上，走完每一行都要在它该在的格上。
if ( cd "$ROOT/tools/check/bag-plan-test" && "$DOTNET" run -c Release >"$TMP/plan.out" 2>&1 ); then
  if grep -q '^bag_sort_plan=ok' "$TMP/plan.out"; then
    ok "排序落位：直挪 / 链 / 纯环 / 满格 / 打乱 13 行都落在空格上、每行都在该在的格"
  else
    bad "排序落位核对不过"; tail -8 "$TMP/plan.out" | detail
  fi
else
  bad "排序落位核对跑不起来"; tail -8 "$TMP/plan.out" | detail
fi

if [ "$HAS_DB" != "1" ] || { [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; }; then
  printf '  ..   跳过真库段（MMO_Q10_HAS_DB 未开或缺 gamedb 凭据）\n'
  finish "$TITLE"
fi
echo "== 2. 迁移与临时配置 =="
HAVE_DB=1
"$DOTNET" "$PROBE_DLL" --migrate --config config/character.json >"$TMP/migrate.log" 2>&1
if grep -q '^migrate=ok' "$TMP/migrate.log"; then
  ok "迁移清单跑通（角色 → 社交 → 交易）"
else
  bad "迁移失败"; tail -8 "$TMP/migrate.log" | detail; finish "$TITLE"
fi

STATUS="$("$DOTNET" "$PROBE_DLL" --schema-status --config config/character.json 2>&1)"
# 版本号现取清单：v28 之后还会往后走，写死就会在后一张卡上红。
ROLE_V="$(latest_version character)"
if printf '%s' "$STATUS" | grep -q "schema_status=ok schema=角色 .*latest_version=${ROLE_V} "; then
  ok "版本表记到 角色 v${ROLE_V}，没有 drift（v28 结晶外键跟着格号走）"
else
  bad "版本表不是 角色 v${ROLE_V}"; printf '%s' "$STATUS" | grep schema_status | detail; finish "$TITLE"
fi

ROOT="$ROOT" TMP="$TMP" CHAR_PORT="$CHAR_PORT" GATEWAY_PORT="$GATEWAY_PORT" \
GLOBAL_PORT="$GLOBAL_PORT" WORLD_PORT="$WORLD_PORT" python3 - <<'PY'
import json, os
tmp = os.environ['TMP']
c = {
    'schema': 'character-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'db_pool_max': 6, 'bag_slots': 40,
    'dungeon_table': 'config/dungeons.json', 'exp_table': 'config/exp.json',
    'enhance_table': 'config/enhance.json', 'transfer_table': 'config/transfer.json',
    'auction_table': 'config/auction.json',
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
printf '%s\n' "$TEST_PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$ACCOUNT" \
  --seed-slots 8 --config config/gateway.json >>"$TMP/seed.log" 2>&1
ok "测试账号已播种"

echo "== 3. 起角色服、全局服务与网关 =="
"$DOTNET" "$PROBE_DLL" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & CHARACTER_PID=$!
"$DOTNET" "$GLOBAL_DLL" --config "$TMP/global.json" >"$TMP/global.log" 2>&1 & GLOBAL_PID=$!
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >"$TMP/gateway.log" 2>&1 & GATEWAY_PID=$!
if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
   && wait_ready "$TMP/global.log" '^ready name=Global' "全局服务" \
   && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关"; then
  ok "三个进程起来"
else
  tail -12 "$TMP/character.log" | detail; finish "$TITLE"
fi

post() {
  curl -s -m 10 -w '\n%{http_code}' -X POST "http://127.0.0.1:${GATEWAY_PORT}$1?token=${TOKEN}" \
    -H 'Content-Type: application/json' -d "$2"
}

get_bag() {
  curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/inventory?token=${TOKEN}&character_id=$1"
}

# 某一格现在是什么：`item:count:bound`，空格是 `-:0:false`。
slot_of() {
  get_bag "$1" | python3 -c 'import json,sys
slot=int(sys.argv[1])
try: data=json.load(sys.stdin)
except Exception: print("-:0:false"); raise SystemExit
for one in data.get("slots", []):
    if int(one.get("slot", 0)) == slot:
        print("%s:%s:%s" % (one.get("item_id","-"), one.get("count",0), one.get("bound",False)))
        break
else:
    print("-:0:false")' "$2"
}

expect_slot() {
  local want="$1" label="$2" got
  got="$(slot_of "$3" "$4")"
  if [ "$got" = "$want" ]; then ok "$label"; else bad "${label}：第 $4 格=[$got] 期望=[$want]"; fi
}

expect_field() {
  local want="$1" label="$2" got
  got="$(printf '%s' "$3" | python3 -c "import json,sys
key=sys.argv[1]
try: print(json.load(sys.stdin).get(key, ''))
except Exception: print('')" "$4")"
  if [ "$got" = "$want" ]; then ok "$label"; else bad "${label}：$4=[$got] 期望=[$want]"; fi
}

# 不看回包，按格从库里读回：背包行 id 与强化等级。
row_of() {
  "$DOTNET" "$PROBE_DLL" --enhance-row "$1" "$2" --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^enhance_row=ok .* inventory_id=\([0-9]*\) item=[^ ]* level=\([0-9]*\)/\1 \2/p'
}

# 按物品编号反查现在在第几格。排序会换格号，断言不钉死格号，一律现取（第 10 节读写纪律 2）。
slot_of_item() {
  get_bag "$1" | python3 -c 'import json,sys
item=sys.argv[1]
try: data=json.load(sys.stdin)
except Exception: print(0); raise SystemExit
for one in data.get("slots", []):
    if one.get("item_id") == item:
        print(int(one.get("slot", 0))); break
else:
    print(0)' "$2"
}

expect_level() {
  local want="$1" label="$2" got
  got="$(row_of "$3" "$4" | awk '{print $2}')"
  if [ "$got" = "$want" ]; then ok "$label"; else bad "${label}：level=[$got] 期望=[$want]"; fi
}

echo "== 4. 造料：两堆矿、两件装备（其中一件 +5）、两格消耗品 =="
TOKEN="$(login_token "$ACCOUNT")"
[ -n "$TOKEN" ] || { bad "登录没拿到令牌"; finish "$TITLE"; }
RESP="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/character?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"$CHAR_NAME\",\"race\":\"mai\",\"class_id\":\"warrior\"}")"
[ "$(code_of "$RESP")" = "200" ] || { bad "建角色失败"; body_of "$RESP" | detail; finish "$TITLE"; }
ID="$(json_field "$(body_of "$RESP")" character_id)"
ok "角色已建：id=$ID"

post /inventory/add "{\"character_id\":$ID,\"slot\":3,\"item_id\":\"ore_iron\",\"count\":100}" >/dev/null
post /inventory/add "{\"character_id\":$ID,\"slot\":5,\"item_id\":\"ore_iron\",\"count\":100}" >/dev/null
post /inventory/add "{\"character_id\":$ID,\"slot\":7,\"item_id\":\"sword_trial\",\"count\":1}" >/dev/null
post /inventory/add "{\"character_id\":$ID,\"slot\":9,\"item_id\":\"ring_trial\",\"count\":1}" >/dev/null
post /inventory/add "{\"character_id\":$ID,\"slot\":11,\"item_id\":\"exp_booster\",\"count\":3}" >/dev/null
post /inventory/add "{\"character_id\":$ID,\"slot\":13,\"item_id\":\"pill_rejuvenate\",\"count\":5}" >/dev/null
"$DOTNET" "$PROBE_DLL" --raw-enhance-write "$ID" 7 5 --config "$TMP/character.json" >/dev/null 2>&1
expect_level 5 "7 号格的剑先写到 +5（下面用它验「移动不串强化」）" "$ID" 7

echo "== 5. 移动：同物品合并 / 不同物品交换 / 空格 / 拒绝路径 =="
RESP="$(post /inventory/move "{\"character_id\":$ID,\"from_slot\":3,\"to_slot\":5}")"
expect_field true "两堆同样的矿合到一格" "$(body_of "$RESP")" merged
expect_slot "ore_iron:200:false" "目标格变成 200 个" "$ID" 5
expect_slot "-:0:false" "来源格空了" "$ID" 3

RESP="$(post /inventory/move "{\"character_id\":$ID,\"from_slot\":7,\"to_slot\":9}")"
expect_field true "两件不同装备互换" "$(body_of "$RESP")" swapped
expect_level 5 "换到 9 号格后强化还在 +5（跟着背包行，不跟格子）" "$ID" 9
expect_slot "ring_trial:1:false" "7 号格换成原来 9 号格那件" "$ID" 7
expect_level 0 "7 号格那件没沾上 +5" "$ID" 7

RESP="$(post /inventory/move "{\"character_id\":$ID,\"from_slot\":20,\"to_slot\":21}")"
expect_field slot_empty "空来源格被拒，回 slot_empty" "$(body_of "$RESP")" reason
RESP="$(post /inventory/move "{\"character_id\":$ID,\"from_slot\":5,\"to_slot\":5}")"
expect_field same_slot "同一格自己挪被拒，回 same_slot" "$(body_of "$RESP")" reason
RESP="$(post /inventory/move "{\"character_id\":$ID,\"from_slot\":5,\"to_slot\":41}")"
if [ "$(code_of "$RESP")" = "400" ]; then ok "越过容量的格号按参数错拒（400）"; else
  bad "越界格号没有按参数错拒：HTTP $(code_of "$RESP")"; fi

echo "== 6. 丢弃：不带二次确认拒、带确认才丢、同号重放不二次丢 =="
RESP="$(post /inventory/discard "{\"character_id\":$ID,\"slot\":5,\"count\":200,\"confirm\":false}")"
expect_field not_confirmed "confirm=false 被拒，回 not_confirmed" "$(body_of "$RESP")" reason
expect_slot "ore_iron:200:false" "被拒时这一格一个都没少" "$ID" 5

RESP="$(post /inventory/discard \
  "{\"character_id\":$ID,\"slot\":5,\"count\":50,\"confirm\":true,\"txn_id\":\"q10-discard-1\"}")"
expect_field true "confirm=true 才真丢" "$(body_of "$RESP")" ok
expect_slot "ore_iron:150:false" "丢 50 个后剩 150" "$ID" 5

RESP="$(post /inventory/discard \
  "{\"character_id\":$ID,\"slot\":5,\"count\":50,\"confirm\":true,\"txn_id\":\"q10-discard-1\"}")"
expect_field duplicate "同一个事务号重放回 duplicate" "$(body_of "$RESP")" duplicate
expect_slot "ore_iron:150:false" "重放没有再丢一次" "$ID" 5

RESP="$(post /inventory/discard \
  "{\"character_id\":$ID,\"slot\":5,\"count\":999,\"confirm\":true,\"txn_id\":\"q10-discard-2\"}")"
expect_field count_invalid "丢的比这一格还多被拒，回 count_invalid" "$(body_of "$RESP")" reason
expect_slot "ore_iron:150:false" "被拒时数量不变" "$ID" 5

echo "== 7. 排序：种类 → 品阶 → 编号，紧凑，绑定与强化不变 =="
RESP="$(post /inventory/sort "{\"character_id\":$ID}")"
expect_field true "排序成功" "$(body_of "$RESP")" ok
get_bag "$ID" | python3 -c 'import json,sys
data=json.load(sys.stdin)
slots=[int(one["slot"]) for one in data.get("slots", [])]
items=[one["item_id"] for one in data.get("slots", [])]
assert slots == sorted(slots), "格号没有按升序紧凑"
assert slots == list(range(1, len(slots)+1)), "中间有空格，没有紧凑到第 1 格起"
assert len(items) >= 2 and set(items[:2]) == {"sword_trial", "ring_trial"}, \
    "装备没排在前面：" + str(items)
print("sorted=ok slots=" + ",".join(str(s) for s in slots) + " items=" + ",".join(items))' \
  >"$TMP/sort.out" 2>&1 \
  && { ok "排完紧凑从 1 号格起、两件装备在前"; sed 's/^/       /' "$TMP/sort.out"; } \
  || { bad "排序结果不对"; tail -3 "$TMP/sort.out" | detail; }

SWORD_SLOT="$(slot_of_item "$ID" sword_trial)"
expect_level 5 "排序后 +5 还跟着那件剑（背包行没换）" "$ID" "$SWORD_SLOT"
ORE_SLOT="$(slot_of_item "$ID" ore_iron)"
expect_slot "ore_iron:150:false" "排序没动数量、也没把非绑改成绑定" "$ID" "$ORE_SLOT"

echo "== 8. 批量使用：一次一个能成，多个被「同时只生效一个」拒，无效果入口的一个不扣 =="
BOOSTER_SLOT="$(slot_of_item "$ID" exp_booster)"
RESP="$(post /inventory/use-batch \
  "{\"character_id\":$ID,\"slot\":$BOOSTER_SLOT,\"count\":1,\"txn_id\":\"q10-use-1\"}")"
expect_field true "限时经验道具用 1 个成功" "$(body_of "$RESP")" ok
expect_field 1 "回包里 used=1" "$(body_of "$RESP")" used
expect_slot "exp_booster:2:true" "用掉 1 个后剩 2 个" "$ID" "$BOOSTER_SLOT"

RESP="$(post /inventory/use-batch \
  "{\"character_id\":$ID,\"slot\":$BOOSTER_SLOT,\"count\":2,\"txn_id\":\"q10-use-2\"}")"
expect_field effect_single_instance "一次用 2 个被拒（同时只生效一个）" "$(body_of "$RESP")" reason
expect_slot "exp_booster:2:true" "被拒时一个都不扣" "$ID" "$BOOSTER_SLOT"

PILL_SLOT="$(slot_of_item "$ID" pill_rejuvenate)"
RESP="$(post /inventory/use-batch \
  "{\"character_id\":$ID,\"slot\":$PILL_SLOT,\"count\":1,\"txn_id\":\"q10-use-3\"}")"
expect_field no_effect "丹药没有效果入口，回 no_effect" "$(body_of "$RESP")" reason
expect_slot "pill_rejuvenate:5:false" "无效果入口时一个都不扣" "$ID" "$PILL_SLOT"

RESP="$(post /inventory/use-batch \
  "{\"character_id\":$ID,\"slot\":$SWORD_SLOT,\"count\":1,\"txn_id\":\"q10-use-4\"}")"
expect_field not_usable "装备不是消耗品，回 not_usable" "$(body_of "$RESP")" reason

finish "$TITLE"
