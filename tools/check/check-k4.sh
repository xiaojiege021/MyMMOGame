#!/usr/bin/env bash
# tools/check/check-k4.sh —— K4 实物引用审计（开发架构 14.7 顺序 2）。
#
# 完成标准（14.7 的 K4 行，逐条对应下面的断言）：
#   1. 配置里 79 处物品引用 100% 存在；
#   2. 代码 grep 不到物品编号 / kind 字面量；
#   3. check 脚本进 CI；
#   4. 新增物品只改配置。
#
# 口径（写死在断言里，后面加卡不要放松）：
#   - 「引用」= config/**/*.json 里所有 item_id / stone_id / protect_item_id /
#     stone_item_id 字段的值，加 crystal.json 的 equipment_parts 键（物品编号 → 部位）。
#     新增一种引用字段名要在下面 REF_KEYS 里登记，否则 static 段会报「没登记的引用键」，
#     不让它悄悄绕过审计。
#   - 「代码」= 服务端运行时代码（src/**，不含 *.Tests、bin、obj）、proto/mmo/*.proto、
#     db/**/*.sql。核对 / 自检代码（tools/probe、client/**/Editor、单测）与客户端显示 /
#     请求参数表不算经济内容：前者必须用具体编号才能核对，后者的编号只当请求参数、
#     服务端仍按配置校验（客户端文案与编号归属 D9 / K6）。
#   - 物品编号与商城门类同名时（teleport_talisman / exp_booster 本来就是门类名），
#     代码里出现的是门类，不是第二份物品编号；限定它只出现在校验器里。
#
#   bash tools/check/check-k4.sh
#   MMO_K4_SKIP_BUILD=1 bash tools/check/check-k4.sh   跳过编译
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CHARACTER_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TMP="$(mktemp -d)"
TITLE="K4（server）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_K4_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_K4_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/Character/Character.csproj -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "角色服编译通过"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 配置引用审计（静态） =="
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

# 14.7 的 K4 行只写了「物品引用」；这五种键就是当前配置里全部引用方式。
REF_KEYS = {"item_id", "stone_id", "protect_item_id", "stone_item_id"}

# 配置设计 2 节的物品种类，加上通行证已经在用的 appearance_fragment / outfit。
KINDS = {
    "material", "consumable", "equipment", "crystal", "voucher", "title",
    "appearance", "appearance_fragment", "outfit", "home_decor", "tool", "bag",
}
BINDS = {"none", "on_pickup", "always"}

items_doc = cfg("config/items.json")
items = items_doc.get("items") or []
ids = [(x.get("id") or "").strip() for x in items]
idset = set(ids)

# ---------------------------------------------------------------- 1. 物品表本身
dups = sorted({i for i in ids if ids.count(i) > 1})
report("items_id_unique", not dups and len(ids) == len(idset), "重复=%s" % ",".join(dups))
report("items_id_format", all(re.fullmatch(r"[a-z][a-z0-9_]*", i) for i in ids),
       "不是小写+下划线的=%s" % ",".join(i for i in ids if not re.fullmatch(r"[a-z][a-z0-9_]*", i)))
report("items_kind_known", all(x.get("kind") in KINDS for x in items),
       "未知种类=%s" % ",".join("%s:%s" % (x.get("id"), x.get("kind")) for x in items
                               if x.get("kind") not in KINDS))
report("items_bind_known", all(x.get("bind") in BINDS for x in items),
       "未知绑定=%s" % ",".join("%s:%s" % (x.get("id"), x.get("bind")) for x in items
                               if x.get("bind") not in BINDS))

def lack(x):
    bad = [k for k in ("id", "name", "kind", "stack_max", "bind") if x.get(k) in (None, "")]
    if not isinstance(x.get("stack_max"), int) or (x.get("stack_max") or 0) < 1:
        bad.append("stack_max")
    return bad

bad_fields = ["%s:%s" % (x.get("id"), ",".join(lack(x))) for x in items if lack(x)]
report("items_required_fields", not bad_fields, ";".join(bad_fields))
report("items_count_floor", len(ids) >= 33, "只有 %d 件" % len(ids))

