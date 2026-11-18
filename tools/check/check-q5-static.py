#!/usr/bin/env python3
"""tools/check/check-q5-static.py —— Q5 静态断言（check-q5.sh 第 2 段调用）。

约定与其它卡的静态段一致：逐条往 stdout 打 `PASS <名字>` / `FAIL <名字>  <原因>`，
由 shell 计数；退出码恒 0（除非脚本自身崩），红了看行不看码。
"""
import hashlib
import json
import os
import re
import sys

os.chdir(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

out = sys.stdout


def report(k, cond, info=""):
    out.write(("PASS " if cond else "FAIL ") + k + ("" if cond else "  " + str(info)) + "\n")


def read(p):
    try:
        return open(p, encoding="utf-8").read()
    except Exception:
        return ""

cfg = json.loads(read("config/city_tasks.json"))
tasks = cfg.get("tasks", [])
stones = json.loads(read("config/stone_exchange.json")).get("tiers", [])
cities = {c.get("city_id") for c in json.loads(read("config/city.json")).get("cities", [])}
realms = json.loads(read("config/realms.json"))
realm_names = set(realms.get("realms", [])) | set(realms.get("aliases", {}).keys())

report("config_schema", cfg.get("schema") == "city-task-table/1", cfg.get("schema"))
report("config_version", isinstance(cfg.get("version"), int) and cfg["version"] >= 1, cfg.get("version"))
report("config_pending", cfg.get("pending") is True, "任务与数值方案没写定，整体标待定")
report("config_keys", set(cfg) <= {"schema", "version", "pending", "note", "tasks"}, sorted(cfg))
report("tasks_count", len(tasks) >= 2, len(tasks))
ids = [t.get("id") for t in tasks]
report("task_ids_unique", len(ids) == len(set(ids)), ids)
report("task_city", all(t.get("city_id") in cities for t in tasks),
       [(t.get("id"), t.get("city_id")) for t in tasks if t.get("city_id") not in cities])
report("task_period", all(t.get("period") in ("daily", "weekly") for t in tasks),
       [(t.get("id"), t.get("period")) for t in tasks])
report("task_periods_cover", {"daily", "weekly"} <= {t.get("period") for t in tasks},
       "日常与周常各一条")
report("task_counts", all(isinstance(t.get("max_per_period"), int) and 1 <= t["max_per_period"] <= 100
                          for t in tasks), "次数在配置里")
report("task_levels", all(isinstance(t.get("require_level", 0), int) and 0 <= t.get("require_level", 0) <= 1000
                          for t in tasks), "等级门槛在配置里（0 表示不卡）")
report("task_realms", all((not t.get("require_realm")) or t["require_realm"] in realm_names for t in tasks),
       "境界门槛在 realms.json 的顺序表里")
bad_cur = [(t.get("id"), r.get("currency")) for t in tasks for r in t.get("rewards", [])
           if r.get("currency") not in stones]
report("reward_currency_stone", not bad_cur, bad_cur or "奖励只有灵石五档")
report("reward_amount", all(isinstance(r.get("amount"), int) and r["amount"] >= 1
                            for t in tasks for r in t.get("rewards", [])), "数量是正整数")
report("note_calendar", "calendar.json" in cfg.get("note", "")
       and "业务日" in cfg.get("note", "") and "周首" in cfg.get("note", ""),
       "note 写明重置边界只认日历")
report("note_grant_txn", "grant_txn" in cfg.get("note", "") and "重复" in cfg.get("note", ""),
       "note 写明走 grant_txn 且重复不双发")
report("note_tax", "city_stone_tax_ledger" in cfg.get("note", "") and "tax_pct" in cfg.get("note", ""),
       "note 写明按城池税记账、税率读库")

table = read("src/Character/CityTaskTable.cs")
repo = read("src/Character/CityTaskRepository.cs")
clock = re.compile(r"CURRENT_DATE|date_trunc\(|DateTime\.Now|DateTime\.Today", re.I)
report("no_second_clock", not clock.search(table) and not clock.search(repo),
       "任务表与仓储不出现第二套时钟 / 数据库当天函数")
report("period_from_calendar", "CityTaskTable.PeriodKey" in repo
       and "CalendarTable.DayKey" in table and "CalendarTable.WeekStartDate" in table,
       "周期键只由 CalendarTable 算")

report("one_transaction", repo.count("BeginTransactionAsync") == 1
       and repo.count("CommitAsync") == 1 and repo.count("RollbackAsync") >= 3,
       (repo.count("BeginTransactionAsync"), repo.count("CommitAsync"), repo.count("RollbackAsync")))
report("claim_txn_first", "await grants.TryClaimTxnInTransactionAsync" in repo
       and repo.index("await grants.TryClaimTxnInTransactionAsync")
       < repo.index("await grants.ApplyCurrencyDeltaInTransactionAsync"),
       "同一笔事务里先抢 grant_txn 号，再动钱")
report("currency_via_grant", "ApplyCurrencyDeltaInTransactionAsync" in repo)
bad_currency_sql = re.findall(r'(?:insert into|update)\\?"角色\\?"\.currency', repo, re.I)
report("no_direct_currency_write", not bad_currency_sql, bad_currency_sql or "钱的写入口只有 GrantRepository")
report("ledger_and_progress", "city_task_ledger" in repo and "city_task_progress" in repo
       and "done_count = done_count + 1" in repo,
       "流水与进度同笔提交")
report("tax_in_same_txn", "RecordStoneGrantInTransactionAsync" in repo,
       "抽成在发灵石那一笔事务里记账")
taxrepo = read("src/Character/CityTaxRepository.cs")
report("tax_rate_from_db", 'select tax_pct from \\"社交\\".city' in taxrepo
       or "select tax_pct from \"社交\".city" in taxrepo,
       "税率读运行时城池行，不读请求 / 配置")
report("tax_only_ledger", "只记账" in taxrepo and "不从玩家" in taxrepo,
       "抽成只记账、不从玩家扣（与 N9 金币口径一致）")

ddl = read("db/character/city_task.sql")
main = read("db/character/character.sql")
manifest = read("db/migrations.json")
report("ddl_progress", 'CREATE TABLE IF NOT EXISTS "角色".city_task_progress' in ddl
       and "period_key" in ddl and "CHECK (done_count >= 0)" in ddl, "进度表带周期键与非负")
report("ddl_ledger", 'CREATE TABLE IF NOT EXISTS "角色".city_task_ledger' in ddl
       and "PRIMARY KEY (txn_id, currency)" in ddl and "CHECK (amount > 0)" in ddl, "任务流水")
soc_ddl = read("db/social/city_task.sql")
soc_agg = read("db/social/social.sql")
report("ddl_stone_tax", 'CREATE TABLE IF NOT EXISTS "社交".city_stone_tax_ledger' in soc_ddl
       and "gross_stone" in soc_ddl and "tax_stone" in soc_ddl
       and "CHECK (tax_stone <= gross_stone)" in soc_ddl,
       "灵石抽成账本在社交迁移里（单位是灵石）")
report("ddl_tax_in_aggregate", "city_stone_tax_ledger" in soc_agg
       and soc_agg.count('CREATE TABLE IF NOT EXISTS "社交".city_stone_tax_ledger') == 1,
       "社交聚合主迁移整份追加同一份 DDL")
report("ddl_tax_separate", '"社交".city_tax_ledger' not in ddl
       and '"社交".city_tax_ledger' not in soc_ddl,
       "不碰金币账本：两张表分开记，单位不同")
report("main_selfcontained", all(t in main for t in
       ("city_task_progress", "city_task_ledger", "city_stone_tax_ledger")), "主迁移整份追加")
report("migration_entry", re.search(r'"version":\s*23,\s*"file":\s*"db/character/city_task\.sql"', manifest)
       is not None, "db/migrations.json 登记角色 23")
report("migration_entry_social", re.search(r'"version":\s*12,\s*"file":\s*"db/social/city_task\.sql"', manifest)
       is not None, "db/migrations.json 登记社交 12")

proto = read("proto/mmo/character.proto")
report("proto_rpcs", "rpc DescribeCityTasks(" in proto and "rpc CompleteCityTask(" in proto, "两条 RPC")
report("proto_facts", "message CityTaskFact" in proto and "message CityTaskGrantFact" in proto
       and "message DescribeCityTasksResponse" in proto, "面板与发放事实消息")
srv = read("src/Shared/Generated/Character.cs")
cli_proto = read("client/MMOGame/Assets/Proto/Generated/Character.cs")
if cli_proto:
    report("proto_generated_both", "DescribeCityTasks" in srv and "CompleteCityTask" in srv
           and "DescribeCityTasks" in cli_proto and "CompleteCityTask" in cli_proto,
           "两端生成物都重出了")
else:
    report("proto_generated_both", "DescribeCityTasks" in srv and "CompleteCityTask" in srv,
           "本机副本没有客户端生成物（只同步了服务端），服务端生成物已重出")
man = json.loads(read("proto/manifest.json")) if read("proto/manifest.json").strip() else {}
entries = man.get("protos") or man.get("files") or []
sha = hashlib.sha256(open("proto/mmo/character.proto", "rb").read()).hexdigest()
hit = [e for e in entries if "character.proto" in json.dumps(e)]
report("proto_manifest", bool(hit) and sha in json.dumps(hit), "manifest 钉住 character.proto 的 sha")

api = read("src/Gateway/CityTaskApi.cs")
gw = read("src/Gateway/GatewayProcess.cs")
report("gateway_routes", '"/city/tasks"' in api and '"/city/tasks/complete"' in api, "两个 HTTP 入口")
report("gateway_txn", '"city_task:"' in api, "事务号服务端生成，客户端带不进来")
report("gateway_registered", "CityTaskApi.Map(app, tokens, config)" in gw, "网关挂上路由")
parser = read("tools/probe/CliProbe/CliParser.cs")
citycli = read("tools/probe/CliProbe/CityTaskCli.cs")
flags = read("tools/probe/CliProbe/CliFlags.cs")
dbcli = read("tools/probe/CliProbe/DbCli.cs")
entry = read("tools/probe/CliProbe/CliEntry.cs")
loaders = read("tools/probe/CliProbe/CliLoaders.cs")
report("cli_flags", "CityTaskCli.Handles" in parser and "CityTaskCli.Parse" in parser
       and all(k in citycli for k in
               ('"--city-task-config"', '"--city-task-state"', '"--city-task-complete"', '"--city-task-ledger"')),
       "四个核对 flag 由 CityTaskCli 解析、CliParser 一行分发")
report("cli_state", all(k in flags for k in ("cityTaskConfig", "cityTaskStateOf", "cityTaskLedgerOf")),
       "共享状态声明")
report("cli_loaders", "LoadCityTasks" in loaders and "CityTaskTable.Load" in loaders,
       "只读入口与启动共用同一份加载链")
report("cli_dispatch", "CityTaskCli.TryRunDbAsync" in dbcli and "cityTaskStateOf" in dbcli
       and "CityTaskCli.TryReadOnly" in entry, "连库 / 只读两条分发都在")

ci = read("tools/check/ci.sh")
allsh = read("tools/check/check-all.sh")
report("ci_registered", re.search(r"^CHECKS=.*\bq5\b", ci, re.M) is not None, "ci.sh 的 CHECKS 有 q5")
report("checkall_deps", re.search(r"q5\)\s*echo", allsh) is not None, "check-all.sh 的 deps_of 有 q5")
report("checkall_cards", re.search(r'ALL_CARDS="[^"]*\bq5\b', allsh, re.S) is not None,
       "ALL_CARDS 有 q5")

inv = read("docs/不变量清单.md")
report("invariants_doc", "| INV-20 |" in inv and "| INV-21 |" in inv, "清单有 INV-20 / INV-21")
queue = read("docs/任务队列.md")
report("queue_row", re.search(r"\| \d+ \| 2 \| Q5 \|", queue) is not None
       and re.search(r"\| Q5 \|[^\n]*\| 2 \|", queue) is not None, "队列有 Q5 行且风险 2")