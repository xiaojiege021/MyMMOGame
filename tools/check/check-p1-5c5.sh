#!/usr/bin/env bash
# P1-5c5 核对：蓝符只回复自己的蓝（方案 12.1、开发架构第 14 节第 44 行）。
#
# 完成标准：不回血，不提高伤害。回复不超过同级蓝药。和红符可以各用一次。
#
# 四种模式：
#   bash tools/check/check-p1-5c5.sh
#       编译 + 静态 + 只读入口 + 坏表，不连库不连服（Mac 上就能全跑）
#   MMO_P1_5C5_GATEWAY=http://127.0.0.1:61000 MMO_P1_5C5_TOKEN=<令牌> \
#     MMO_P1_5C5_CHARACTER=<角色号> bash tools/check/check-p1-5c5.sh
#       再加真网关段：买一张蓝符（扣款、入包、绑定）
#   MMO_P1_5C5_BATTLE=ws://127.0.0.1:60194/battle?char=<角色号> \
#     MMO_P1_5C5_BATTLE_DEADLY=ws://127.0.0.1:60195/battle?char=<角色号> \
#     MMO_P1_5C5_PROBE=<BattleProbe.dll> bash tools/check/check-p1-5c5.sh
#       再加战斗段：只回蓝不回血、不提高伤害、红蓝各一次、满蓝 / 冷却 / 编号 / 倒地 / 背包空
#   MMO_P1_5C5_SKIP_BUILD=1 bash tools/check/check-p1-5c5.sh
#       跳过编译（已经 build 过时用）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY="${MMO_P1_5C5_GATEWAY:-}"
TOKEN="${MMO_P1_5C5_TOKEN:-}"
CHARACTER="${MMO_P1_5C5_CHARACTER:-0}"
BATTLE="${MMO_P1_5C5_BATTLE:-}"
BATTLE_DEADLY="${MMO_P1_5C5_BATTLE_DEADLY:-}"
PROBE="${MMO_P1_5C5_PROBE:-$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll}"
SKILL="${MMO_P1_5C5_SKILL:-1002}"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

# ---------------------------------------------------------------- 1. 编译