# 价格 / 币种 / 发货 / 限购 / 掉落只许在商店、配方、掉落表：物品表里出现就是第二份。
ECONOMY_KEYS = {"price", "currency", "delivery", "limit", "stock", "drop", "drops", "reward"}
bad_econ = [x.get("id") + ":" + k for x in items for k in ECONOMY_KEYS if k in x]
report("items_no_economy_fields", not bad_econ, ";".join(bad_econ[:8]))

# ---------------------------------------------------------------- 2. 所有引用
refs = []      # (文件, 路径, 编号)
equip = []     # crystal.json 的 equipment_parts：（文件, 路径, 编号）
prods = []     # 商品编号引用（season_pass / monthly_bundle 的 product_id）
unknown = []   # 没登记的 *_item_id 键：新引用字段必须在这里登记，否则审计漏掉

for dirpath, _, filenames in os.walk(os.path.join(root, "config")):
    for name in sorted(filenames):
        if not name.endswith(".json"):
            continue
        path = os.path.join(dirpath, name)
        rel = os.path.relpath(path, root)
        try:
            doc = json.load(open(path, encoding="utf-8"))
        except Exception as ex:
            report("config_json_%s" % rel, False, str(ex))
            continue

        def walk(node, where):
            if isinstance(node, dict):
                for key, value in node.items():
                    here = where + "." + key
                    if key in REF_KEYS and isinstance(value, str):
                        refs.append((rel, here, value))
                    elif key.endswith("_item_id") and isinstance(value, str) and key not in REF_KEYS:
                        unknown.append((rel, here, value))
                    elif key == "product_id" and isinstance(value, str):
                        prods.append((rel, here, value))
                    elif key == "equipment_parts" and isinstance(value, dict):
                        for item_id in value:
                            equip.append((rel, here + "." + item_id, item_id))
                    walk(value, here)
            elif isinstance(node, list):
                for index, value in enumerate(node):
                    walk(value, where + "[%d]" % index)

        walk(doc, "")

total = len(refs) + len(equip)
missing = [(f, p, v) for f, p, v in refs + equip if v not in idset]
report("refs_count_floor", total >= 79, "只扫到 %d 处引用（14.7 写的是 79）" % total)
report("refs_all_exist", not missing,
       ";".join("%s:%s->%s" % (f, p, v) for f, p, v in missing[:8]))
report("ref_keys_registered", not unknown,
       "没登记的引用键=%s" % ";".join("%s:%s" % (f, p) for f, p, _ in unknown[:8]))

shop_ids = {(x.get("id") or "") for x in cfg("config/shop.json").get("items") or []}
bad_prod = [(f, p, v) for f, p, v in prods if v not in shop_ids]
report("product_refs_exist", not bad_prod,
       ";".join("%s:%s->%s" % x for x in bad_prod[:8]))

# 引用到的物品种类要跟引用位置对得上（编号存在还不够）。
item_kind = {x["id"]: x.get("kind") for x in items}
bad_part = [v for _, _, v in equip if item_kind.get(v) != "equipment"]
report("crystal_equipment_parts_are_equipment", not bad_part, ",".join(bad_part))

crystal = cfg("config/crystal.json")
bad_crystal = [c.get("id") for c in crystal.get("crystals") or []
               if item_kind.get(c.get("id")) != "crystal"]
report("crystal_items_are_crystal", not bad_crystal, ",".join(bad_crystal))

# 商城发货：mode=item 的每一条都要指向真物品；品种还必须是该门类该发的种类。
SHOP_CONSUMABLE = {"red_talisman", "blue_talisman", "revive_scroll", "exp_booster",
                   "unbind_item", "teleport_talisman"}
