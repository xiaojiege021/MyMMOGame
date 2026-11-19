#!/usr/bin/env bash
# tools/check/check-k1.sh —— K1 商城 / 家园币商城配置化（开发架构 14.7 顺序 1）。
#
# 完成标准（14.7 的 K1 行，逐条对应下面的断言）：
#   1. shop.json / home_coin_shop.json 每条带 delivery.mode 与限购 / 库存；
#   2. ShopTable 的 18 类 / 禁售 / 已发货三张硬编码表进配置；
#   3. BuyShopItem 不再按 season_pass / monthly_bundle / second_slot 分支；
#   4. 加一件商品只改配置。
#
#   bash tools/check/check-k1.sh
#   MMO_K1_SKIP_BUILD=1 bash tools/check/check-k1.sh   跳过编译
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CHARACTER_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TMP="$(mktemp -d)"
TITLE="K1（server）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_K1_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_K1_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/Character/Character.csproj -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "角色服编译通过"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言 =="
ROOT="$ROOT" python3 - > "$TMP/static.txt" 2>&1 <<'PY'
import json, os, re

root = os.environ["ROOT"]

def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def cfg(p):
    return json.loads(read(p))

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

# 方案 12.1 的十八种与「绝不上架」那一串：这是方案正文，只能搬进配置，不能改。
KINDS_121 = ["outfit", "weapon_skin", "realm_skin", "flight_trail", "mount_skin",
             "home_decor", "guild_display", "bag_expand", "teleport_talisman",
             "red_talisman", "blue_talisman", "revive_scroll", "unbind_item",
             "exp_booster", "monthly_bundle", "second_slot", "season_pass",
             "protect_ticket", "rename_card"]
FORBIDDEN_121 = ["gold", "enhance_stone", "transfer_stone", "crystal", "spirit_stone",
                 "pill", "ore", "herb", "equip_blueprint", "equip", "attr_item",
                 "revive_coin", "fatigue", "dungeon_count", "weekly_share",
                 "realm_speed", "guild_tech", "territory_bonus"]
MODES = ("none", "item", "season_pass", "monthly_bundle", "second_slot")
HOME_CATS = ["outfit", "weapon_skin", "realm_skin", "flight_trail", "mount_skin",
             "home_decor", "guild_display", "bag_expand", "teleport_talisman"]

shop = cfg("config/shop.json")
home = cfg("config/home_coin_shop.json")
items = {x["id"]: x for x in cfg("config/items.json")["items"]}
currencies = [c["id"] for c in cfg("config/currency.json")["currencies"]]
shop_cs = read("src/Character/ShopTable.cs")
home_cs = read("src/Character/HomeCoinShopTable.cs")
svc = read("src/Character/CharacterGrpcService.cs")

# ---------------------------------------------------------------- 1. 配置字段
# 版本号只增不减：K1 收口时是 11，后面的卡（Q12 加改名卡）会往上走，不钉具体数字。
report("shop_schema_version",
       shop.get("schema") == "shop-table/1" and isinstance(shop.get("version"), int)
       and shop.get("version") >= 11,
       "schema=%s version=%s" % (shop.get("schema"), shop.get("version")))
report("shop_categories_from_config", shop.get("categories") == KINDS_121,
       "config=%s" % ",".join(shop.get("categories") or []))
report("shop_forbidden_from_config", shop.get("forbidden") == FORBIDDEN_121,
       "config=%s" % ",".join(shop.get("forbidden") or []))
report("shop_no_overlap",
       not (set(shop.get("categories") or []) & set(shop.get("forbidden") or []))
       and len(shop.get("categories") or []) == len(set(shop.get("categories") or []))
       and len(shop.get("forbidden") or []) == len(set(shop.get("forbidden") or [])),
       "两张表有交集或重复")

def missing_fields(rows, need):
    bad = []
    for i in rows:
        d = i.get("delivery") or {}
        lack = [k for k in need if k not in i and k != "delivery"]
        if not d.get("mode"):
            lack.append("delivery.mode")
        if i.get("limit") is None:
            lack.append("limit")
        if i.get("stock") is None:
            lack.append("stock")
        if lack:
            bad.append(i.get("id", "?") + ":" + ",".join(lack))
    return bad

