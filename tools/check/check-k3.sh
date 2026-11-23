#!/usr/bin/env bash
# tools/check/check-k3.sh —— K3 生产 / 配方（开发架构 14.7 顺序 7）。
#
# 完成标准（14.7 的 K3 行，逐条对应下面的断言）：
#   1. config/recipes.json 驱动炼丹 → 铁匠 → 裁缝 → 巧匠；
#   2. 材料 / 产出 / 费用 / 成功率 / 时间 / 解锁在配置，代码只读；
#   3. 加一个配方不改代码。
#
#   bash tools/check/check-k3.sh                               服务端段（本机无库也能跑静态与只读）
#   MMO_K3_SKIP_BUILD=1 bash tools/check/check-k3.sh           跳过编译
#   MMO_K3_HAS_DB=1 MMO_K3_CHARACTER_ID=<临时角色号> \
#     bash tools/check/check-k3.sh                             加跑连库段（要有 gamedb；会改这个角色的
#                                                              背包 / 金币 / 等级，只对一次性角色用）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
AGG_CHARGRPC="$(src_agg src/Character/CharacterGrpcService.cs)"; export AGG_CHARGRPC
# Windows 上 dotnet 重定向输出走 GBK：副职名（炼丹 / 铁匠 …）是中文，断言前先按平台转码。
utf8() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      python3 -c 'import sys; sys.stdout.buffer.write(
          sys.stdin.buffer.read().decode("gbk", "replace").encode("utf-8"))' ;;
    *) cat ;;
  esac
}

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TMP="$(mktemp -d)"
TITLE="K3（server）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_K3_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_K3_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/Character/Character.csproj -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "角色服编译通过（顺带编出 probe）"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态：配置 =="
ROOT="$ROOT" python3 - > "$TMP/static.txt" 2>&1 <<'PY'
import json, os, re

root = os.environ["ROOT"]

def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def cfg(p):
    return json.loads(read(p))

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + str(info)))

PROFESSIONS = ["alchemy", "blacksmith", "tailor", "jeweler"]
NAMES = ["炼丹", "铁匠", "裁缝", "巧匠"]
RUNTIME_KEYS = {"owner_sect", "tax_pct", "sold_today", "stock_now", "bought_today",
                "claimed", "current_owner"}

doc = cfg("config/recipes.json")
items_doc = cfg("config/items.json")
items = {x["id"]: x for x in items_doc["items"]}
currencies = {c["id"] for c in cfg("config/currency.json")["currencies"]}
recipes = doc.get("recipes") or []
professions = doc.get("professions") or []

# B1 把炼丹六条换成真实配方，version 从 1 升到 2（schema 不变）；这里只要求
# 「还是那份 schema 且版本不小于 2」，不再钉死 1——钉死会让每次内容定稿都要改老卡。
report("recipe_schema_version",
       doc.get("schema") == "recipe-table/1" and int(doc.get("version", 0)) >= 2,
       "schema=%s version=%s" % (doc.get("schema"), doc.get("version")))
report("recipe_table_pending", doc.get("pending") is True,
       "整表还是占位，pending 必须为 true，表 B 才认得出来")
report("professions_written", [p.get("id") for p in professions] == PROFESSIONS,
       [p.get("id") for p in professions])
report("profession_names_written", [p.get("name") for p in professions] == NAMES,
       [p.get("name") for p in professions])
report("recipes_count_floor", len(recipes) >= 8, "只有 %d 条" % len(recipes))

ids = [r.get("id") or "" for r in recipes]
report("recipe_ids_unique", len(ids) == len(set(ids)),
       ",".join(sorted(i for i in set(ids) if ids.count(i) > 1)))
report("recipe_ids_shape", all(re.fullmatch(r"recipe_[a-z0-9_]+", i) for i in ids),
       ",".join(i for i in ids if not re.fullmatch(r"recipe_[a-z0-9_]+", i)))

by_profession = {}
for r in recipes:
    by_profession.setdefault(r.get("profession"), []).append(r)
report("every_profession_has_recipe",
       all(by_profession.get(p) for p in PROFESSIONS) and set(by_profession) <= set(PROFESSIONS),
       {p: len(by_profession.get(p, [])) for p in PROFESSIONS})