bad_delivery = []
for row in cfg("config/shop.json").get("items") or []:
    mode = (row.get("delivery") or {}).get("mode")
    if mode != "item":
        continue
    item_id = (row.get("delivery") or {}).get("item_id")
    kind = item_kind.get(item_id)
    want = "voucher" if row.get("category") == "protect_ticket" else (
        "consumable" if row.get("category") in SHOP_CONSUMABLE else None)
    if kind is None or (want is not None and kind != want):
        bad_delivery.append("%s:%s->%s(%s)" % (row.get("id"), row.get("category"), item_id, kind))
report("shop_delivery_refs_typed", not bad_delivery, ";".join(bad_delivery))

# 通行证：奖励种类 → 物品种类（方案 12.1 的四种奖励）。
PASS_KIND = {"title": "title", "appearance_fragment": "appearance_fragment",
             "season_outfit": "outfit", "home_decor": "home_decor",
             "protect_ticket": "voucher"}
bad_reward = []
for group in cfg("config/season_pass.json").get("seasons") or []:
    for track in ("free", "paid"):
        for step in group.get(track) or []:
            for reward in step.get("rewards") or []:
                want = PASS_KIND.get(reward.get("kind"))
                if want is None or item_kind.get(reward.get("item_id")) != want:
                    bad_reward.append("%s/%s/%s:%s" % (group.get("season"), track,
                                                       reward.get("kind"), reward.get("item_id")))
report("season_reward_refs_typed", not bad_reward, ";".join(bad_reward[:8]))

# ---------------------------------------------------------------- 3. 代码里没有第二份
def literals(text):
    """取 C# / proto / SQL 的字符串字面量，跳过注释。"""
    out, i, n = [], 0, len(text)
    in_line = in_block = False
    while i < n:
        c = text[i]
        if in_line:
            if c == "\n":
                in_line = False
            i += 1
            continue
        if in_block:
            if c == "*" and i + 1 < n and text[i + 1] == "/":
                in_block = False
                i += 2
                continue
            i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            in_line = True
            i += 2
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            in_block = True
            i += 2
            continue
        if c == '"':
            j, buf = i + 1, []
            while j < n:
                if text[j] == "\\":
                    buf.append(text[j:j + 2])
                    j += 2
                    continue
                if text[j] == '"':
                    break
                buf.append(text[j])
                j += 1
            out.append("".join(buf))
            i = j + 1
            continue
        i += 1
    return out

shop_cats = set(cfg("config/shop.json").get("categories") or [])
home_cats = set(cfg("config/home_coin_shop.json").get("categories") or [])
categories = shop_cats | home_cats

files = []
for base in ("src", "proto", "db"):
    for dirpath, _, filenames in os.walk(os.path.join(root, base)):
        if "/bin" in dirpath or "/obj" in dirpath or ".Tests" in dirpath:
            continue
        for name in filenames:
            if name.endswith((".cs", ".proto", ".sql")):
                files.append(os.path.join(dirpath, name))

item_hits, category_hits = [], []
for path in files:
    rel = os.path.relpath(path, root)
    body = read(rel)
    for value in literals(body):
        if value not in idset:
            continue
        if value in categories:
            category_hits.append((rel, value))
        else:
            item_hits.append((rel, value))

report("code_no_item_id_literals", not item_hits,
       ";".join("%s:%s" % x for x in item_hits[:8]))

# 与门类同名的编号只许留在校验器里：业务服务（购买 / 发货 / 制造）里出现就是分支。
service = read("src/Character/CharacterGrpcService.cs")
bad_service = [c for c in categories if '"%s"' % c in service]
report("code_service_no_category_literals", not bad_service, ",".join(bad_service))

# 门类字面量参与 if / switch 的地方只许在加载 / 校验表（*Table.cs）里：
# 业务服务、仓储、进程一旦按门类分支，就是把「发什么」又从配置搬回代码。
branch_sites = []
for path in files:
    if not path.endswith(".cs"):
        continue
    rel = os.path.relpath(path, root)
    for line in read(rel).splitlines():
        if line.strip().startswith("//"):
            continue
        for name in categories:
            if re.search(r'(case\s+"%s"|Kind\s*==\s*"%s"|string\.Equals\([^,]+,\s*"%s")'
                         % (name, name, name), line):
                branch_sites.append(rel + ":" + name)
