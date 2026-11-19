#!/usr/bin/env bash
# B1 核对：丹药 / 炼丹。
#
# 完成标准（开发架构 14.5 第 7 行 / 任务队列 #36）：
#   六种丹药可用；回血 / 回蓝不超同级符*；突破丹在突破事务里扣；炼丹耗药材；
#   商城不卖、拍卖可交易。
#   * 卡片原文写「不超同级符」，方案 12.1 与 talisman.json 写「符不超过同级丹」，
#     按方案正文执行（丹是上限，符 ≤ 丹），核对钉的是这一条，见开发日志的口径说明。
#
# 两种模式：
#   bash tools/check/check-b1.sh                      无库段（CI 跑这一段）
#   MMO_B1_HAS_DB=1 bash tools/check/check-b1.sh     加真库段（要 gamedb）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GATEWAY="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
HAS_DB="${MMO_B1_HAS_DB:-0}"
TITLE="B1"

CHAR_PORT=${MMO_B1_CHAR_PORT:-62161}
GATEWAY_PORT=${MMO_B1_GATEWAY_PORT:-61162}
ACCOUNT="p4b1_pill"
TEST_PASSWORD="p4b1-Pw-91c7"
CHAR_NAME=""
CHARACTER_PID=""
GATEWAY_PID=""
TOKEN=""
TMP="$(mktemp -d)"

cleanup() {
  stop_pid "$GATEWAY_PID"
  stop_pid "$CHARACTER_PID"
  if [ -n "${HAVE_DB:-}" ] && [ -n "$CHAR_NAME" ]; then
    "$DOTNET" "$PROBE" --delete-character "$CHAR_NAME" --config "$TMP/character.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY" --delete-account "$ACCOUNT" \
      --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Character/Character.csproj src/Battle/Battle.csproj \
            src/Gateway/Gateway.csproj tools/probe/CliProbe/CliProbe.csproj; do
  name="$(basename "$proj" .csproj)"
  "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$name.log" 2>&1 || {
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E 'error ' "$TMP/build-$name.log" | head -4 | detail
  }
done
[ "$BUILD_OK" = "1" ] && ok "角色服、战斗服、网关与探针编译通过"

echo "== 2. 静态断言 =="
if python3 "$ROOT/tools/check/check-b1-static.py" >"$TMP/static.out" 2>&1; then
  ok "协议 / 配置 / 扣丹 / 冷却 / 库 / 挂载六组静态断言全过"
  sed 's/^/       /' "$TMP/static.out" | tail -2
else
  bad "静态断言失败"
  grep '^FAIL' "$TMP/static.out" | head -8 | detail
fi

echo "== 3. 只读入口：六种丹与上限都从配置现取 =="
LIST="$("$DOTNET" "$PROBE" --pill-list 2>&1)"
printf '%s' "$LIST" >"$TMP/pill-list.txt"
HEAD="$(head -1 "$TMP/pill-list.txt")"
case "$HEAD" in
  pill_list_state=ok\ version=*\ pills=6\ hp_top=*\ mana_top=*\ talisman_hp_cap=*\ talisman_mana_cap=*)
    ok "丹药表读得出来：六种丹 + 两个上限（${HEAD}）" ;;
  *) bad "丹药清单头部不对：$HEAD" ;;
esac

# 六种丹的分档 / 池 / 冷却组与配置一一对上（期望值现取，不写死数字）
if ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os, sys
root, tmp = os.environ["ROOT"], os.environ["TMP"]
doc = json.load(open(os.path.join(root, "config/pill.json"), encoding="utf-8"))
rows = {}
for line in open(os.path.join(tmp, "pill-list.txt"), encoding="utf-8"):
    if line.startswith("pill="):
        parts = dict(x.split("=", 1) for x in line.strip().split(" "))
        # 行首是 pill=<编号>，字典里就是 item_id，直接拿来当键。
        parts["item_id"] = parts.pop("pill")
        rows[parts["item_id"]] = parts
if len(rows) != len(doc["pills"]):
    print("条数不一致 " + str(len(rows)) + " vs " + str(len(doc["pills"])))
    sys.exit(1)
