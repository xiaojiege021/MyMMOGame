#!/usr/bin/env python3
"""Q12/K3 静态断言：名字规则、商城第十九类、登记表与三处接线。

只读文件，不连库、不起进程。每行输出 PASS/FAIL，由 check-q12.sh 计数。
判据一律读现值：条数、价格、行数都不在脚本里写死（除「方案 12.1 表格行数 ==
配置的类别数」这一条联动校验）。
"""
import json
import os
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else "."
FAILS = []


def read(path):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server"}
    _name = __import__("os").path.basename(path)
    if _name in _sub:
        # 14.8 拆分卡之后：主文件 + 分片目录一起读（断言文本不变，只改读法）。
        _os = __import__("os"); _g = __import__("glob")
        _b = path if _os.path.isabs(path) else _os.path.join(_os.getcwd(), path)
        if not _os.path.exists(_b):
            _d = _os.path.dirname(_os.getcwd())
            while _d and not _os.path.exists(_os.path.join(_d, path)):
                _nd = _os.path.dirname(_d)
                if _nd == _d: break
                _d = _nd
            _b = _os.path.join(_d, path)
        return "".join(open(f, encoding="utf-8").read() for f in
                       [_b] + sorted(_g.glob(_os.path.join(_os.path.dirname(_b), _sub[_name], "*.cs"))))
    full = os.path.join(ROOT, path)
    if not os.path.exists(full):
        return ""
    return open(full, encoding="utf-8").read()


def load(path):
    try:
        return json.loads(read(path))
    except Exception:
        return {}


def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + str(info)))
    if not cond:
        FAILS.append(key)


rules = load("config/name_rules.json")
items = load("config/items.json")
shop = load("config/shop.json")

# ---------------------------------------------------------------- 规则正本
report("rules_schema", rules.get("schema") == "name-rules/2" and rules.get("pending") is False,
       "schema=%s pending=%s" % (rules.get("schema"), rules.get("pending")))

mn, mx = rules.get("min_length"), rules.get("max_length")
report("rules_length", isinstance(mn, int) and isinstance(mx, int) and 1 <= mn < mx <= 24,
       "min=%s max=%s" % (mn, mx))
report("rules_unique", rules.get("global_unique") is True,
       "global_unique=%s" % rules.get("global_unique"))
report("rules_rename", rules.get("allow_rename") is True,
       "allow_rename=%s" % rules.get("allow_rename"))

item_map = {i.get("id"): i for i in items.get("items", [])}
card = item_map.get("rename_card")
report("rules_item_id", rules.get("rename_item_id") == "rename_card" and card is not None,
       "rename_item_id=%s in_items=%s" % (rules.get("rename_item_id"), card is not None))
report("item_rename_card", card is not None
       and card.get("bind") == "always"
       and bool((card.get("desc") or "").strip()),
       "bind=%s" % ((card or {}).get("bind"),))

word_file = os.path.join(ROOT, rules.get("banned_words_file") or "")
words = []
if os.path.exists(word_file):
    for line in open(word_file, encoding="utf-8"):
        line = line.strip()
        if line and not line.startswith("#"):
            words.append(line)
report("rules_banned_file", len(words) > 0,
       "file=%s words=%d" % (rules.get("banned_words_file"), len(words)))

# ---------------------------------------------------------------- 商城第十九类
cats = shop.get("categories") or []
report("shop_category", "rename_card" in cats, "categories=%d" % len(cats))
report("shop_not_forbidden", "rename_card" not in (shop.get("forbidden") or []),
       "forbidden_has_rename_card")

shop_item = next((i for i in shop.get("items", []) if i.get("id") == "shop_rename_card_1"), None)
delivery = (shop_item or {}).get("delivery") or {}
report("shop_item",
       shop_item is not None
       and shop_item.get("category") == "rename_card"
       and shop_item.get("currency") == "yuanbao"
       and isinstance(shop_item.get("price"), int) and shop_item.get("price") > 0
       and delivery.get("mode") == "item"
       and delivery.get("item_id") == "rename_card"
       and delivery.get("count") == 1
       and shop_item.get("on_sale") is True,
       "item=%s" % json.dumps(shop_item, ensure_ascii=False)[:160])

# 方案 12.1：表格行数与配置的类别数联动（方案追加一行而配置没跟，或反过来，都会红）。
plan = read("docs/MMO系统架构方案.html")
sec = plan.split('<span class="n">12.1</span>', 1)[-1].split("</table>", 1)[0]
plan_rows = sec.count("<tr><td>")
report("plan_121_rows", plan_rows == len(cats),
       "plan_rows=%d config_categories=%d" % (plan_rows, len(cats)))