bad_sites = [x for x in branch_sites if not x.split(":")[0].endswith("Table.cs")]
report("code_category_checks_only_in_tables", not bad_sites,
       ";".join(bad_sites[:8]))

shop_cs = read("src/Character/ShopTable.cs")
home_cs = read("src/Character/HomeCoinShopTable.cs")
code_lines = [l for f in (shop_cs, home_cs) for l in f.splitlines()
              if not l.strip().startswith("//")]
report("code_no_shop_kind_tables",
       not any(re.search(r"string\[\]\s+(AllowedKinds|DeliveredKinds|ForbiddenKinds)", l)
               for l in code_lines),
       "商城 / 家园币商城又出现了种类硬编码表")

begin = service.find("public override async Task<BuyShopItemResponse> BuyShopItem(")
end = service.find("public override async Task<BuyHomeCoinShopResponse> BuyHomeCoinShopItem(", begin)
buy = service[begin:end] if begin >= 0 and end > begin else ""
report("buy_body_found", len(buy) > 2000, "len=%d" % len(buy))
report("buy_dispatch_by_mode", "switch (item.Mode)" in buy, "购买没有按 delivery.mode 分发")
report("buy_no_kind_branches",
       not any(('item.Kind == "%s"' % c) in buy or ('string.Equals(item.Kind, "%s"' % c) in buy
               for c in categories),
       "购买还在按门类字符串分支")

report("code_no_stone_ban_table",
       "stone_1" not in shop_cs and "stone_1" not in home_cs,
       "商城又出现 stone_1..5 禁售表")

report("itemtable_duplicate_guard", "物品编号重复" in read("src/Character/ItemTable.cs"),
       "ItemTable 还会让重复编号后者覆盖前者")

ci = read("tools/check/ci.sh")
report("ci_registers_k4", re.search(r'^CHECKS="[\s\S]*\bk4\b', ci, re.M) is not None,
       "ci.sh 的 CHECKS 里没有 k4")

# 核对 / 显示层的具体编号不算经济内容，只报个数，不判红。
display_hits = set()
for base in ("client/MMOGame/Assets/HotUpdate", "client/MMOGame/Assets/Editor",
             "tools/probe", "src/Shared.Domain.Tests"):
    for dirpath, _, filenames in os.walk(os.path.join(root, base)):
        if "/bin" in dirpath or "/obj" in dirpath:
            continue
        for name in filenames:
            if not name.endswith((".cs", ".py", ".sh")):
                continue
            path = os.path.join(dirpath, name)
            for value in literals(open(path, encoding="utf-8", errors="ignore").read()):
                if value in idset:
                    display_hits.add(os.path.relpath(path, root))
print("INFO category_name_literals=%d（与物品编号同名的门类，只在校验器里）" % len(category_hits))
print("INFO display_or_check_item_id_files=%d（客户端显示 / 请求参数、probe、单测；归 D9 / K6）"
      % len(display_hits))
print("INFO refs=%d (item=%d, equipment_parts=%d) items=%d"
      % (total, len(refs), len(equip), len(ids)))
print("PASS static_end")
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
    INFO) printf '  ..   %s\n' "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static.txt"
if ! grep -qE '^(PASS|FAIL) ' "$TMP/static.txt"; then
  bad "静态断言没跑起来"; tail -12 "$TMP/static.txt" | detail
elif ! grep -q '^PASS static_end' "$TMP/static.txt"; then
  bad "静态断言没跑完"; grep -vE '^(PASS|FAIL|INFO) ' "$TMP/static.txt" | tail -6 | detail
fi