for pill in doc["pills"]:
    row = rows.get(pill["item_id"])
    if row is None:
        print("清单里没有 " + pill["item_id"])
        sys.exit(1)
    if row["kind"] != pill["kind"]:
        print(pill["item_id"] + " kind 不一致")
        sys.exit(1)
    want_group = pill.get("cooldown_group") or "-"
    if row["cooldown_group"] != want_group:
        print(pill["item_id"] + " 冷却组不是 " + want_group)
        sys.exit(1)
    for key, field in (("hp_restore", "restore"), ("mana_restore", "restore")):
        want = pill.get("restore", 0) if (
            (key == "hp_restore" and pill["kind"] == "hp")
            or (key == "mana_restore" and pill["kind"] == "mana")) else 0
        if row[key] != str(want):
            print(pill["item_id"] + " " + key + " 不是配置值 " + str(want))
            sys.exit(1)
sys.exit(0)
PY
then
  ok "六种丹的池 / 回复值 / 冷却组与 pill.json 逐条一致（现取）"
else
  bad "丹药清单与配置对不上"
fi

# 符的上限就是同级最强丹（两份文件同一份数）
CAPS="$(python3 - <<'PY'
import json, re
head = open("config/pill.json", encoding="utf-8").read()
doc = json.loads(head)
hp = max(p.get("restore", 0) for p in doc["pills"] if p["kind"] == "hp")
mana = max(p.get("restore", 0) for p in doc["pills"] if p["kind"] == "mana")
tal = json.load(open("config/talisman.json", encoding="utf-8"))
print(str(tal["red"]["potion_hp_restore"] == hp and tal["blue"]["potion_mana_restore"] == mana).lower(),
      tal["red"]["hp_restore"] <= hp and tal["blue"]["mana_restore"] <= mana)
PY
)"
case "$CAPS" in
  "true True") ok "符上限 = 同级最强丹，且符自身回复不超过它（方案 12.1）" ;;
  *) bad "符与丹的上限对不上：$CAPS" ;;
esac

# 每段突破都配了丹，且都在这六种里
if ROOT="$ROOT" python3 - <<'PY'
import json, os, sys
root = os.environ["ROOT"]
pills = {p["item_id"] for p in json.load(open(os.path.join(root, "config/pill.json"), encoding="utf-8"))["pills"]}
realms = json.load(open(os.path.join(root, "config/realms.json"), encoding="utf-8"))
bad = [a.get("from") for a in realms["advance"]
       if a.get("pill") not in pills or int(a.get("pill_count", 0)) < 1]
if bad:
    print(",".join(bad))
    sys.exit(1)
sys.exit(0)
PY
then
  ok "十段突破各配一枚境界丹，编号都在六种丹里"
else
  bad "有段落没配境界丹或配错了编号"
fi

echo "== 4. 炼丹：六条配方产出六种丹、材料只有药材 =="
if ROOT="$ROOT" python3 - <<'PY'
import json, os, sys
root = os.environ["ROOT"]
items = {x["id"]: x for x in json.load(open(os.path.join(root, "config/items.json"), encoding="utf-8"))["items"]}
pills = {p["item_id"] for p in json.load(open(os.path.join(root, "config/pill.json"), encoding="utf-8"))["pills"]}
recipes = json.load(open(os.path.join(root, "config/recipes.json"), encoding="utf-8"))
alch = [r for r in recipes["recipes"] if r["profession"] == "alchemy"]
if sorted(r["output"]["item_id"] for r in alch) != sorted(pills):
    print("炼丹产出不是这六种")
    sys.exit(1)
for r in alch:
    for m in r["inputs"]:
        if items.get(m["item_id"], {}).get("kind") != "material":
            print(r["id"] + " 用了非药材 " + m["item_id"])
            sys.exit(1)
    if int(r.get("craft_time_s", 0)) < 1 or not 0 <= int(r.get("success_pct", 0)) <= 100:
        print(r["id"] + " 成功率 / 耗时越界")
        sys.exit(1)
sys.exit(0)
PY
then
  ok "炼丹六条：产出覆盖六种丹，材料全是药材，成功率与耗时在范围内"
else
  bad "炼丹配方对不上六种丹"
fi

echo "== 5. 坏配置起不来 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
base = json.load(open(os.path.join(root, "config/pill.json"), encoding="utf-8"))

def save(name, mutate):
    doc = copy.deepcopy(base)
    mutate(doc)
    json.dump(doc, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"),
              ensure_ascii=False)

def find(doc, item):
    return next(p for p in doc["pills"] if p["item_id"] == item)