report("plan_121_rename", "改名卡" in sec, "方案 12.1 里没有「改名卡」这一行")

# ---------------------------------------------------------------- 代码
table_cs = read("src/Character/NameRulesTable.cs")
report("table_normalize",
       "NormalizationForm.FormKC" in table_cs
       and "ToUpperInvariant" in table_cs
       and ".Trim()" in table_cs,
       "缺 NFKC / 大写 / trim 之一")
report("table_load_checks",
       'name-rules/2' in table_cs
       and "items.Find(renameItemId)" in table_cs
       and "敏感词表里一条词都没有" in table_cs,
       "缺 schema / 卡在物品表 / 空词表 之一")
report("table_reasons",
       all(r in table_cs for r in
           ("name_too_short", "name_too_long", "name_banned", "name_invalid",
            "name_missing")),
       "拒绝码不全")

repo = read("src/Character/CharacterRepository.cs")
report("repo_registry", "character_name (name_key, name, character_id)" in repo,
       "建号或改名没有写登记表")
report("repo_release", 'delete from \\"角色\\".character_name where character_id' in repo
       or 'delete from "角色".character_name where character_id' in repo,
       "改名没有释放旧名")
rename_body = repo.split("public async Task<RenameOutcome> RenameAsync", 1)[-1]
claim_at = rename_body.find("TryClaimTxnInTransactionAsync")
consume_at = rename_body.find("ConsumeInTransactionAsync")
report("repo_order", claim_at != -1 and consume_at != -1 and claim_at < consume_at,
       "抢事务号必须早于扣卡：claim=%d consume=%d" % (claim_at, consume_at))

service = read("src/Character/CharacterGrpcService.cs")
report("service_rename", "public override async Task<RenameCharacterResponse> RenameCharacter" in service,
       "服务没有 RenameCharacter")
report("service_no_hardcoded_length", "name.Length > 16" not in service,
       "建号里还留着写死的长度 16")
report("service_uses_rules", "nameRules.Check(" in service and "nameRules.RenameItemId" in service,
       "服务没有走规则表 / 卡编号")

gateway = read("src/Gateway/CharacterApi.cs")
report("gateway_route", '"/character/rename"' in gateway and '"rename:" + Guid' in gateway,
       "网关没有改名路由或事务号")
report("gateway_no_client_txn", "TxnId = body." not in gateway.split('"/character/rename"', 1)[-1][:4000],
       "改名请求里的 txn 不该由客户端给")

proto_text = read("proto/mmo/character.proto")
gen = read("src/Shared/Generated/CharacterGrpc.cs")
report("proto_rpc", "rpc RenameCharacter(" in proto_text and "RenameCharacter" in gen,
       "proto 或生成物缺 RenameCharacter")
report("proto_fields",
       "string new_name = 3" in proto_text and "string txn_id = 4" in proto_text,
       "请求字段编号变了")

# ---------------------------------------------------------------- 数据
ddl = read("db/character/name_registry.sql")
report("ddl_table", 'CREATE TABLE IF NOT EXISTS "角色".character_name' in ddl,
       "没有建表")
report("ddl_unique", "PRIMARY KEY" in ddl and "character_name_character" in ddl,
       "缺 name_key 主键或每角色唯一索引")
report("ddl_single_normalize", "lower(" not in ddl,
       "迁移里另写了一套 lower 归一（应该只在 C# 一处）")

main_ddl = read("db/character/character.sql")
report("main_migration", 'character_name' in main_ddl,
       "主迁移里没有同一份 DDL（老卡只跑主迁移会缺表）")

manifest = load("db/migrations.json")
entries = (manifest.get("schemas") or [{}])[0].get("entries") or []
# 按文件找，不按「最后一条」：后面的卡会继续往后追加迁移（C4 起是 enhance_item.sql）。
entry = next((e for e in entries if e.get("file") == "db/character/name_registry.sql"), None)
versions = [e.get("version") for e in entries]
report("migration_registered",
       entry is not None and versions.count(entry.get("version")) == 1,
       "迁移清单里 name_registry=%s" % json.dumps(entry, ensure_ascii=False))

# ---------------------------------------------------------------- 只读入口接线
loaders = read("tools/probe/CliProbe/CliLoaders.cs")
entry = read("tools/probe/CliProbe/CliEntry.cs")
report("probe_wiring",
       "LoadNameRules" in loaders and "NameCli.TryReadOnly()" in entry,
       "探针没有接名字规则入口")

if FAILS:
    print("STATIC_FAILS=" + ",".join(FAILS))