echo "== 1. 编译 =="
if [ "${MMO_P1_5C5_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_P1_5C5_SKIP_BUILD=1）\n'
else
  if "$DOTNET" build src/MMOGame.sln -c Release --nologo -v q > "$TMP/build.log" 2>&1 \
     && "$DOTNET" build tools/probe/BattleProbe/BattleProbe.csproj -c Release --nologo -v q \
          > "$TMP/build-probe.log" 2>&1; then
    ok "服务端与探针编译通过"
  else
    bad "编译失败"; tail -20 "$TMP/build.log" "$TMP/build-probe.log" | detail
  fi
fi

# ---------------------------------------------------------------- 2. 静态断言

echo "== 2. 静态断言 =="
ROOT="$ROOT" python3 - "$TMP" <<'PY' > "$TMP/static.txt" 2>&1
import json, os, re, sys

root = os.environ["ROOT"]
def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

files = {
    "config/talisman.json": read("config/talisman.json"),
    "config/items.json": read("config/items.json"),
    "config/shop.json": read("config/shop.json"),
    "src/Character/TalismanTable.cs": read("src/Character/TalismanTable.cs"),
    "src/Character/ShopTable.cs": read("src/Character/ShopTable.cs"),
    "src/Character/CharacterGrpcService.cs": read("src/Character/CharacterGrpcService.cs"),
    "src/Battle/TalismanSpend.cs": read("src/Battle/TalismanSpend.cs"),
    "src/Battle/Encounter.cs": read("src/Battle/Encounter.cs"),
    "proto/mmo/battle.proto": read("proto/mmo/battle.proto"),
    "proto/mmo/character.proto": read("proto/mmo/character.proto"),
    "tools/probe/BattleProbe/Program.cs": read("tools/probe/BattleProbe/Program.cs"),
}
report("files", all(files.values()),
       "missing " + ",".join(k for k, v in files.items() if not v))

talisman = json.loads(files["config/talisman.json"])
items = json.loads(files["config/items.json"])
shop = json.loads(files["config/shop.json"])
red, blue = talisman.get("red") or {}, talisman.get("blue") or {}
item_by_id = {i["id"]: i for i in items["items"]}

report("talisman_schema",
       talisman.get("schema") == "talisman-table/1" and int(talisman.get("version", 0)) >= 2,
       str(talisman.get("schema")) + " v" + str(talisman.get("version")))

blue_item = blue.get("item_id", "")
goods = item_by_id.get(blue_item)
report("blue_item",
       bool(goods) and goods.get("kind") == "consumable" and goods.get("bind") == "always",
       "item=" + blue_item + " " + json.dumps(goods, ensure_ascii=False) if goods else "缺物品")

# 完成标准「回复不超过同级蓝药」：配置里卡死，不在运行时兜。
report("restore_under_potion",
       int(blue.get("mana_restore", 0)) >= 1
       and int(blue.get("potion_mana_restore", 0)) >= 1
       and int(blue.get("mana_restore", 0)) <= int(blue.get("potion_mana_restore", 0)),
       "mana=" + str(blue.get("mana_restore")) + " potion=" + str(blue.get("potion_mana_restore")))

# 方案 12.1：蓝符和蓝药共用 mana 组；红符的 heal 组没被带坏；两组必须不同。
report("blue_group_mana",
       blue.get("cooldown_group") == "mana" and int(blue.get("cooldown_ms", 0)) >= 1,
       "group=" + str(blue.get("cooldown_group")))
report("red_group_kept",
       red.get("cooldown_group") == "heal"
       and int(red.get("hp_restore", 0)) <= int(red.get("potion_hp_restore", 0)))
report("groups_differ",
       bool(red.get("cooldown_group")) and red.get("cooldown_group") != blue.get("cooldown_group"))
report("items_differ", bool(red.get("item_id")) and red.get("item_id") != blue_item)

blue_shop = [i for i in shop["items"] if i["kind"] == "blue_talisman"]
red_shop = [i for i in shop["items"] if i["kind"] == "red_talisman"]
report("shop_blue_delivery",
       len(blue_shop) == 1
       and blue_shop[0].get("delivery_item_id") == blue_item
       and blue_shop[0].get("delivery_count") == 1,
       json.dumps(blue_shop, ensure_ascii=False))
report("shop_red_kept",
       len(red_shop) == 1 and red_shop[0].get("delivery_item_id") == red.get("item_id"))
# 方案 12.1「蓝符不是蓝药」：清单里没有把凝神丹 / 回灵丹当发货物。
report("shop_no_potion",
       not any("丹" in (i.get("delivery_item_id") or "") for i in shop["items"])
       and blue_item not in ("",) and "talisman" in blue_item)

shop_cs = files["src/Character/ShopTable.cs"]
kinds = re.search(r"DeliveredKinds\s*=\s*(?:new\s*)?(?:string\[\]\s*)?\{([^}]*)\}", shop_cs)
kinds_text = kinds.group(1) if kinds else ""
report("delivered_kinds",
       all(('"' + k + '"') in kinds_text
           for k in ("protect_ticket", "red_talisman", "blue_talisman")),
       "白名单没接蓝符：" + kinds_text)

table_cs = files["src/Character/TalismanTable.cs"]
report("table_rejects_over_potion",
       "ManaRestore > raw.PotionManaRestore" in table_cs
       and "HpRestore > raw.PotionHpRestore" in table_cs,
       "蓝符上限校验不在配置加载里")
report("table_rejects_same_group",
       "string.Equals(red.CooldownGroup, blue.CooldownGroup" in table_cs)
report("table_rejects_same_item",
       "string.Equals(red.ItemId, blue.ItemId" in table_cs)
report("table_two_pools",
       "TalismanPool.Mana" in table_cs and "TalismanPool.Hp" in table_cs)

# 请求里没有目标：符只回复自己，客户端连「想给谁用」都表达不了。
battle_proto = files["proto/mmo/battle.proto"]
start = battle_proto.find("message UseTalismanRequest")
end = battle_proto.find("}", start)
use_req = battle_proto[start:end] if start >= 0 else ""
report("no_target_in_request",
       start >= 0 and "target" not in use_req.lower(),
       "请求段里出现了 target：" + use_req.replace("\n", " ")[:120])

char_proto = files["proto/mmo/character.proto"]
report("pool_enum",
       "enum TalismanPool" in char_proto
       and "TALISMAN_POOL_HP = 1" in char_proto
       and "TALISMAN_POOL_MANA = 2" in char_proto)

# 只读查询只判「是不是符、落在哪个池」，不写库、不把回复值放进请求。
d_start = char_proto.find("message DescribeTalismanRequest")
d_end = char_proto.find("message DescribeTalismanResponse", d_start)
describe_req = char_proto[d_start:d_end] if d_start >= 0 else ""
report("describe_request_readonly",
       d_start >= 0
       and "character_id" in describe_req
       and "item_id" in describe_req
       and "restore" not in describe_req
       and "cooldown" not in describe_req)

svc = files["src/Character/CharacterGrpcService.cs"]
s_start = svc.find("public override async Task<DescribeTalismanResponse> DescribeTalisman(")
s_end = svc.find("    /// <summary>", s_start + 10)
describe_body = svc[s_start:s_end] if s_start >= 0 and s_end > s_start else ""
report("describe_no_write",
       "SpendAsync" not in describe_body
       and "insert" not in describe_body.lower()
       and "delete" not in describe_body.lower()
       and "talismanTable.Find" in describe_body)

c_start = char_proto.find("message ConsumeTalismanRequest")
c_end = char_proto.find("message ConsumeTalismanResponse", c_start)
consume_req = char_proto[c_start:c_end] if c_start >= 0 else ""
report("consume_request_body",
       "string account = 1" in consume_req
       and "uint64 character_id = 2" in consume_req
       and "string item_id = 3" in consume_req
       and "string txn_id = 4" in consume_req
       and "hp_restore" not in consume_req
       and "mana_restore" not in consume_req
       and "cooldown" not in consume_req,
       "请求体里混进了服务端算的数")

enc = files["src/Battle/Encounter.cs"]
u_start = enc.find("public ServerMessage UseTalisman(")
u_end = enc.find("// ---- 时间推进 ----", u_start)
use_body = enc[u_start:u_end] if u_start >= 0 and u_end > u_start else ""
report("one_pool_only",
       u_start >= 0 and u_end > u_start
       and "member.Hp +=" in use_body
       and "member.Mana +=" in use_body
       and "TargetId" not in use_body
       and "target." not in use_body
       and "DamageDealt" not in use_body
       and "ThreatStability" not in use_body
       and "Spirit" not in use_body
       and "ManaMax =" not in use_body,
       "用符那一段碰了伤害 / 属性 / 目标")
report("restore_from_reply",
       "spend.ManaRestore" in use_body and "spend.HpRestore" in use_body
       and "80" not in use_body,
       "回复值不是只认角色服回包")
report("describe_before_spend",
       0 <= use_body.find("_talismans.Describe") < use_body.find("_talismans.Spend"),
       "没先查池就去扣符，满蓝 / 冷却会白扣一张")
report("spend_before_apply",
       use_body.find("_talismans.Spend") < use_body.find("member.Mana +=")
       and use_body.find("_talismans.Spend") < use_body.find("member.Hp +="),
       "扣符与回复的先后不对")
report("separate_groups",
       "ManaGroupReadyAtMs" in use_body and "HealGroupReadyAtMs" in use_body
       and "member.ManaGroupReadyAtMs =" in use_body
       and "member.HealGroupReadyAtMs =" in use_body,
       "红蓝没有各写各的组")
report("no_revive_on_self",
       'if (member.Down)' in use_body and 'return "caster_down"' in use_body)
report("no_damage_route",
       "member.DamageDealt" not in use_body and "EffectiveThreat" not in use_body)

spend_cs = files["src/Battle/TalismanSpend.cs"]
report("describe_is_readonly_channel",
       "DescribeTalisman(" in spend_cs
       and "ConsumeTalisman(" in spend_cs
       and "delete from" not in spend_cs.lower()
       and "insert into" not in spend_cs.lower())

# 蓝符的编号只活在三份配置里，源码不写死；副本 / 任务 / 月叠没有发货路径。
src_hits = []
for base, _, names in os.walk(os.path.join(root, "src")):
    for n in names:
        if n.endswith(".cs") and not n.endswith(".g.cs"):
            p = os.path.join(base, n)
            if '"' + blue_item + '"' in open(p, encoding="utf-8").read():
                src_hits.append(os.path.relpath(p, root))
report("no_hardcoded_blue_item", not src_hits, ",".join(src_hits))

gw_proc = read("src/Gateway/GatewayProcess.cs")
report("no_client_route",
       "talisman" not in gw_proc.lower(),
       "网关不该有用符路由：用符走战斗服到角色服的服间调用")

probe = files["tools/probe/BattleProbe/Program.cs"]
report("probe_cases",
       all(('"' + c + '"') in probe for c in (
           "p15c5_blue", "p15c5_blue_full", "p15c5_blue_twice",
           "p15c5_red_blue_both", "p15c5_blue_damage", "p15c5_blue_other",
           "p15c5_blue_down", "p15c5_blue_none")))
PY
cat "$TMP/static.txt" | sed 's/^PASS /  ok   /; s/^FAIL /  FAIL /'
grep -q '^PASS ' "$TMP/static.txt" || bad "静态断言没跑起来"
if grep -q '^FAIL ' "$TMP/static.txt"; then
  bad "静态断言有未通过项"
else
  ok "静态断言全过（$(grep -c '^PASS ' "$TMP/static.txt") 条）"
fi

# ---------------------------------------------------------------- 3. 只读入口

echo "== 3. 只读入口与坏表 =="

if "$DOTNET" "$CHARACTER_DLL" --talisman-list > "$TMP/talis.txt" 2>&1; then
  head1="$(head -1 "$TMP/talis.txt")"
  case "$head1" in
    *"talisman_list_state=ok"*"cooldown_group=heal"*"blue_item=talisman_blue"*"blue_group=mana"*)
      ok "符表两种都读得出来：$head1" ;;
    *) bad "符表头部不对：$head1" ;;
  esac
  # 按空格切词、只认前缀完全相等的那一个：sed 的 .* 会贪婪地把
  # potion_mana_restore 当成 mana_restore，取出来的是上限而不是回复值。
  word_of() { printf '%s\n' "$head1" | python3 -c 'import sys
key = sys.argv[1]
for t in sys.stdin.read().split():
    if t.startswith(key + "="):
        print(t.split("=", 1)[1]); break' "$1"; }
  mana="$(word_of mana_restore)"
  potion="$(word_of potion_mana_restore)"
  if [ -n "$mana" ] && [ -n "$potion" ] && [ "$mana" -le "$potion" ]; then
    ok "蓝符回复 ${mana} 不超过同级蓝药 ${potion}"
  else
    bad "回复与上限对不上：mana=${mana:-?} potion=${potion:-?}"
  fi
else
  bad "符表读取失败"; tail -3 "$TMP/talis.txt" | detail
fi

# 蓝符的八类坏表都要起不来，而且要说清是哪一条。
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
base = json.load(open(os.path.join(root, "config/talisman.json"), encoding="utf-8"))
cases = {
    "blue_over_potion": lambda j: j["blue"].__setitem__("mana_restore", j["blue"]["potion_mana_restore"] + 1),
    "blue_zero_restore": lambda j: j["blue"].__setitem__("mana_restore", 0),
    "blue_group_heal": lambda j: j["blue"].__setitem__("cooldown_group", "heal"),
    "blue_zero_cooldown": lambda j: j["blue"].__setitem__("cooldown_ms", 0),
    "blue_unknown_item": lambda j: j["blue"].__setitem__("item_id", "no_such_item"),
    "blue_not_consumable": lambda j: j["blue"].__setitem__("item_id", "ticket_protect"),
    "blue_same_item": lambda j: j["blue"].__setitem__("item_id", j["red"]["item_id"]),
    "no_blue": lambda j: j.pop("blue", None),
}
for name, fn in cases.items():
    j = json.loads(json.dumps(base))
    fn(j)
    with open(os.path.join(tmp, name + ".json"), "w", encoding="utf-8") as f:
        json.dump(j, f, ensure_ascii=False)
PY

for bad_case in blue_over_potion blue_zero_restore blue_group_heal blue_zero_cooldown \
                blue_unknown_item blue_not_consumable blue_same_item no_blue; do
  out="$("$DOTNET" "$CHARACTER_DLL" --talisman-list --talisman-config "$TMP/$bad_case.json" 2>&1)"
  code=$?
  if [ "$code" = "2" ] && printf '%s' "$out" | grep -q 'config_error='; then
    ok "坏表 ${bad_case}：起不来（$(printf '%s' "$out" | head -1 | cut -c1-46)…）"
  else
    bad "坏表 ${bad_case} 没被挡住（exit=$code）：$out"
  fi
done

if "$DOTNET" "$CHARACTER_DLL" --shop-list > "$TMP/shop.txt" 2>&1; then
  if grep -q 'shop_item=shop_blue_talisman_1 .*delivery=talisman_bluex1' "$TMP/shop.txt" \
     && grep -q 'shop_item=shop_red_talisman_1 .*delivery=talisman_redx1' "$TMP/shop.txt"; then
    ok "清单里蓝符发 talisman_bluex1、红符仍发 talisman_redx1"
  else
    bad "清单里的发货物不对"; grep -E 'shop_item=shop_(blue|red)_talisman' "$TMP/shop.txt" | detail
  fi
else
  bad "清单读取失败"; tail -3 "$TMP/shop.txt" | detail
fi

# ---------------------------------------------------------------- 4. 真网关段

echo "== 4. 真网关段 =="
if [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （跳过：没给 MMO_P1_5C5_GATEWAY / MMO_P1_5C5_TOKEN / MMO_P1_5C5_CHARACTER）\n'
else
  blue_of() {
    printf '%s' "$1" | python3 -c 'import json,sys
try:
    d = json.load(sys.stdin)
except Exception:
    print("count=-1 bound=false"); raise SystemExit
slots = [s for s in d.get("slots", []) if s.get("item_id") == "talisman_blue"]
print("count=%d bound=%s" % (
    sum(int(s.get("count") or 0) for s in slots),
    "true" if slots and all(s.get("bound") for s in slots) else "false"))'
  }

  yuanbao_of() {
    printf '%s' "$1" | python3 -c 'import json,sys
try:
    d = json.load(sys.stdin)
except Exception:
    print(""); raise SystemExit
for b in d.get("balances", []):
    if b.get("kind") == "yuanbao":
        print(b.get("amount")); break
else:
    print("")'
  }

  buy() {
    curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' \
      -X POST "$GATEWAY/shop/buy" -H "Authorization: Bearer $TOKEN" \
      -H 'Content-Type: application/json' -d "$1" 2>&1
  }

  bag() {
    curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
      "$GATEWAY/inventory?character_id=$CHARACTER" 2>&1
  }

  money() {
    curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
      "$GATEWAY/currency?character_id=$CHARACTER" 2>&1
  }

  before_money="$(yuanbao_of "$(money)")"
  before_blue="$(blue_of "$(bag)")"

  resp="$(buy "{\"character_id\":$CHARACTER,\"item_id\":\"shop_blue_talisman_1\"}")"
  body="$(body_of "$resp")"
  if [ "$(expect_json "$body" ok=true delivered=true bound=true)" = "ok" ] \
     && [ "$(json_field "$body" charged_yuanbao)" = "15" ]; then
    ok "买一张蓝符：扣 15 元宝、入包、到账即绑"
  else
    bad "买蓝符的回包不对：$body"
  fi

  after_money="$(yuanbao_of "$(money)")"
  after_blue="$(blue_of "$(bag)")"
  if [ -n "$before_money" ] && [ "$after_money" = "$((before_money - 15))" ]; then
    ok "元宝 $before_money → $after_money"
  else
    bad "元宝不对：before=${before_money:-?} after=${after_money:-?}"
  fi
  before_count="${before_blue#count=}"; before_count="${before_count%% *}"
  after_count="${after_blue#count=}"; after_count="${after_count%% *}"
  if [ "$after_count" = "$((before_count + 1))" ] \
     && printf '%s' "$after_blue" | grep -q 'bound=true'; then
    ok "背包蓝符 ${before_count} → ${after_count}，且到账即绑"
  else
    bad "背包蓝符不对：before=${before_blue} after=${after_blue}"
  fi

  resp="$(buy "{\"character_id\":$CHARACTER,\"item_id\":\"shop_no_such_item\"}")"
  body="$(body_of "$resp")"
  if [ "$(expect_json "$body" ok=false reason=not_in_list)" = "ok" ]; then
    ok "清单外：仍是 not_in_list"
  else
    bad "清单外的回包不对：$body"
  fi
fi

# ---------------------------------------------------------------- 5. 战斗段

echo "== 5. 战斗段 =="
if [ -z "$BATTLE" ] || [ ! -f "$PROBE" ]; then
  printf '       （跳过：没给 MMO_P1_5C5_BATTLE 或找不到 BattleProbe）\n'
else
  run_probe() {
    local url="$1"; shift
    local out
    out="$("$DOTNET" "$PROBE" --url "$url" "$@" 2>&1)"
    printf '%s\n' "$out" | tail -3 | detail
    printf '%s' "$out" | grep -q 'verdict=ok'
  }

  blue=talisman_blue
  red=talisman_red

  if run_probe "$BATTLE" --case p15c5_blue --blue "$blue" --skill "$SKILL"; then
    ok "蓝符只回自己的蓝：Healed=0、血不动、mana_after 对得上"
  else
    bad "蓝符回血了，或者回蓝量不对"
  fi

  if run_probe "$BATTLE" --case p15c5_blue_full --blue "$blue"; then
    ok "满蓝用符：mana_full，不去扣符"
  else
    bad "满蓝没被挡住（会白烧一张）"
  fi

  if run_probe "$BATTLE" --case p15c5_blue_twice --blue "$blue" --skill "$SKILL"; then
    ok "连着两张：第二张撞 mana 组冷却"
  else
    bad "蓝符自己的组冷却没生效"
  fi

  if run_probe "$BATTLE" --case p15c5_red_blue_both --item "$red" --blue "$blue" --skill "$SKILL"; then
    ok "红符与蓝符各用一次互不占冷却，各自第二次撞各自的组"
  else
    bad "红蓝没做到各用一次"
  fi

  if run_probe "$BATTLE" --case p15c5_blue_damage --blue "$blue" --skill "$SKILL"; then
    ok "用符前后同一技能伤害相同：不提高伤害、不提高精神"
  else
    bad "蓝符改变了伤害或蓝量上限"
  fi

  if run_probe "$BATTLE" --case p15c5_blue_other --other ticket_protect; then
    ok "拿保护券当符用：not_talisman，不扣不回"
  else
    bad "别的物品编号没被挡住"
  fi

  if [ -n "$BATTLE_DEADLY" ]; then
    if run_probe "$BATTLE_DEADLY" --case p15c5_blue_down --blue "$blue"; then
      ok "倒地用蓝符：caster_down（符不复活）"
    else
      bad "倒地还能用符"
    fi
  else
    printf '       （跳过倒地那一条：没给 MMO_P1_5C5_BATTLE_DEADLY）\n'
  fi

  # 「背包一张不剩」靠上面四个用符场景把蓝符用光：顺序不能调换。
  # 先把余量打出来，免得把「还有符」读成「背包空了还能用」。
  left_blue="?"
  if [ -n "$GATEWAY" ] && [ -n "$TOKEN" ] && [ "$CHARACTER" != "0" ]; then
    left_blue="$(blue_of "$(bag)")"
    printf '%s\n' "四个用符场景之后背包蓝符 ${left_blue}" | detail
  fi

  if run_probe "$BATTLE" --case p15c5_blue_none --blue "$blue" --skill "$SKILL"; then
    ok "背包一张不剩：no_talisman，血与蓝都不动"
  else
    bad "没符还能用（跑到这里时背包余量 ${left_blue}，不是 0 就说明前面的用符场景没各扣一张）"
  fi
fi

finish "P1-5c5（server + battle）"