echo "== 3. 真配置整链加载 =="
# 角色服启动先把所有表按真实 Load 走一遍；没有库时打完配置就 db_missing=1 退出 3。
# 这一段验的就是「真启动能读通」，不是静态扫一遍。
STARTUP="$TMP/startup.log"
"$DOTNET" "$CHARACTER_DLL" --config config/character.json > "$STARTUP" 2>&1 &
START_PID=$!
for _ in $(seq 1 300); do
  grep -qE '^config_error=|^db_missing=1|Now listening on' "$STARTUP" 2>/dev/null && break
  sleep 0.1
done
stop_pid "$START_PID"

if grep -q '^config_error=' "$STARTUP"; then
  bad "真配置整链加载失败：$(head -1 "$STARTUP")"
  grep -n 'config_error=' "$STARTUP" | head -3 | detail
elif grep -q '^items=' "$STARTUP"; then
  # 有库的机器会继续起监听；没有库的打 db_missing=1 退 3。两边的共同点是「表都读完了」。
  ok "真配置整链加载通过（$(grep -cE '^[a-z_]+=' "$STARTUP") 行配置输出，无 config_error）"
else
  bad "真配置没走到加载结果"; tail -8 "$STARTUP" | detail
fi

ITEM_COUNT="$(python3 -c 'import json; print(len(json.load(open("config/items.json"))["items"]))')"
grep -q "^items=${ITEM_COUNT} " "$STARTUP" \
  && ok "物品表 ${ITEM_COUNT} 件被启动读到" \
  || bad "启动输出的物品数不是 ${ITEM_COUNT}：$(grep '^items=' "$STARTUP" | head -1)"
for line in "shop_table_version=" "season_pass=" "monthly_bundle=" \
            "crystal_table_version=" "dungeon_drops=ok" "mainline_drops=ok"; do
  grep -q "^${line}" "$STARTUP" && ok "启动读到 ${line%%=*}" \
    || bad "启动没读到 ${line%%=*}"
done

echo "== 4. 坏引用都起不来 =="
# 每个用例一份完整配置副本，只改一处引用；只读入口必须打 config_error 且非 0。
newcase() {
  local name="$1"
  mkdir -p "$TMP/bad/$name"
  cp -R config "$TMP/bad/$name/config"
  printf '%s' "$TMP/bad/$name"
}

CASE="$(newcase missing_shop_item)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/items.json")
d = json.load(open(p, encoding="utf-8"))
d["items"] = [x for x in d["items"] if x["id"] != "talisman_red"]
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
out="$( (cd "$CASE" && "$DOTNET" "$CHARACTER_DLL" --shop-list) 2>&1 )"
assert_bad_config "$out" "商城发货物品被删：起不来（$(head -1 <<< "$out" | cut -c1-56)…）"

CASE="$(newcase missing_drop_item)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/dungeon_drops.json")
d = json.load(open(p, encoding="utf-8"))
d["entries"][0]["lines"][0]["item_id"] = "no_such_item"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
out="$( (cd "$CASE" && "$DOTNET" "$CHARACTER_DLL" --dungeon-drop-table-path config/dungeon_drops.json) 2>&1 )"
assert_bad_config "$out" "掉落表外的物品：起不来（$(head -1 <<< "$out" | cut -c1-56)…）"

CASE="$(newcase missing_season_reward)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/season_pass.json")
d = json.load(open(p, encoding="utf-8"))
d["seasons"][0]["free"][0]["rewards"][0]["item_id"] = "no_such_item"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
out="$( (cd "$CASE" && "$DOTNET" "$CHARACTER_DLL" --season-pass-list) 2>&1 )"
assert_bad_config "$out" "通行证奖励物品被删：起不来（$(head -1 <<< "$out" | cut -c1-56)…）"

CASE="$(newcase missing_enhance_stone)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/enhance.json")
d = json.load(open(p, encoding="utf-8"))
d["tiers"][0]["stone_id"] = "no_such_item"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
out="$( (cd "$CASE" && "$DOTNET" "$CHARACTER_DLL" --shop-list) 2>&1 )"
assert_bad_config "$out" "强化石被删：起不来（$(head -1 <<< "$out" | cut -c1-56)…）"