report("shop_every_item_fields",
       not missing_fields(shop["items"], ("id", "name", "category", "price", "currency", "on_sale")),
       ";".join(missing_fields(shop["items"], ("id", "name", "category", "price", "currency", "on_sale"))))
report("home_every_item_fields",
       not missing_fields(home["items"], ("id", "name", "category", "price", "currency", "on_sale")),
       ";".join(missing_fields(home["items"], ("id", "name", "category", "price", "currency", "on_sale"))))

bad_mode = [i["id"] for i in shop["items"] if (i.get("delivery") or {}).get("mode") not in MODES]
report("shop_mode_known", not bad_mode, ",".join(bad_mode))
bad_mode_home = [i["id"] for i in home["items"]
                 if (i.get("delivery") or {}).get("mode") not in ("none", "item")]
report("home_mode_known", not bad_mode_home, ",".join(bad_mode_home))

bad_cur = [i["id"] for i in shop["items"] + home["items"]
           if i.get("currency") not in ("yuanbao", "home_coin") or i.get("currency") not in currencies]
report("every_item_currency_in_table", not bad_cur, ",".join(bad_cur))

bad_limit = [i["id"] for i in shop["items"] + home["items"]
             if (i.get("limit") or {}).get("per_character", -1) < 0
             or (i.get("limit") or {}).get("per_day", -1) < 0]
report("every_item_limit_non_negative", not bad_limit, ",".join(bad_limit))
bad_stock = [i["id"] for i in shop["items"] + home["items"]
             if (i.get("stock") or {}).get("rule") not in ("unlimited", "daily", "weekly")]
report("every_item_stock_rule", not bad_stock, ",".join(bad_stock))

# 发货参数：模式与参数必须对得上，不许留半个。
def param_problem(i):
    d = i.get("delivery") or {}
    m = d.get("mode")
    if m == "none":
        return [k for k in ("item_id", "pass_id", "bundle_id") if d.get(k)] or []
    if m == "item":
        p = []
        if d.get("item_id") not in items:
            p.append("item_id")
        if int(d.get("count") or 0) < 1:
            p.append("count")
        return p
    if m == "season_pass":
        return [] if d.get("pass_id") == "current" and not d.get("item_id") else ["pass_id"]
    if m == "monthly_bundle":
        return [] if d.get("bundle_id") == "current" and not d.get("item_id") else ["bundle_id"]
    if m == "second_slot":
        return [] if not d.get("item_id") and not d.get("pass_id") and not d.get("bundle_id") else ["params"]
    return ["mode"]

bad_param = [i["id"] + ":" + ",".join(param_problem(i)) for i in shop["items"] + home["items"] if param_problem(i)]
report("every_item_delivery_params", not bad_param, ";".join(bad_param))

# 方案写定的「同一种只能是这一件」：编号出处必须与各自的配置对上。
def one(cat):
    return next(i for i in shop["items"] if i["category"] == cat)["delivery"]

tal = cfg("config/talisman.json")
report("refs_follow_plan",
       one("protect_ticket")["item_id"] == cfg("config/enhance.json")["protect_item_id"]
       and one("red_talisman")["item_id"] == tal["red"]["item_id"]
       and one("blue_talisman")["item_id"] == tal["blue"]["item_id"]
       and one("revive_scroll")["item_id"] == cfg("config/revive_scroll.json")["scroll"]["item_id"]
       and one("exp_booster")["item_id"] == cfg("config/exp_booster.json")["booster"]["item_id"]
       and one("unbind_item")["item_id"] == cfg("config/unbind_item.json")["unbind"]["item_id"])

# ---------------------------------------------------------------- 2. 三张表进配置
report("home_schema_version",
       home.get("schema") == "home-coin-shop/1" and home.get("version") == 2,
       "version=%s" % home.get("version"))
report("home_categories_from_config", home.get("categories") == HOME_CATS,
       ",".join(home.get("categories") or []))
report("home_forbidden_covers_shop_kinds",
       all(k not in (home.get("categories") or []) for k in KINDS_121[9:]),
       "家园币商城白名单里仍有只该在商城的门类")