bad = []
for r in recipes:
    rid = r.get("id")
    level = r.get("unlock_level")
    if not isinstance(level, int) or level < 1 or level > 100:
        bad.append(rid + ":unlock")
    ins = r.get("inputs") or []
    if not ins:
        bad.append(rid + ":no_inputs")
    seen = set()
    for i in ins:
        iid = i.get("item_id")
        if iid not in items:
            bad.append(rid + ":input_missing:" + str(iid))
        if not isinstance(i.get("count"), int) or i.get("count") < 1:
            bad.append(rid + ":input_count")
        if iid in seen:
            bad.append(rid + ":input_dup:" + str(iid))
        seen.add(iid)
    fee = r.get("fee") or {}
    if not isinstance(fee, dict):
        bad.append(rid + ":fee_shape")
    else:
        for kind, amount in fee.items():
            if kind not in currencies:
                bad.append(rid + ":fee_currency:" + str(kind))
            if not isinstance(amount, int) or amount < 0:
                bad.append(rid + ":fee_amount:" + str(kind))
    out = r.get("output") or {}
    oid = out.get("item_id")
    if oid not in items:
        bad.append(rid + ":output_missing:" + str(oid))
    else:
        count = out.get("count")
        if not isinstance(count, int) or count < 1:
            bad.append(rid + ":output_count")
        elif count > items[oid]["stack_max"]:
            bad.append(rid + ":output_over_stack")
    success = r.get("success_pct")
    if not isinstance(success, (int, float)) or success < 0 or success > 100:
        bad.append(rid + ":success")
    craft_time = r.get("craft_time_s")
    if not isinstance(craft_time, int) or craft_time < 0 or craft_time > 86400:
        bad.append(rid + ":craft_time")
    if "grade_roll" in r:
        bad.append(rid + ":grade_roll")
    for key in RUNTIME_KEYS:
        if key in r:
            bad.append(rid + ":runtime:" + key)
report("every_recipe_valid", not bad, ";".join(bad[:8]))

# 配置里不许有第二份价格 / 掉落之类：配方只管材料 / 产出 / 费用。
extra = [k for k in doc if k not in ("schema", "version", "pending", "note",
                                     "professions", "recipes")]
report("recipe_no_extra_top_keys", not extra, ",".join(extra))

print("PASS static_config_end")
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static.txt"
if ! grep -qE '^(PASS|FAIL) ' "$TMP/static.txt"; then
  bad "静态断言没跑起来"; tail -12 "$TMP/static.txt" | detail
elif ! grep -q '^PASS static_config_end' "$TMP/static.txt"; then
  bad "静态断言没跑完"; grep -vE '^(PASS|FAIL) ' "$TMP/static.txt" | tail -6 | detail
fi

echo "== 3. 静态：代码 / proto / 库 / 挂载 =="
ROOT="$ROOT" python3 - > "$TMP/static2.txt" 2>&1 <<'PY'
import json, os, re

root = os.environ["ROOT"]

def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def cfg(p):
    return json.loads(read(p))

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + str(info)))

# 「代码里不许有第二份物品 / 配方编号」：这两份实现只认表里的字符串。
items = [x["id"] for x in cfg("config/items.json")["items"]]
recipes = [r["id"] for r in cfg("config/recipes.json")["recipes"]]
impl = read("src/Character/RecipeTable.cs") + read("src/Character/CraftRepository.cs")
lit_items = [i for i in items if re.search(r'"' + re.escape(i) + r'"', impl)]
lit_recipes = [r for r in recipes if r in impl]
report("code_no_item_literals", not lit_items, ",".join(lit_items[:6]))
report("code_no_recipe_literals", not lit_recipes, ",".join(lit_recipes[:6]))

table = read("src/Character/RecipeTable.cs")
report("table_reads_schema_version",
       "recipe-table/1" in table and "wire.Version" in table and "Professions" in table
       and "Recipes" in table, "加载器没读 schema / 版本 / 两份清单")
report("table_reads_all_fields",
       all(x in table for x in ("unlock_level", "inputs", "fee", "output",
                                "success_pct", "craft_time_s")),
       "加载器缺字段")
report("table_rejects_grade_roll", "grade_roll" in table and "B3" in table,
       "grade_roll 应当明确拒绝，不能静默忽略")

craft = read("src/Character/CraftRepository.cs")
report("craft_two_transactions", craft.count("BeginTransactionAsync") >= 2,
       "start / finish 应各一笔事务")