save("zero_restore", lambda d: find(d, "pill_rejuvenate").__setitem__("restore", 0))
save("bad_kind", lambda d: find(d, "pill_rejuvenate").__setitem__("kind", "heal"))
save("mana_in_heal_group", lambda d: find(d, "pill_huiling").__setitem__("cooldown_group", "heal"))
save("breakthrough_with_restore", lambda d: find(d, "pill_zhuji").__setitem__("restore", 50))
save("unknown_item", lambda d: find(d, "pill_qixue").__setitem__("item_id", "pill_not_exists"))
save("dup_id", lambda d: d["pills"].append(copy.deepcopy(find(d, "pill_focus"))))
save("no_hp", lambda d: d.__setitem__("pills", [p for p in d["pills"] if p["kind"] != "hp"]))
save("schema", lambda d: d.__setitem__("schema", "pill-table/9"))
PY
for c in zero_restore bad_kind mana_in_heal_group breakthrough_with_restore \
         unknown_item dup_id no_hp schema; do
  out="$("$DOTNET" "$PROBE" --pill-list --pill-table-path "$TMP/bad-$c.json" 2>&1)"
  rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q 'config_error='; then
    ok "坏配置 ${c}：起不来（config_error=）"
  else
    bad "坏配置 ${c} 没挡住（rc=${rc}）：$(printf '%s' "$out" | head -1)"
  fi
done

# 符上限与丹不一致也要起不来（两份文件互校）
python3 - "$TMP" <<'PY'
import json, sys
tmp = sys.argv[1]
tal = json.load(open("config/talisman.json", encoding="utf-8"))
tal["red"]["potion_hp_restore"] = tal["red"]["potion_hp_restore"] + 10
ch = json.load(open("config/character.json", encoding="utf-8"))
ch["talisman_table"] = tmp + "/bad-talisman.json"
json.dump(tal, open(tmp + "/bad-talisman.json", "w", encoding="utf-8"), ensure_ascii=False)
json.dump(ch, open(tmp + "/character-badtal.json", "w", encoding="utf-8"), ensure_ascii=False)
PY
out="$("$DOTNET" "$PROBE" --pill-list --config "$TMP/character-badtal.json" 2>&1)"
rc=$?
if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q 'config_error='; then
  ok "符上限与同级最强丹不一致：起不来（两份文件只有一个数）"
else
  bad "符上限与丹不一致时没挡住（rc=${rc}）：$(printf '%s' "$out" | head -1)"
fi

