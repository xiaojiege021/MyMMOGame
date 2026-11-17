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
             "protect_ticket"]
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
report("shop_schema_version",
       shop.get("schema") == "shop-table/1" and shop.get("version") == 11,
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
if "$DOTNET" "$CHARACTER_DLL" --shop-list > "$TMP/list.txt" 2>&1; then
  head1="$(head -1 "$TMP/list.txt")"
  case "$head1" in
    "shop_list_state=ok version=11 "*) ok "清单读得出来：v11、只收元宝" ;;
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
if grep -q 'items=19' <<< "$(head -1 <<< "$ADDED")"; then
  ok "条数跟着配置走（18 → 19）"
else
  bad "条数没跟着配置走：$(printf '%s' "$ADDED" | head -1)"
fi

finish "$TITLE"