report("craft_uses_single_inventory_consume", "ConsumeInTransactionAsync" in craft,
       "材料没有走同一个扣减口")
report("craft_uses_single_currency_write", "ApplyCurrencyDeltaInTransactionAsync" in craft,
       "费用没有走 GrantRepository 的货币写法")
report("craft_uses_single_item_grant", "ApplyItemDeltasInTransactionAsync" in craft,
       "产出入包没有复用发奖的同一条 SQL")
report("craft_ready_gate", "remaining > 0" in craft and "craft_not_ready" in craft,
       "没到 ready_at 必须有拒绝")
report("craft_roll_from_config", "Random.Shared" in craft and "recipe.SuccessPct" in craft,
       "掷点不在服务端或没读配置成功率")
report("craft_txn_claim", "on conflict (txn_id) do nothing" in craft,
       "事务号没有抢号 SQL，重放会扣第二次")
report("craft_log_written", "craft_log" in craft and "craft_job" in craft,
       "缺流水或进行中的活")
report("craft_no_config_write", "File.WriteAllText" not in impl,
       "加载器或制作在写配置")

process = read("src/Character/CharacterProcess.cs")
report("process_loads_recipe", "RecipeTable.Load(config.RecipeTable" in process,
       "启动没加载配方表")
report("process_print_recipe", "recipe_table_version=" in process,
       "启动日志没有配方事实值")
report("process_wires_craft", "new CraftRepository(" in process
       and "AddSingleton(crafts)" in process, "CraftRepository 不在进程装配里")

svc = read(os.environ["AGG_CHARGRPC"])
report("service_four_rpcs",
       all(x in svc for x in ("Task<DescribeRecipesResponse> DescribeRecipes",
                              "Task<StartCraftResponse> StartCraft",
                              "Task<FinishCraftResponse> FinishCraft",
                              "Task<DescribeCraftResponse> DescribeCraft")),
       "四个 RPC 没齐")
report("service_reject_ok_false", "Ok = false" in svc and "Reason = ex.Code" in svc,
       "业务拒绝没有走 ok=false + reason")

proto = read("proto/mmo/character.proto")
report("proto_four_rpcs",
       len(re.findall(r"rpc\s+(DescribeRecipes|StartCraft|FinishCraft|DescribeCraft)\(", proto)) == 4,
       "proto 里的制作 RPC 不是四条")
report("proto_messages",
       all(("message " + m) in proto for m in (
           "DescribeRecipesRequest", "DescribeRecipesResponse", "RecipeFact",
           "StartCraftRequest", "StartCraftResponse",
           "FinishCraftRequest", "FinishCraftResponse",
           "DescribeCraftRequest", "DescribeCraftResponse")),
       "proto 消息没齐")
server_gen = read("src/Shared/Generated/Character.cs")
client_gen = read("client/MMOGame/Assets/Proto/Generated/Character.cs")
report("generated_both_sides",
       "class StartCraftRequest" in server_gen
       and "class StartCraftRequest" in client_gen
       and "DescribeRecipesResponse" in server_gen,
       "两端生成物没重出，或只出了一端")

gw = read("src/Gateway/CharacterApi.cs")
routes = ["/craft/recipes", "/craft/state", "/craft/start", "/craft/finish"]
report("gateway_four_routes", all(x in gw for x in routes), ",".join(routes))
start_block = gw[gw.find('"/craft/start"'):gw.find('"/craft/finish"')]
finish_block = gw[gw.find('"/craft/finish"'):]
report("gateway_business_200",
       "WriteAsync(context, 200" in start_block and "WriteAsync(context, 200" in finish_block,
       "业务拒绝没有走 200 + ok=false")

craft_sql = read("db/character/craft.sql")
report("db_craft_tables",
       'CREATE TABLE IF NOT EXISTS "角色".craft_job' in craft_sql
       and 'CREATE TABLE IF NOT EXISTS "角色".craft_log' in craft_sql
       and "ready_at" in craft_sql and "output_item_id" in craft_sql,
       "craft_job / craft_log 的 DDL 不全")
report("db_no_numbers", not re.search(r"success_pct|craft_time_s", craft_sql),
       "库里出现了配置数值")
main_sql = read("db/character/character.sql")
report("db_main_migration_includes",
       '"角色".craft_job' in main_sql and '"角色".craft_log' in main_sql,
       "主迁移没带两张新表，只跑主迁移的老卡会缺表")