if [ "$HAS_DB" != "1" ] || { [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; }; then
  printf '  ..   跳过真库段（MMO_B1_HAS_DB 未开或缺 gamedb 凭据）\n'
  finish "$TITLE"
fi

echo "== 6. 真库：吃丹扣一颗、突破扣丹、拍卖可挂、商城不卖 =="
HAVE_DB=1
"$DOTNET" "$PROBE" --migrate --config config/character.json >"$TMP/migrate.log" 2>&1
grep -q '^migrate=ok' "$TMP/migrate.log" \
  && ok "迁移清单跑通（含 v30 的 pill_kind / pill_count）" \
  || { bad "迁移失败"; tail -6 "$TMP/migrate.log" | detail; finish "$TITLE"; }

ROOT="$ROOT" TMP="$TMP" CHAR_PORT="$CHAR_PORT" GATEWAY_PORT="$GATEWAY_PORT" python3 - <<'PY'
import json, os
tmp = os.environ["TMP"]
# 突破要掷一次成功 / 失败，核对不能靠运气：临时表把成功率抬到 100。
realms = json.load(open("config/realms.json", encoding="utf-8"))
for step in realms["advance"]:
    step["success_pct"] = 100
json.dump(realms, open(tmp + "/realms-100.json", "w", encoding="utf-8"), ensure_ascii=False)
json.dump({
    "schema": "character-config/1",
    "listen": "http://127.0.0.1:" + os.environ["CHAR_PORT"],
    "db_pool_max": 6, "bag_slots": 40,
    "dungeon_table": "config/dungeons.json", "exp_table": "config/exp.json",
    "realm_table": tmp + "/realms-100.json",
}, open(tmp + "/character.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
json.dump({
    "schema": "gateway-config/1",
    "listen": "http://127.0.0.1:" + os.environ["GATEWAY_PORT"],
    "token_ttl_s": 7200, "pbkdf2_iterations": 210000,
    "battle_upstream": "ws://127.0.0.1:60001/battle",
    "character_upstream": "http://127.0.0.1:" + os.environ["CHAR_PORT"],
    "world_upstream": "http://127.0.0.1:63108",
}, open(tmp + "/gateway.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
printf '%s\n' "$TEST_PASSWORD" | "$DOTNET" "$GATEWAY" --seed-account "$ACCOUNT" \
  --seed-slots 8 --config "$TMP/gateway.json" >>"$TMP/seed.log" 2>&1

"$DOTNET" "$PROBE" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & CHARACTER_PID=$!
"$DOTNET" "$GATEWAY" --config "$TMP/gateway.json" >"$TMP/gateway.log" 2>&1 & GATEWAY_PID=$!
if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
   && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关"; then
  ok "角色服与网关起来"
else
  tail -10 "$TMP/character.log" | detail; finish "$TITLE"
fi

post() {
  curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}$1?token=${TOKEN}" \
    -H 'Content-Type: application/json' -d "$2"
}
CHAR() { "$DOTNET" "$PROBE" "$@" --config "$TMP/character.json" 2>&1 | utf8_trim; }
utf8_trim() { sed -e 's/\r$//' | grep -v '^$'; }

TOKEN="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/login" -H 'Content-Type: application/json' \
  -d "{\"account\":\"$ACCOUNT\",\"password\":\"${TEST_PASSWORD}\"}" \
  | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("token",""))
except Exception: print("")')"
[ -n "$TOKEN" ] || { bad "登录没拿到令牌"; finish "$TITLE"; }

CHAR_NAME="p4b1_$(date +%s | tail -c 6)"
WAR="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/character?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"name\":\"$CHAR_NAME\",\"race\":\"mai\",\"class_id\":\"warrior\"}" \
  | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")')"
[ -n "$WAR" ] || { bad "角色没建起来"; finish "$TITLE"; }
ok "造一个角色（${WAR}）"

# 发丹：三颗回春丹放第 20 格
CHAR --raw-item-write "$WAR" 20 pill_rejuvenate 3 >/dev/null
STATE="$(CHAR --pill-state "$WAR" | sed -n 's/^pill_state=ok //p')"
printf '%s' "$STATE" | grep -q 'pill_rejuvenate=3' \
  && ok "发到背包：回春丹 3 颗（${STATE}）" \
  || bad "发丹后持有量不对：$STATE"

# 吃一颗：扣一颗、剩两颗
TX="b1-$$-$(date +%s)-a"
R="$(CHAR --pill-spend "$WAR" pill_rejuvenate "$TX" --pill-txn "$TX" | sed -n 's/^pill_spend_ok=//p')"
printf '%s' "$R" | grep -q '^true reason= item=pill_rejuvenate count_left=2 .*duplicate=false$' \
  && ok "吃一颗：扣一颗，背包剩 2（${R}）" \
  || bad "吃一颗的结果不对：$R"

# 同一个号重放：不再扣第二颗
R="$(CHAR --pill-spend "$WAR" pill_rejuvenate "$TX" --pill-txn "$TX" | sed -n 's/^pill_spend_ok=//p')"
printf '%s' "$R" | grep -q 'count_left=2 .*duplicate=true$' \
  && ok "同一个事务号重放：不扣第二颗（${R}）" \
  || bad "重放扣了第二颗：$R"

# 吃光之后没有丹：no_pill，颗数不动
CHAR --raw-item-write "$WAR" 20 pill_rejuvenate 0 >/dev/null
R="$(CHAR --pill-spend "$WAR" pill_qixue "b1-$$-$(date +%s)-b" \
     --pill-txn "b1-$$-$(date +%s)-b" | sed -n 's/^pill_spend_ok=//p')"
printf '%s' "$R" | grep -q '^false reason=no_pill' \
  && ok "背包没有这种丹：no_pill，一颗都不扣（${R}）" \
  || bad "没丹时不是 no_pill：$R"

# 突破：先没有丹 → pill_not_enough；补丹之后成
CHAR --raw-realm-phase-write "$WAR" 8 >/dev/null
CHAR --raw-main-stage-write "$WAR" 9 >/dev/null
CHAR --raw-drill-write "$WAR" 100 >/dev/null
CHAR --raw-currency-add "$WAR" stone_1 10 >/dev/null
R="$(CHAR --advance-realm "$WAR" "b1-$$-$(date +%s)-nopill" | sed -n 's/^advance_realm=done //p')"
printf '%s' "$R" | grep -q 'realm=炼体 reason=pill_not_enough .*pill=pill_zhuji pill_count=1 pill_left=0' \
  && ok "没有筑基丹：pill_not_enough，修为不动（${R}）" \
  || bad "缺丹时没挡住：$R"

CHAR --raw-item-write "$WAR" 21 pill_zhuji 2 >/dev/null
R="$(CHAR --advance-realm "$WAR" "b1-$$-$(date +%s)-ok" | sed -n 's/^advance_realm=done //p')"
printf '%s' "$R" | grep -q 'success=true .*realm=筑基 .*pill=pill_zhuji pill_count=1 pill_left=1' \
  && ok "有丹：突破成功进筑基，筑基丹 2 → 1（${R}）" \
  || bad "带丹突破的结果不对：$R"

# 炼丹耗药材：按仓库里那条真实配方走一遍 start → finish，药材被扣、丹入背包
RECETA="$(python3 -c 'import json;r=[x for x in json.load(open("config/recipes.json",encoding="utf-8"))["recipes"] if x["profession"]=="alchemy" and x["output"]["item_id"]=="pill_rejuvenate"][0];print(r["id"])')"
NEED="$(python3 -c 'import json;r=[x for x in json.load(open("config/recipes.json",encoding="utf-8"))["recipes"] if x["profession"]=="alchemy" and x["output"]["item_id"]=="pill_rejuvenate"][0];print(r["inputs"][0]["count"])')"
WAIT="$(python3 -c 'import json;r=[x for x in json.load(open("config/recipes.json",encoding="utf-8"))["recipes"] if x["profession"]=="alchemy" and x["output"]["item_id"]=="pill_rejuvenate"][0];print(r["craft_time_s"])')"
CHAR --raw-item-write "$WAR" 23 herb_ling "$NEED" >/dev/null
CHAR --raw-currency-add "$WAR" gold 200 >/dev/null
R="$(CHAR --craft-start "$WAR" "$RECETA" --craft-txn "b1-$$-$(date +%s)-craft" | sed -n 's/^craft_start=//p')"
printf '%s' "${R}" | grep -q '^ok' \
  && ok "炼丹开工：按配方扣药材与费用（${RECETA}，灵草 ${NEED} 株）" \
  || bad "炼丹开工失败：${R}"
HERB_AFTER="$(CHAR --item-count "$WAR" herb_ling | sed -n 's/^item_count=\([0-9]*\) .*/\1/p')"
if [ "${HERB_AFTER}" = "0" ]; then
  ok "开工即扣药材：灵草 ${NEED} 株被扣光（现 ${HERB_AFTER}）"
else
  bad "炼丹开工没扣药材：${HERB_AFTER}"
fi
sleep "$((WAIT + 2))"
R="$(CHAR --craft-finish "$WAR" --craft-txn "b1-$$-$(date +%s)-craftdone" | sed -n 's/^craft_finish=//p')"
printf '%s' "${R}" | grep -q '^ok .*success=true output=pill_rejuvenate count=1' \
  && ok "到点收取：产出回春丹 1 颗（成功率 100 的配方）" \
  || bad "收取结果不对：${R}"
STATE2="$(CHAR --pill-state "$WAR" | sed -n 's/^pill_state=ok //p')"
printf '%s' "${STATE2}" | grep -q 'pill_rejuvenate=1' \
  && ok "炼丹产出进背包：回春丹 1 颗（${STATE2}）" \
  || bad "炼丹没产出到背包：${STATE2}"

# 拍卖可交易：丹到账不绑，所以交易判定必须放行（拍卖入口只收可交易物品）
CHAR --raw-item-write "$WAR" 22 pill_rejuvenate 2 >/dev/null
R="$(post /inventory/can-trade "{\"character_id\":$WAR,\"slot\":22}")"
printf '%s' "$R" | grep -Eq '"tradable": *true' \
  && ok "回春丹可以上拍卖（到账不绑，can-trade 放行）" \
  || bad "丹药不能交易：$R"

# 商城不卖：拿丹药编号当商品编号买 → 被拒且不扣元宝
yuanbao() {
  curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/currency?token=${TOKEN}&character_id=$1" \
    | python3 -c 'import json,sys
try:
    print([x.get("amount") for x in json.load(sys.stdin).get("balances",[]) if x.get("kind")=="yuanbao"][0])
except Exception:
    print("")'
}
BEFORE="$(yuanbao "$WAR")"
R="$(post /shop/buy "{\"character_id\":$WAR,\"item_id\":\"pill_rejuvenate\"}")"
AFTER="$(yuanbao "$WAR")"
if [ -n "$BEFORE" ] && ! printf '%s' "$R" | grep -Eq '"ok": *true' && [ "$BEFORE" = "$AFTER" ]; then
  ok "商城不卖丹药：按丹药编号买被拒，元宝 ${BEFORE} 未动"
elif [ -z "$BEFORE" ]; then
  bad "读不到元宝余额，商城禁售这条没法核"
else
  bad "商城买了丹药或扣了元宝：$R（${BEFORE} → ${AFTER}）"
fi

finish "$TITLE"
