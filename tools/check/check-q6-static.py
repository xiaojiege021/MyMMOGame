#!/usr/bin/env python3
"""tools/check/check-q6-static.py —— Q6 静态断言（check-q6.sh 第 2 段调用）。

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


cfg = json.loads(read("config/season_pass_progress.json"))
sources = cfg.get("sources", [])
dungeons = {d.get("id") for d in json.loads(read("config/dungeons.json")).get("dungeons", [])}

report("config_schema", cfg.get("schema") == "season-pass-progress/1", cfg.get("schema"))
report("config_version", isinstance(cfg.get("version"), int) and cfg["version"] >= 1,
       cfg.get("version"))
report("config_pending", cfg.get("pending") is True, "数值方案没写定，整体标待定")
report("config_keys", set(cfg) <= {"schema", "version", "pending", "note",
                                   "points_per_stage", "sources"}, sorted(cfg))
report("points_per_stage", isinstance(cfg.get("points_per_stage"), int)
       and cfg["points_per_stage"] >= 1, cfg.get("points_per_stage"))
report("sources_present", len(sources) >= 2, len(sources))

ids = [s.get("id") for s in sources]
report("source_ids_unique", len(set(ids)) == len(ids) and all(ids), ids)
report("source_keys", all(set(s) <= {"id", "kind", "dungeon_id", "points", "period",
                                    "max_per_period"} for s in sources),
       "来源只许出现这几个键")
report("source_kinds", all(s.get("kind") in ("dungeon_clear", "mainline_stage")
                           for s in sources), [s.get("kind") for s in sources])
report("source_dungeons_exist",
       all(s.get("dungeon_id") in dungeons for s in sources if s.get("kind") == "dungeon_clear"),
       [s.get("dungeon_id") for s in sources if s.get("kind") == "dungeon_clear"])
report("source_mainline_no_dungeon",
       all(not s.get("dungeon_id") for s in sources if s.get("kind") == "mainline_stage"),
       "主线来源不挂副本")
report("source_periods", all(s.get("period") in ("daily", "weekly", "once") for s in sources),
       [s.get("period") for s in sources])
report("source_caps", all(
    (s.get("max_per_period") in (None, 1) if s.get("period") == "once"
     else isinstance(s.get("max_per_period"), int) and s["max_per_period"] >= 1)
    for s in sources), [s.get("max_per_period") for s in sources])
report("source_points_positive",
       all(isinstance(s.get("points"), int) and s["points"] >= 1 for s in sources),
       [s.get("points") for s in sources])
report("no_currency_like_keys",
       not [k for k in json.dumps(cfg, ensure_ascii=False).split('"')
            if k in ("gold", "yuanbao", "stone_1", "item_id", "equipment", "dungeon_runs")],
       "通行证点数不是货币，也不是第二条发货路")

sql = read("db/character/season_pass_progress.sql")
main_sql = read("db/character/character.sql")
report("sql_points_table", 'CREATE TABLE IF NOT EXISTS "角色".season_pass_points' in sql
       and "PRIMARY KEY (character_id, season_id)" in sql, "点数表与主键")
report("sql_ledger_table", 'CREATE TABLE IF NOT EXISTS "角色".season_pass_point_ledger' in sql
       and "txn_id       text        NOT NULL PRIMARY KEY" in sql, "流水表以 txn_id 幂等")
report("sql_ledger_checks", "CHECK (points > 0)" in sql
       and "CHECK (period_key" not in sql
       and "kind IN ('dungeon_clear', 'mainline_stage')" in sql, "流水的取值约束")
report("sql_in_main_migration",
       'CREATE TABLE IF NOT EXISTS "角色".season_pass_points' in main_sql
       and 'CREATE TABLE IF NOT EXISTS "角色".season_pass_point_ledger' in main_sql,
       "主迁移自包含")

migrations = json.loads(read("db/migrations.json"))
character_entries = next((s.get("entries", []) for s in migrations.get("schemas", [])
                          if s.get("key") == "character"), [])
# 只认「season_pass_progress.sql 在这张表里、版本号比排在它前面的都大」。
# 不钉「它必须是最后一条」——Q9 的 deserter_penalty.sql（v25）之后每加一张迁移卡都会破。
mine = [e for e in character_entries if e.get("file") == "db/character/season_pass_progress.sql"]
idx = character_entries.index(mine[0]) if mine else -1
report("migration_registered", bool(mine) and isinstance(mine[0].get("version"), int),
       mine[0] if mine else "season_pass_progress.sql 没登记")
report("migration_appended", bool(mine) and all(
           e.get("version", 0) < mine[0].get("version", 0) for e in character_entries[:idx]),
       "season_pass_progress.sql 的版本号比它前面的都大")

table = read("src/Character/SeasonPassProgressTable.cs")
repo = read("src/Character/SeasonPassProgressRepository.cs")
pass_repo = read("src/Character/SeasonPassRepository.cs")
svc = read("src/Character/CharacterGrpcService.cs")
process = read("src/Character/CharacterProcess.cs")

report("table_validation", all(w in table for w in (
    "dungeon_id 不在 config/dungeons.json 里", "不许出现", "period 只认",
    "max_per_period", "主线来源只能配一个")), "加载期硬校验在")
report("table_required", "PointsPerStage" in table and "RequiredPoints" in table,
       "阶门槛 = 阶号 × 每阶点数")
report("table_missing_degrades", "SeasonPassProgressTable Missing" in table.replace("() =>", "() =>")
       or "public static SeasonPassProgressTable Missing()" in table, "缺表降级但 IsMissing=true")

report("repo_single_txn", repo.count("BeginTransactionAsync") == 1, repo.count("BeginTransactionAsync"))
report("repo_ledger_then_points",
       0 <= repo.index('insert into \\"角色\\".season_pass_point_ledger')
       < repo.index('update \\"角色\\".season_pass_points set points'), "先写流水再加点数")
report("repo_txn_idempotent", "LedgerExistsAsync" in repo
       and "PostgresErrorCodes.UniqueViolation" in repo, "同号重放与并发都回 duplicate")
report("repo_cap_by_config", "source.MaxPerPeriod" in repo and "CountAsync" in repo,
       "次数上限只从配置读")
report("repo_period_from_calendar", "calendar.DayKey" in repo and "calendar.WeekStartDate" in repo
       and "CURRENT_DATE" not in repo and "date_trunc" not in repo,
       "周期键只由日历表算")
report("repo_no_currency_write",
       'insert into \"角色\".currency' not in repo and 'update \"角色\".currency' not in repo,
       "点数不是货币：不写货币行")
report("claim_gate", "points_not_enough" in pass_repo
       and "progress.RequiredPoints(stage)" in pass_repo
       and 'select points from \\"角色\\".season_pass_points' in pass_repo,
       "领取门槛读本季点数")
report("claim_gate_before_claim", pass_repo.index("points_not_enough")
       < pass_repo.index("already_claimed"), "门槛先于「领过没有」判")
report("no_hardcoded_points", not re.search(r"PointsPerStage\s*=\s*\d+;", repo + table),
       "每阶点数不写死在代码里")

report("hook_dungeon_clear", "FindByDungeon" in svc and "AwardPassPointsAsync" in svc,
       "副本通关按 dungeon_id 找来源")
report("hook_mainline", "FindMainline" in svc and "result.Stage >= 1" in svc,
       "主线交任务推进后加点")
report("hook_idempotent_key", "pass_pt:" in svc and "SHA256" in svc,
       "加点幂等键从事件号派生")
report("process_wired", "SeasonPassProgressRepository(" in process
       and "season_pass_progress=version=" in process
       and "seasonPassProgressTable" in process, "启动加载 / 打印 / 注入都在")

proto = read("proto/mmo/character.proto")
report("proto_reward_fields", "uint32 required_points = 6;" in proto
       and "bool unlocked = 7;" in proto, "每阶门槛与是否解锁")
report("proto_points_fields", "int64 points = 9;" in proto and "int32 points_per_stage = 10;" in proto,
       "本季点数与每阶点数")
claim_req = proto.split("message ClaimSeasonPassRequest {")[1].split("}")[0]
report("proto_claim_request_unchanged",
       len(re.findall(r"^\s*\w+\s+\w+\s*=\s*\d+;", claim_req, re.M)) == 5, "领取请求仍是五个字段")
report("proto_reason_doc", "points_not_enough" in proto, "拒绝码写进注释")

sha = lambda p: hashlib.sha256(open(p, "rb").read()).hexdigest()
report("generated_both_sides",
       sha("src/Shared/Generated/Character.cs")
       == sha("client/MMOGame/Assets/Proto/Generated/Character.cs"), "两端生成物一致")

api = read("src/Gateway/PassApi.cs")
report("gateway_points", "points = reply.Points" in api
       and "points_per_stage = reply.PointsPerStage" in api
       and "required_points = r.RequiredPoints" in api and "unlocked = r.Unlocked" in api,
       "网关只透出服务端事实值")
report("gateway_query_unchanged",
       set(re.findall(r'Request\.Query\["([^"]+)"\]', api)) == {"token", "character_id"},
       "季 / 点数仍不从 query 读")

flags = read("tools/probe/CliProbe/CliFlags.cs")
parser = read("tools/probe/CliProbe/CliParser.cs")
dbcli = read("tools/probe/CliProbe/DbCli.cs")
entry = read("tools/probe/CliProbe/CliEntry.cs")
loaders = read("tools/probe/CliProbe/CliLoaders.cs")
report("cli_flags", all(k in flags for k in ("passProgressConfig", "passPointsOf",
                                            "passPointAwardOf", "passLedgerOf")), "共享状态声明")
report("cli_parser", "PassProgressCli.Handles" in parser and "PassProgressCli.Parse" in parser,
       "CliParser 一行分发")
report("cli_loaders", "LoadSeasonPassProgress" in loaders and "SeasonPassProgressTable.Load" in loaders,
       "只读入口与启动共用同一条加载链")
report("cli_dispatch", "PassProgressCli.TryRunDbAsync" in dbcli and "passPointsOf" in dbcli
       and "PassProgressCli.TryReadOnly" in entry, "连库 / 只读两条分发都在")

ci = read("tools/check/ci.sh")
allsh = read("tools/check/check-all.sh")
report("ci_registered", re.search(r"^CHECKS=.*\bq6\b", ci, re.M) is not None, "ci.sh 的 CHECKS 有 q6")
report("checkall_deps", re.search(r"q6\)\s*echo", allsh) is not None, "check-all.sh 的 deps_of 有 q6")
report("checkall_cards", re.search(r'ALL_CARDS="[^"]*\bq6\b', allsh, re.S) is not None,
       "ALL_CARDS 有 q6")

inv = read("docs/不变量清单.md")
report("invariants_doc", "| INV-22 |" in inv and "| INV-23 |" in inv, "清单有 INV-22 / INV-23")
probe = read("tools/probe/CliProbe/InvariantCli.cs")
report("invariants_probe", '"INV-22"' in probe and '"INV-23"' in probe, "探针里两条都在")

queue = read("docs/任务队列.md")
report("queue_row", re.search(r"\| \d+ \| 2 \| Q6 \|", queue) is not None
       and re.search(r"\| Q6 \|[^\n]*\| 2 \|", queue) is not None, "队列有 Q6 行且风险 2")