man = cfg("db/migrations.json")
entries = next(s["entries"] for s in man["schemas"] if s["key"] == "character")
last = entries[-1]
# Q5 起后面还会有卡追加迁移：这里只钉「craft.sql 是角色 v22，且它之后的版本都更大」，
# 不钉「craft.sql 永远是最后一条」（那是当时的事实，不是规则）。
craft_idx = next((i for i, e in enumerate(entries) if e.get("file") == "db/character/craft.sql"), -1)
report("migration_appended",
       craft_idx >= 0 and entries[craft_idx].get("version") == 22
       and all(e.get("version") > 22 for e in entries[craft_idx + 1:]),
       "craft.sql 不是角色 v22，或它后面追加了更小的版本：%s" % last)

flags = read("tools/probe/CliProbe/CliFlags.cs")
parser = read("tools/probe/CliProbe/CliParser.cs")
flag_names = ["--recipe-list", "--recipe-table-path", "--craft-start", "--craft-finish",
              "--craft-state", "--raw-item-write"]
report("probe_flags",
       all(f in parser for f in flag_names) and "recipeList" in flags
       and "craftStartOf" in flags and "rawItemWriteId" in flags,
       "probe 入口没齐")
report("probe_dispatch", "CraftCli.TryReadOnly()" in read("tools/probe/CliProbe/CliEntry.cs")
       and "CraftCli.TryRunDbAsync" in read("tools/probe/CliProbe/DbCli.cs"),
       "probe 没挂 CraftCli")

allsh = read("tools/check/check-all.sh")
ci = read("tools/check/ci.sh")
report("check_all_registered",
       "k3) echo \"k1 k5\"" in allsh and re.search(r"ALL_CARDS=.*\bk3\b", allsh, re.S) is not None,
       "check-all 的依赖 / 名单没挂 k3")
client_fn = allsh[allsh.find("client_card_needs_unity()"):]
client_fn = client_fn[:client_fn.find("}")]
report("check_all_no_client", "k3" not in client_fn, "k3 不该进 Unity 名单")
report("ci_registered", re.search(r"^CHECKS=.*\bk3\b", ci, re.M) is not None,
       "ci.sh 的无库段没挂 k3")

queue = read("docs/任务队列.md")
report("queue_k3_done",
       re.search(r"\| 19 \| 1 \| K3 \|[^\n]*\| 完成 \|", queue) is not None,
       "任务队列 K3 行还没标完成")
# 不钉「下一张是谁」：K3 收口时是 D16，但每张卡收口都会改 NEXT（D16 这次就撞红过）。
# 只钉「NEXT 有值」——队列没有停在空处，派工能继续。
report("queue_next_present", re.search(r"^NEXT=\S+$", queue, re.M) is not None,
       "任务队列 NEXT 没填（收口后应指向下一张）")
# CURRENT 是队列的瞬时状态，不是 K3 的承诺：D17 在跑时它必然不是 —。
# 只钉「自洽」：要么没有进行中的卡，要么 CURRENT 指向的那一行确实标着进行中。
current_match = re.search(r"^CURRENT=(\S+)$", queue, re.M)
current = current_match.group(1) if current_match else ""
report("queue_current_consistent",
       current == "—"
       or re.search(r"\|\s*" + re.escape(current) + r"\s*\|[^\n]*\|\s*进行中\s*\|", queue) is not None,
       "CURRENT 既不是 —，也不是队列表里标进行中的卡")

config_doc = read("docs/配置设计.md")
report("config_doc_k3",
       "recipes.json" in config_doc and "recipe-table/1" in config_doc
       and "unlock_level" in config_doc, "配置设计第 7 节没写清 K3 的口径")
ledger = read("docs/数值台账.md")
report("ledger_registered", "`recipes.json`" in ledger, "数值台账表 A 没登记 recipes.json")
dev = read("docs/开发架构.md")
report("dev_doc_k3", re.search(r"\| 7 \| K3 \|.*v1\.180", dev) is not None,
       "开发架构 14.7 的 K3 行没写收口版本")
log = read("docs/开发日志.md")
report("log_k3", re.search(r"^### K3 · v1\.180", log, re.M) is not None,
       "开发日志没有 K3 条目")