# 只查「代码行」里的数组定义：注释里提一句「原来的 DeliveredKinds 没有第二份了」是沿革说明，
# 不算第二份表（K1 复跑时踩到的假红）。
code_lines = [l for l in (shop_cs + home_cs).splitlines() if not l.strip().startswith("//")]
report("code_no_kind_tables",
       not any(re.search(r"string\[\]\s+(AllowedKinds|DeliveredKinds|ForbiddenKinds)", l)
               for l in code_lines),
       "代码里还留着种类硬编码表")
report("code_reads_config_tables",
       "Categories" in shop_cs and "Forbidden" in shop_cs and "Categories" in home_cs
       and "Forbidden" in home_cs, "加载器没从配置读两张表")
report("code_deliverable_from_mode",
       "Mode != DeliveryMode.None" in shop_cs and "Mode != DeliveryMode.None" in home_cs,
       "Deliverable 不在 mode 上")

# ---------------------------------------------------------------- 3. 按 mode 分发
begin = svc.find("public override async Task<BuyShopItemResponse> BuyShopItem(")
end = svc.find("public override async Task<BuyHomeCoinShopResponse> BuyHomeCoinShopItem(", begin)
body = svc[begin:end] if begin >= 0 and end > begin else ""
report("buy_body_found", len(body) > 2000, "len=%d" % len(body))
report("buy_dispatch_by_mode", "switch (item.Mode)" in body, "没有按 delivery.mode 分发")
report("buy_no_kind_branches",
       not any(('item.Kind == "%s"' % k) in body or ('string.Equals(item.Kind, "%s"' % k) in body
               for k in ("season_pass", "monthly_bundle", "second_slot")),
       "还在按种类字符串分支")
report("buy_reply_kind_kept", "Kind = item.Kind" in body, "回包 kind 没填分类")
report("buy_none_pending", "delivery_pending" in body, "none 没回 delivery_pending")
report("buy_on_sale_gate", "not_on_sale" in body, "on_sale 没生效")
report("home_buy_on_sale_gate",
       "not_on_sale" in svc[svc.find("async Task<BuyHomeCoinShopResponse> BuyHomeCoinShopItem("):
                           svc.find("BuyHomeCoinShopResponse HomeCoinShopRejected(")],
       "家园币商城 on_sale 没生效")
report("no_config_write",
       "File.WriteAllText" not in shop_cs and "File.WriteAllText" not in home_cs,
       "加载器在写配置")

print("PASS static_end")
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static.txt"
if ! grep -qE '^(PASS|FAIL) ' "$TMP/static.txt"; then
  bad "静态断言没跑起来"; tail -12 "$TMP/static.txt" | detail
elif ! grep -q '^PASS static_end' "$TMP/static.txt"; then
  bad "静态断言没跑完"; grep -vE '^(PASS|FAIL) ' "$TMP/static.txt" | tail -6 | detail
fi

echo "== 3. 只读入口 =="
# 版本号从配置现取：K1 之后每加一条商品就会升版本，钉死会一直红。
SHOP_VERSION="$(python3 -c 'import json;print(json.load(open("config/shop.json"))["version"])')"
if "$DOTNET" "$CHARACTER_DLL" --shop-list > "$TMP/list.txt" 2>&1; then
  head1="$(head -1 "$TMP/list.txt")"
  case "$head1" in
    "shop_list_state=ok version=${SHOP_VERSION} "*) ok "清单读得出来：v${SHOP_VERSION}、只收元宝" ;;
    *) bad "清单头部不对：$head1" ;;
  esac
  rows=$(grep -c '^shop_item=' "$TMP/list.txt" || true)
  [ "$rows" -ge 18 ] && ok "清单商品不少于 18 条（实际 ${rows}）" \
    || bad "清单商品条数少于 18：$rows"
  # 每条都要有 mode / limit / stock / on_sale 四段（这是「带字段」的可核对形态）。
  if [ "$(grep -cE '^shop_item=.* mode=(none|item|season_pass|monthly_bundle|second_slot) .* limit=[0-9]+/[0-9]+ stock=(unlimited|daily|weekly).* on_sale=(true|false) ' "$TMP/list.txt")" = "$rows" ]; then
    ok "每条都带 mode / limit / stock / on_sale"
  else
    bad "有条目缺字段：$(grep -vE ' mode=.* limit=.* stock=.* on_sale=' "$TMP/list.txt" | head -2 | tr '\n' ' ')"
  fi
  [ "$(grep -c ' mode=item ' "$TMP/list.txt")" -ge 1 ] && ok "item 发货的商品照旧带货" \
    || bad "没有一条 item 发货"