CASE="$(newcase missing_monthly_item)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/monthly_bundle.json")
d = json.load(open(p, encoding="utf-8"))
d["items"][0]["item_id"] = "no_such_item"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
out="$( (cd "$CASE" && "$DOTNET" "$CHARACTER_DLL" --monthly-bundle-list) 2>&1 )"
assert_bad_config "$out" "月叠物品被删：起不来（$(head -1 <<< "$out" | cut -c1-56)…）"

CASE="$(newcase duplicate_item_id)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/items.json")
d = json.load(open(p, encoding="utf-8"))
d["items"].append(dict(d["items"][0]))
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
out="$( (cd "$CASE" && "$DOTNET" "$CHARACTER_DLL" --shop-list) 2>&1 )"
assert_bad_config "$out" "物品编号重复：起不来（$(head -1 <<< "$out" | cut -c1-56)…）"

echo "== 5. 新增物品只改配置 =="
# 不改一行代码：物品表加一件、商城加一条、掉落表加一行，只读入口就能读到。
CASE="$(newcase add_new_item)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
base = os.path.join(os.environ["CASE"], "config")

items = json.load(open(os.path.join(base, "items.json"), encoding="utf-8"))
items["items"].append({"id": "k4_probe_talisman", "name": "核对用新符",
                       "kind": "consumable", "stack_max": 20, "bind": "always"})
json.dump(items, open(os.path.join(base, "items.json"), "w", encoding="utf-8"), ensure_ascii=False)

shop = json.load(open(os.path.join(base, "shop.json"), encoding="utf-8"))
shop["categories"].append("k4_probe_kind")
shop["items"].append({
    "id": "shop_k4_probe_1", "name": "核对用新商品", "category": "k4_probe_kind",
    "price": 1, "currency": "yuanbao",
    "delivery": {"mode": "item", "item_id": "k4_probe_talisman", "count": 1},
    "limit": {"per_character": 0, "per_day": 0},
    "stock": {"rule": "unlimited"}, "on_sale": True, "note": "核对用，不进仓库"})
json.dump(shop, open(os.path.join(base, "shop.json"), "w", encoding="utf-8"), ensure_ascii=False)

drops = json.load(open(os.path.join(base, "dungeon_drops.json"), encoding="utf-8"))
drops["entries"][0]["lines"].append({"item_id": "k4_probe_talisman", "count": 1})
json.dump(drops, open(os.path.join(base, "dungeon_drops.json"), "w", encoding="utf-8"),
          ensure_ascii=False)
PY

SHOP_COUNT="$(python3 -c 'import json; print(len(json.load(open("config/shop.json"))["items"]))')"
ADDED="$( (cd "$CASE" && "$DOTNET" "$CHARACTER_DLL" --shop-list) 2>&1 )"
if grep -q 'shop_item=shop_k4_probe_1 kind=k4_probe_kind .* delivery=k4_probe_talismanx1 ' <<< "$ADDED"; then
  ok "新物品 + 新商品只改配置就出现在清单里"
else
  bad "新商品没生效：$(grep shop_k4_probe_1 <<< "$ADDED" | head -1)"
fi
if head -1 <<< "$ADDED" | grep -q "items=$((SHOP_COUNT + 1)) "; then
  ok "商品条数跟着配置走（${SHOP_COUNT} → $((SHOP_COUNT + 1))）"
else
  bad "条数没跟着配置走：$(head -1 <<< "$ADDED")"
fi

DROP_ADDED="$( (cd "$CASE" && "$DOTNET" "$CHARACTER_DLL" --dungeon-drop-table-path config/dungeon_drops.json) 2>&1 )"
if grep -q '^dungeon_drop_table_state=ok' <<< "$DROP_ADDED" \
   && grep -q '^dungeon_drop=abyss lines=3' <<< "$DROP_ADDED"; then
  ok "新物品进掉落表也只改配置（abyss 2 → 3 行）"
else
  bad "掉落表没跟着配置走：$(head -3 <<< "$DROP_ADDED" | tr '\n' ' ')"
fi

finish "$TITLE"