print("PASS static_wiring_end")
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static2.txt"
if ! grep -q '^PASS static_wiring_end' "$TMP/static2.txt"; then
  bad "代码 / 挂载静态断言没跑完"
  grep -vE '^(PASS|FAIL) ' "$TMP/static2.txt" | tail -6 | detail
fi

echo "== 4. 只读入口 =="
if "$DOTNET" "$PROBE" --recipe-list > "$TMP/list.txt" 2>&1; then
  head1="$(head -1 "$TMP/list.txt")"
  # B2 把配方表的版本从 2 抬到 3：版本号现取，不钉现值（可调数值只认配置）。
  REC_VER="$(python3 -c 'import json;print(json.load(open("config/recipes.json",encoding="utf-8"))["version"])' | tr -d '\r')"
  case "$head1" in
    "recipe_table_state=ok version=${REC_VER} pending=true professions=4 recipes="*)
      ok "配方表读得出来：v${REC_VER}、四个副职、整表占位" ;;
    *) bad "清单头部不对：$head1" ;;
  esac
  rows="$(grep -c '^recipe=' "$TMP/list.txt" || true)"
  [ "$rows" -ge 12 ] && ok "配方不少于 12 条（实际 ${rows}）" || bad "配方条数少于 12：${rows}"
  if [ "$(grep -cE '^recipe=.* inputs=.* fee=.* output=.* success_pct=[0-9.]+ craft_time_s=[0-9]+$' "$TMP/list.txt")" = "$rows" ]; then
    ok "每条都带 inputs / fee / output / success_pct / craft_time_s"
  else
    bad "有条目缺字段：$(grep -vE '^recipe=.* inputs=.* fee=.* output=.* success_pct=[0-9.]+ craft_time_s=[0-9]+$' "$TMP/list.txt" | grep '^recipe=' | head -1)"
  fi
  # 副职名是中文：转码后的那一份才比得中（原文件保留给纯 ASCII 断言）。
  utf8 <"$TMP/list.txt" >"$TMP/list-u8.txt"
  for p in alchemy:炼丹 blacksmith:铁匠 tailor:裁缝 jeweler:巧匠; do
    grep -q "^recipe_profession=${p%%:*} name=${p##*:} recipes=[0-9]*" "$TMP/list-u8.txt" \
      && ok "副职 ${p%%:*}（${p##*:}）在清单里" \
      || bad "副职 ${p%%:*}（${p##*:}）没出现在清单里"
  done
else
  bad "清单读取失败"; tail -5 "$TMP/list.txt" | detail
fi

echo "== 5. 加一个配方只改配置 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
d = json.load(open(os.path.join(root, "config/recipes.json"), encoding="utf-8"))
d["recipes"].append({
    "id": "recipe_k3_probe_1", "profession": "alchemy", "unlock_level": 1,
    "inputs": [{"item_id": "ore_iron", "count": 1}],
    "fee": {"stone_1": 1},
    "output": {"item_id": "pill_focus", "count": 1},
    "success_pct": 50, "craft_time_s": 1, "note": "核对用，不进仓库"})