else
  bad "清单读取失败"; tail -5 "$TMP/list.txt" | detail
fi

echo "== 4. 坏配置都起不来 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
base = json.load(open(os.path.join(root, "config/shop.json"), encoding="utf-8"))

def item(j, cat):
    return next(i for i in j["items"] if i["category"] == cat)

cases = {
    # category 不在 categories 里
    "category_unknown": lambda j: item(j, "outfit").__setitem__("category", "unknown_kind"),
    # 上了方案写定绝不上架的种类
    "category_forbidden": lambda j: item(j, "outfit").__setitem__("category", "gold"),
    # delivery.mode 不认识
    "mode_unknown": lambda j: item(j, "red_talisman")["delivery"].__setitem__("mode", "gift"),
    # item 发货没有发货物
    "mode_item_no_goods": lambda j: item(j, "red_talisman")["delivery"].pop("item_id"),
    # 币种不在 currency.json
    "currency_unknown": lambda j: j.__setitem__("currency", "silver"),
    # stock.rule 不在三种里
    "stock_rule_unknown": lambda j: item(j, "outfit")["stock"].__setitem__("rule", "monthly"),
    # 白名单与黑名单打架
    "category_overlap": lambda j: j["forbidden"].append("outfit"),
}
for name, mutate in cases.items():
    doc = copy.deepcopy(base)
    mutate(doc)
    with open(os.path.join(tmp, "bad-%s.json" % name), "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False)
PY

for case in category_unknown category_forbidden mode_unknown mode_item_no_goods \
            currency_unknown stock_rule_unknown category_overlap; do
  out="$("$DOTNET" "$CHARACTER_DLL" --shop-list --shop-config "$TMP/bad-$case.json" 2>&1)"
  assert_bad_config "$out" "坏清单 ${case}：起不来（$(printf '%s' "$out" | head -1 | cut -c1-52)…）"
done

echo "== 5. 加一件商品只改配置 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
d = json.load(open(os.path.join(root, "config/shop.json"), encoding="utf-8"))
d["items"].append({
    "id": "shop_k1_probe_1", "name": "核对用临时商品", "category": "outfit",
    "price": 1, "currency": "yuanbao",
    "delivery": {"mode": "none"},
    "limit": {"per_character": 0, "per_day": 0},
    "stock": {"rule": "unlimited"}, "on_sale": True, "note": "核对用，不进仓库"})
json.dump(d, open(os.path.join(tmp, "added.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
ADDED="$("$DOTNET" "$CHARACTER_DLL" --shop-list --shop-config "$TMP/added.json" 2>&1)"
# 用 herestring 而不是管道：19 行输出走管道时 grep -q 提前退出会让 printf 吃 SIGPIPE，
# 在 set -o pipefail 下整条判断变假（K1 自己踩到的坑）。
if grep -q 'shop_item=shop_k1_probe_1 .*mode=none.*delivery=pending ' <<< "$ADDED"; then
  ok "新加一条商品：只改配置就出现在清单里，且按 none 回 delivery_pending"
else
  bad "新加商品没生效：$(printf '%s' "$ADDED" | grep shop_k1_probe_1 | head -1)"
fi
if grep -q "items=$(python3 -c 'import json;print(len(json.load(open("config/shop.json"))["items"])+1)')" <<< "$(head -1 <<< "$ADDED")"; then
  ok "条数跟着配置走（配置条数 + 1）"
else
  bad "条数没跟着配置走：$(printf '%s' "$ADDED" | head -1)"
fi

echo "== 6. K2+Q13 NPC 商店：配置驱动 + 禁售清单 + 加一件商品只改配置 =="
ROOT="$ROOT" python3 - > "$TMP/npc.txt" 2>&1 <<'PY'
import json, os

root = os.environ["ROOT"]

def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def cfg(p):
    return json.loads(read(p))

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

npc = cfg("config/npc_shop.json")
items = cfg("config/items.json")["items"]
by_id = {i["id"]: i for i in items}
npc_doc = cfg("config/npc.json")["npcs"]
pills = {p["item_id"]: p for p in cfg("config/pill.json")["pills"]}
migrations = cfg("db/migrations.json")

report("npc_shop_schema", npc.get("schema") == "npc-shop-table/2" and npc.get("version", 0) >= 2
       and npc.get("pending") is False,
       "schema=%s version=%s pending=%s" % (npc.get("schema"), npc.get("version"), npc.get("pending")))

shops = npc.get("shops") or []
report("npc_shop_two_shops", [s["id"] for s in shops] == ["npc_general_qiyun", "npc_sect_supply"],
       "shops=%s" % ",".join(s["id"] for s in shops))

# npc.json 的 shop_id 必须指回来：两张表任意一边改了都起不来。
shop_of_npc = {n["id"]: n.get("shop_id") or "" for n in npc_doc}
report("npc_shop_npc_refs", all(shop_of_npc.get(s["npc_id"]) == s["id"] for s in shops),
       "npc.json=%s" % {s["npc_id"]: shop_of_npc.get(s["npc_id"]) for s in shops})

shelf = set(npc.get("shelf_kinds") or [])
forbidden = set(npc.get("forbidden_items") or [])
goods = [(s["id"], g) for s in shops for g in (s.get("items") or [])]
report("npc_shop_shelf_kinds", shelf == {"consumable", "tool", "bag"}, "shelf=%s" % ",".join(sorted(shelf)))
report("npc_shop_no_forbidden_kind",
       not [g for _, g in goods if by_id.get(g["item_id"], {}).get("kind") in ("equipment", "material", "crystal")],
       "货架上出现了装备 / 材料 / 结晶")
report("npc_shop_no_forbidden_item",
       not [g for _, g in goods if g["item_id"] in forbidden],
       "货架上出现了禁售清单里的物品")
report("npc_shop_prices_positive",
       all(isinstance(g["price"], int) and g["price"] > 0 for _, g in goods)
       and all(isinstance(g.get("stock_per_day", 0), int) and g.get("stock_per_day", 0) >= 0 for _, g in goods)
       and all(isinstance(g.get("limit_per_character", 0), int) and g.get("limit_per_character", 0) >= 0
               for _, g in goods),
       "价格 / 库存 / 限购有非正数")

# 方案 12：宗门卖「基础药品」，不是丹。回春丹 / 凝神丹 / 各档丹都不在货架上。
report("npc_shop_sect_potions",
       sorted(g["item_id"] for s in shops if s["id"] == "npc_sect_supply" for g in s["items"])
       == ["potion_hp_basic", "potion_mp_basic"],
       "宗门店=%s" % [g["item_id"] for s in shops if s["id"] == "npc_sect_supply" for g in s["items"]])
hp = pills.get("potion_hp_basic") or {}
mp = pills.get("potion_mp_basic") or {}
low = [p["restore"] for p in pills.values() if p["kind"] == "hp" and p["item_id"] != "potion_hp_basic"]
low_mp = [p["restore"] for p in pills.values() if p["kind"] == "mana" and p["item_id"] != "potion_mp_basic"]
report("npc_potion_weaker_than_pill",
       hp.get("restore", 0) > 0 and mp.get("restore", 0) > 0
       and hp["restore"] < min(low) and mp["restore"] < min(low_mp)
       and hp.get("cooldown_group") == "heal" and mp.get("cooldown_group") == "mana",
       "hp=%s mp=%s 最低丹=%s/%s" % (hp.get("restore"), mp.get("restore"), min(low), min(low_mp)))

sell = npc.get("sell") or {}
sell_items = [x["item_id"] for x in (sell.get("items") or [])]
report("npc_sell_material_only",
       sell_items and all(by_id.get(i, {}).get("kind") == "material" for i in sell_items),
       "sell=%s" % ",".join(sell_items))
report("npc_sell_prices", all(isinstance(x["price"], int) and x["price"] >= 0 for x in sell.get("items") or []),
       "回收价有负数")

bb = npc.get("buyback") or {}
report("npc_buyback_window", 1 <= bb.get("keep", 0) <= 20 and 1 <= bb.get("price_pct", 0) <= 100
       and 1 <= bb.get("expire_days", 0) <= 365, "buyback=%s" % bb)

repair = npc.get("repair") or {}
grades = {i.get("grade") for i in items if i.get("kind") == "equipment"}
missing = sorted(g for g in grades if g not in (repair.get("durability_max") or {}))
report("npc_repair_covers_grades", not missing and repair.get("price_per_point", 0) > 0,
       "缺品阶=%s" % missing)

# 价格只在配置：这两份 C# 里不许出现具体价 / 耐久数字。
table_cs = read("src/Character/NpcShopTable.cs")
repo_cs = read("src/Character/NpcShopRepository.cs")
report("npc_shop_no_price_in_code",
       not any(tok in table_cs + repo_cs for tok in ("price = 10", "price = 5", "durability = 60", "= 60;")),
       "代码里出现了具体价 / 耐久数字")

# 接线：迁移 31、DDL、proto、网关、probe 五处都在。
chars = [e for g in migrations["schemas"] if g["key"] == "character" for e in g["entries"]]
report("npc_shop_migration", chars[-1]["version"] == 31 and chars[-1]["file"] == "db/character/npc_shop.sql",
       "最后一版=%s" % chars[-1])
master = read("db/character/character.sql")
ddl = read("db/character/npc_shop.sql")
report("npc_shop_ddl",
       all(t in ddl for t in ('npc_shop_day', 'npc_shop_purchase', 'npc_shop_buyback', 'durability'))
       and all(t in master for t in ('npc_shop_day', 'npc_shop_purchase', 'npc_shop_buyback', 'durability')),
       "DDL 缺表 / 缺列，或主迁移没追加同一份")
report("npc_shop_proto",
       all(t in read("proto/mmo/character.proto") for t in (
           "rpc DescribeNpcShop", "rpc BuyNpcShopItem", "rpc SellNpcItem",
           "rpc BuybackNpcItem", "rpc RepairNpcEquipment")),
       "proto 缺 rpc")
report("npc_shop_gateway",
       all(t in read("src/Gateway/ShopApi.cs") for t in (
           '"/shop/npc"', '"/shop/npc/buy"', '"/shop/npc/sell"',
           '"/shop/npc/buyback"', '"/shop/npc/repair"')),
       "网关缺路由")
report("npc_shop_probe",
       "NpcShopCli.TryReadOnly()" in read("tools/probe/CliProbe/CliEntry.cs")
       and "NpcShopCli.TryRunDbAsync" in read("tools/probe/CliProbe/DbCli.cs"),
       "probe 分发缺一处")
# 钱与货的先后：买/回购必须「先抢号 → 先扣币 → 再入包」，修理必须先扣币再写耐久。
# 这几条不看运行，只看调用顺序，所以「不扣币就发货」这种改法在本地无库段就会红。
def order(body, first, second):
    a, b = body.find(first), body.find(second)
    return a >= 0 and b >= 0 and a < b

buy_body = repo_cs.split("public async Task<NpcBuyFacts> BuyAsync")[1].split("public async Task<NpcSellFacts>")[0]
sell_body = repo_cs.split("public async Task<NpcSellFacts> SellAsync")[1].split("public async Task<NpcBuybackFacts>")[0]
back_body = repo_cs.split("public async Task<NpcBuybackFacts> BuybackAsync")[1].split("public async Task<NpcRepairFacts>")[0]
repair_body = repo_cs.split("public async Task<NpcRepairFacts> RepairAsync")[1].split("// ------------------------------------------------------------------ 内部")[0]
report("npc_buy_claim_charge_deliver",
       order(buy_body, "TryClaimTxnInTransactionAsync", "ApplyCurrencyDeltaInTransactionAsync")
       and order(buy_body, "ApplyCurrencyDeltaInTransactionAsync", "ApplyItemDeltasInTransactionAsync"),
       "买的顺序不是 抢号 → 扣币 → 入包")
report("npc_sell_remove_then_gain",
       order(sell_body, "delete from", "ApplyCurrencyDeltaInTransactionAsync")
       and "npc_shop_buyback" in sell_body,
       "卖的顺序不是 删行 → 加币 → 记回购")
report("npc_buyback_charge_then_deliver",
       order(back_body, "TryClaimTxnInTransactionAsync", "ApplyCurrencyDeltaInTransactionAsync")
       and order(back_body, "ApplyCurrencyDeltaInTransactionAsync", "ApplyItemDeltasInTransactionAsync"),
       "回购的顺序不是 抢号 → 扣币 → 入包")
report("npc_repair_charge_then_write",
       order(repair_body, "ApplyCurrencyDeltaInTransactionAsync", "set durability = null"),
       "修理不是先扣币再写耐久")
report("npc_shop_client",
       "NpcShopCodex" in read("client/MMOGame/Assets/HotUpdate/Ui/UiContentTable.cs")
       and "NpcShops" in read("client/MMOGame/Assets/HotUpdate/Ui/UiContentTable.cs"),
       "客户端入口表没生成")
PY
while IFS= read -r line; do
  case "$line" in
    PASS*) ok "${line#PASS }" ;;
    FAIL*) bad "${line#FAIL }" ;;
  esac
done < "$TMP/npc.txt"

NPCLIST="$("$DOTNET" "$CHARACTER_DLL" --npc-shop-list 2>&1)"
if grep -q 'npc_shop_table=ok version=2 shops=2' <<< "$NPCLIST"; then
  ok "只读入口：两店都读出来了"
else
  bad "只读入口没读出两店：$(head -1 <<< "$NPCLIST")"
fi
if [ "$(grep -c '^npc_shop_item ' <<< "$NPCLIST")" = "$(python3 -c 'import json;d=json.load(open("config/npc_shop.json"));print(sum(len(s["items"]) for s in d["shops"]))')" ]; then
  ok "货架件数跟着配置走"
else
  bad "货架件数对不上：$(grep -c '^npc_shop_item ' <<< "$NPCLIST")"
fi
if grep -q 'npc_shop_durability grade=red max=' <<< "$NPCLIST"; then
  ok "修理单价与六档耐久上限都在配置里读出来"
else
  bad "耐久上限没读全"
fi

# 坏配置三例：禁售物品 / 装备 / 丹上了货架，都必须起不来（退出 2，config_error）。
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
base = json.load(open(os.path.join(root, "config/npc_shop.json"), encoding="utf-8"))

def dump(name, mutate):
    d = json.loads(json.dumps(base))
    mutate(d)
    json.dump(d, open(os.path.join(tmp, name), "w", encoding="utf-8"), ensure_ascii=False)

dump("bad-forbidden.json", lambda d: d["shops"][1]["items"].append(
    {"item_id": "pill_rejuvenate", "price": 1, "stock_per_day": 1, "limit_per_character": 0}))
dump("bad-equip.json", lambda d: d["shops"][0]["items"].append(
    {"item_id": "sword_trial", "price": 1, "stock_per_day": 1, "limit_per_character": 0}))
dump("bad-sell.json", lambda d: d["sell"]["items"].append(
    {"item_id": "sword_trial", "price": 1}))
PY
for case in forbidden equip sell; do
  out="$("$DOTNET" "$CHARACTER_DLL" --npc-shop-list --npc-shop-table-path "$TMP/bad-$case.json" 2>&1)"
  assert_bad_config "$out" "坏货架 ${case}：起不来（$(printf '%s' "$out" | head -1 | cut -c1-56)…）"
done

# 加一件商品只改配置：类别合法的一件新工具，出现在只读入口里，代码一行不动。
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
d = json.load(open(os.path.join(root, "config/npc_shop.json"), encoding="utf-8"))
d["shops"][0]["items"].append(
    {"item_id": "rename_card", "price": 7, "stock_per_day": 2, "limit_per_character": 1})
json.dump(d, open(os.path.join(tmp, "added-npc.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
ADDED_NPC="$("$DOTNET" "$CHARACTER_DLL" --npc-shop-list --npc-shop-table-path "$TMP/added-npc.json" 2>&1)"
if grep -q 'npc_shop_item shop=npc_general_qiyun item=rename_card .*price=7' <<< "$ADDED_NPC"; then
  ok "加一件商品：只改配置就进货架，代码不动"
else
  bad "新加商品没生效：$(grep rename_card <<< "$ADDED_NPC" | head -1)"
fi

finish "$TITLE"