json.dump(d, open(os.path.join(tmp, "added.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
ADDED="$("$DOTNET" "$PROBE" --recipe-list --recipe-table-path "$TMP/added.json" 2>&1)"
if grep -q 'recipe=recipe_k3_probe_1 .*inputs=ore_iron:1 fee=stone_1:1 output=pill_focus:1 success_pct=50 craft_time_s=1' <<< "$ADDED"; then
  ok "新加一条配方：只改配置就出现在清单里"
else
  bad "新加配方没生效：$(printf '%s' "$ADDED" | grep recipe_k3_probe_1 | head -1)"
fi
head_added="$(head -1 <<< "$ADDED")"
case "$head_added" in
  *"recipes=13"*) ok "条数跟着配置走（12 → 13）" ;;
  *) bad "条数没跟着配置走：${head_added}" ;;
esac

echo "== 6. 坏配置都起不来 =="
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
base = json.load(open(os.path.join(root, "config/recipes.json"), encoding="utf-8"))

cases = {
    "profession_unknown": lambda d: d["recipes"][0].__setitem__("profession", "cooking"),
    "input_missing": lambda d: d["recipes"][0]["inputs"][0].__setitem__("item_id", "herb_none"),
    "fee_currency_unknown": lambda d: d["recipes"][0].__setitem__("fee", {"silver": 1}),
    "output_missing": lambda d: d["recipes"][0]["output"].__setitem__("item_id", "sword_none"),
    "success_over_100": lambda d: d["recipes"][0].__setitem__("success_pct", 101),
    "craft_time_negative": lambda d: d["recipes"][0].__setitem__("craft_time_s", -1),
    "profession_without_recipe": lambda d: d.__setitem__(
        "recipes", [r for r in d["recipes"] if r["profession"] != "jeweler"]),
    "profession_order": lambda d: d["professions"][0].__setitem__("id", "cooking"),
    "grade_roll": lambda d: d["recipes"][0].__setitem__(
        "grade_roll", {"mode": "fixed", "table": "x"}),
    "no_recipes": lambda d: d.__setitem__("recipes", []),
}
for name, mutate in cases.items():
    doc = copy.deepcopy(base)
    mutate(doc)
    json.dump(doc, open(os.path.join(tmp, "bad-%s.json" % name), "w", encoding="utf-8"),
              ensure_ascii=False)
PY

for case in profession_unknown input_missing fee_currency_unknown output_missing \
            success_over_100 craft_time_negative profession_without_recipe \
            profession_order grade_roll no_recipes; do
  out="$("$DOTNET" "$PROBE" --recipe-list --recipe-table-path "$TMP/bad-$case.json" 2>&1)"
  code=$?
  if [ "$code" = 2 ] && grep -q '^config_error=' <<< "$out"; then
    ok "坏配置 ${case}：起不来（$(head -1 <<< "$out" | cut -c1-52)…）"
  else
    bad "坏配置 ${case} 没起不来：exit=${code} 输出=$(head -1 <<< "$out")"
  fi
done
out="$("$DOTNET" "$PROBE" --recipe-list --recipe-table-path "$TMP/nope.json" 2>&1)"; code=$?
if [ "$code" = 2 ] && grep -q '^config_error=' <<< "$out"; then
  ok "配方表不存在：起不来（exit=2）"
else
  bad "配方表不存在时没报 config_error：exit=${code}"
fi

echo "== 7. 连库段（可选） =="
if [ "${MMO_K3_HAS_DB:-0}" != "1" ] || [ -z "${MMO_K3_CHARACTER_ID:-}" ] \
   || { [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; }; then
  printf '  ..   跳过：本机无 gamedb / 没给临时角色号；在 ubu 用 MMO_K3_HAS_DB=1 MMO_K3_CHARACTER_ID=<临时角色号> 跑\n'
else
  ID="$MMO_K3_CHARACTER_ID"
  ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
d = json.load(open(os.path.join(root, "config/recipes.json"), encoding="utf-8"))
d["recipes"][0]["inputs"] = [{"item_id": "herb_ling", "count": 12}]
d["recipes"][0]["fee"] = {"gold": 10}
d["recipes"][0]["success_pct"] = 100
d["recipes"][0]["craft_time_s"] = 2
d["recipes"][0]["unlock_level"] = 1
d["recipes"].append({
    "id": "recipe_k3_fail", "profession": "alchemy", "unlock_level": 1,
    "inputs": [{"item_id": "herb_ling", "count": 1}],
    "fee": {"gold": 1},
    "output": {"item_id": "pill_rejuvenate", "count": 1},
    "success_pct": 0, "craft_time_s": 0})
d["recipes"].append({
    "id": "recipe_k3_high", "profession": "alchemy", "unlock_level": 100,
    "inputs": [{"item_id": "herb_ling", "count": 1}],
    "fee": {"gold": 1},
    "output": {"item_id": "pill_rejuvenate", "count": 1},
    "success_pct": 100, "craft_time_s": 0})
d["recipes"].append({
    "id": "recipe_k3_short", "profession": "alchemy", "unlock_level": 1,
    "inputs": [{"item_id": "herb_ling", "count": 12}],
    "fee": {"gold": 5},
    "output": {"item_id": "pill_rejuvenate", "count": 1},
    "success_pct": 100, "craft_time_s": 0})
json.dump(d, open(os.path.join(tmp, "recipes-db.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
  RT=(--recipe-table-path "$TMP/recipes-db.json")
  state() { "$DOTNET" "$PROBE" --craft-state "$ID" "${RT[@]}" 2>&1; }
  herb_total() { state | sed -n 's/.*item=herb_ling count=\([0-9][0-9]*\).*/\1/p' | awk '{s+=$1} END{print s+0}'; }
  gold_total() { state | sed -n 's/.*kind=gold amount=\(-\{0,1\}[0-9][0-9]*\).*/\1/p' | head -1; }
  log_count() { state | grep -c '^craft_log='; }
  pill_total() { state | sed -n 's/.*item=pill_rejuvenate count=\([0-9][0-9]*\).*/\1/p' | awk '{s+=$1} END{print s+0}'; }

  "$DOTNET" "$PROBE" --raw-item-write "$ID" 1 herb_ling 10 >/dev/null 2>&1
  "$DOTNET" "$PROBE" --raw-item-write "$ID" 2 herb_ling 5 >/dev/null 2>&1
  "$DOTNET" "$PROBE" --raw-item-write "$ID" 3 pill_rejuvenate 0 >/dev/null 2>&1
  "$DOTNET" "$PROBE" --raw-currency-add "$ID" gold 100 >/dev/null 2>&1
  "$DOTNET" "$PROBE" --raw-level-write "$ID" 1 >/dev/null 2>&1

  [ "$(herb_total)" = 15 ] && ok "造料：两格灵草共 15" || bad "造料失败：灵草 $(herb_total)"
  [ "$(gold_total)" = 100 ] && ok "造料：金币 100" || bad "造料失败：金币 $(gold_total)"

  out="$("$DOTNET" "$PROBE" --craft-start "$ID" recipe_pill_rejuvenate --craft-txn k3-start-1 "${RT[@]}" 2>&1)"
  if grep -q '^craft_start=ok .*remaining_s=[12]' <<< "$out"; then
    ok "start：扣料扣费并给出 ready_at（$(head -1 <<< "$out")）"
  else
    bad "start 没成功：$(head -1 <<< "$out")"
  fi
  [ "$(herb_total)" = 3 ] && ok "start 后灵草 15 → 3（跨格扣）" || bad "start 后灵草=$(herb_total)，期望 3"
  [ "$(gold_total)" = 90 ] && ok "start 后金币 100 → 90" || bad "start 后金币=$(gold_total)，期望 90"
  [ "$(log_count)" = 1 ] && ok "start 写了 1 行流水" || bad "start 流水=$(log_count)，期望 1"

  out="$("$DOTNET" "$PROBE" --craft-finish "$ID" --craft-txn k3-finish-early "${RT[@]}" 2>&1)"
  grep -q 'reason=craft_not_ready' <<< "$out" && ok "没到 ready_at：finish 被拒" \
    || bad "没到 ready_at 时 finish 没被拒：$(head -1 <<< "$out")"
  [ "$(pill_total)" = 0 ] && ok "提前收：没有产出" || bad "提前收居然有产出 $(pill_total)"
  [ "$(grep -c '^craft_log=' <<< "$(state)")" = 1 ] && ok "提前收不写 finish 流水" \
    || bad "提前收写了 finish 流水"

  out="$("$DOTNET" "$PROBE" --craft-start "$ID" recipe_pill_rejuvenate --craft-txn k3-start-1 "${RT[@]}" 2>&1)"
  grep -q 'reason=duplicate' <<< "$out" && ok "同一个事务号再 start：duplicate，不二次扣料" \
    || bad "重放 start 没被挡住：$(head -1 <<< "$out")"
  [ "$(herb_total)" = 3 ] && [ "$(gold_total)" = 90 ] && ok "重放 start 数值一个字节没动" \
    || bad "重放 start 改了数值：灵草=$(herb_total) 金币=$(gold_total)"

  sleep 2.2
  out="$("$DOTNET" "$PROBE" --craft-finish "$ID" --craft-txn k3-finish-1 "${RT[@]}" 2>&1)"
  grep -q '^craft_finish=ok .*success=true output=pill_rejuvenate count=1' <<< "$out" \
    && ok "到点 finish：成功率 100 命中，产出回春丹 1" \
    || bad "到点 finish 没成功：$(head -1 <<< "$out")"
  [ "$(pill_total)" = 1 ] && ok "产出入包：回春丹 1" || bad "产出没入包：$(pill_total)"
  [ "$(log_count)" = 2 ] && ok "finish 写了第 2 行流水" || bad "finish 流水=$(log_count)，期望 2"
  [ "$(state | grep '^craft_state=' )" = "craft_state=ok id=$ID active=false" ] \
    && ok "收完活清掉（active=false）" || bad "收完还挂着活：$(state | grep '^craft_state=')"

  out="$("$DOTNET" "$PROBE" --craft-finish "$ID" --craft-txn k3-finish-1 "${RT[@]}" 2>&1)"
  grep -q '^craft_finish=duplicate' <<< "$out" && ok "同一个事务号再 finish：duplicate，不二次发货" \
    || bad "重放 finish 没被挡住：$(head -1 <<< "$out")"
  [ "$(pill_total)" = 1 ] && [ "$(log_count)" = 2 ] && ok "重放 finish 数值一个字节没动" \
    || bad "重放 finish 改了数值：回春丹=$(pill_total) 流水=$(log_count)"

  out="$("$DOTNET" "$PROBE" --craft-start "$ID" recipe_k3_fail --craft-txn k3-start-fail "${RT[@]}" 2>&1)"
  grep -q '^craft_start=ok ' <<< "$out" && ok "失败样本：start 成功（成功率 0）" \
    || bad "失败样本 start 没成功：$(head -1 <<< "$out")"
  out="$("$DOTNET" "$PROBE" --craft-finish "$ID" --craft-txn k3-finish-fail "${RT[@]}" 2>&1)"
  grep -q '^craft_finish=ok .*success=false output= count=0' <<< "$out" \
    && ok "成功率 0：finish 是「做了但没成」，不是拒绝" \
    || bad "成功率 0 的 finish 不对：$(head -1 <<< "$out")"
  [ "$(pill_total)" = 1 ] && ok "失败不产出：回春丹仍是 1" || bad "失败也发了产出：$(pill_total)"
  [ "$(log_count)" = 4 ] && ok "失败也写流水（4 行）" || bad "失败流水=$(log_count)，期望 4"

  out="$("$DOTNET" "$PROBE" --craft-start "$ID" recipe_k3_high --craft-txn k3-start-high "${RT[@]}" 2>&1)"
  grep -q 'reason=level_too_low' <<< "$out" && ok "解锁等级不够：level_too_low" \
    || bad "解锁等级没挡住：$(head -1 <<< "$out")"
  [ "$(herb_total)" = 2 ] && [ "$(gold_total)" = 89 ] && ok "被拒时材料与金币没动" \
    || bad "被拒时数值动了：灵草=$(herb_total) 金币=$(gold_total)"

  out="$("$DOTNET" "$PROBE" --craft-start "$ID" recipe_k3_short --craft-txn k3-start-short "${RT[@]}" 2>&1)"
  grep -q 'reason=material_insufficient' <<< "$out" && ok "材料不够：material_insufficient" \
    || bad "材料不够没挡住：$(head -1 <<< "$out")"
  [ "$(herb_total)" = 2 ] && ok "材料不够时灵草没动" || bad "材料不够还扣了灵草：$(herb_total)"

  "$DOTNET" "$PROBE" --raw-currency-write "$ID" gold 0 >/dev/null 2>&1
  out="$("$DOTNET" "$PROBE" --craft-start "$ID" recipe_k3_fail --craft-txn k3-start-poor "${RT[@]}" 2>&1)"
  grep -q 'reason=fee_insufficient' <<< "$out" && ok "金币不够：fee_insufficient" \
    || bad "金币不够没挡住：$(head -1 <<< "$out")"
  [ "$(herb_total)" = 2 ] && ok "金币不够时灵草没动（整笔回滚）" || bad "金币不够还扣了灵草：$(herb_total)"

  out="$("$DOTNET" "$PROBE" --craft-start "$ID" recipe_nope --craft-txn k3-start-nope "${RT[@]}" 2>&1)"
  grep -q 'reason=unknown_recipe' <<< "$out" && ok "未知配方：unknown_recipe" \
    || bad "未知配方没挡住：$(head -1 <<< "$out")"
  out="$("$DOTNET" "$PROBE" --craft-finish "$ID" --craft-txn k3-finish-none "${RT[@]}" 2>&1)"
  grep -q 'reason=no_craft' <<< "$out" && ok "没有活时 finish：no_craft" \
    || bad "没有活时 finish 没挡住：$(head -1 <<< "$out")"
fi

finish "$TITLE"
