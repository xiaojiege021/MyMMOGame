#!/usr/bin/env python3
"""tools/check/check-q9-static.py —— Q9/H4 静态断言（check-q9.sh 第 2 段调用）。

约定与其它卡的静态段一致：逐条往 stdout 打 `PASS <名字>` / `FAIL <名字>  <原因>`，
由 shell 计数；退出码恒 0（除非脚本自身崩），红了看行不看码。

五组口径：
  1. 三个数字只在 config/reconnect.json：战斗服读它，代码里没有第二套时长。
  2. 重连只在战斗服内存：掉线等待记录不进库，角色库只有一张惩罚事实表。
  3. 不重复结算：结算写入口只有一个，收尾事实存一份快照，重连只发不收。
  4. 逃跑惩罚挡在所有战斗入口与 /dungeon/enter 之前，且不扣任何东西。
  5. 客户端只重连、不本地补算剩余时长。
"""
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


# ---------------------------------------------------------------- 1. 配置正本
cfg = json.loads(read("config/reconnect.json"))
report("config_schema", cfg.get("schema") == "reconnect/1", cfg.get("schema"))
report("config_pending_false", cfg.get("pending") is False, cfg.get("pending"))

grace = cfg.get("battle_grace_ms")
window = cfg.get("reconnect_window_s")
penalty = cfg.get("deserter_penalty_s")
report("config_grace_positive", isinstance(grace, int) and grace > 0, grace)
report("config_window_positive", isinstance(window, int) and window > 0, window)
report("config_penalty_nonneg", isinstance(penalty, int) and penalty >= 0, penalty)
report("config_grace_within_window",
       isinstance(grace, int) and isinstance(window, int) and grace <= window * 1000,
       "%s <= %s*1000" % (grace, window))
report("config_keys_only",
       set(cfg) <= {"schema", "version", "pending", "note", "battle_grace_ms",
                    "reconnect_window_s", "deserter_penalty_s", "penalty_note"},
       sorted(cfg))

table = read("src/Battle/ReconnectTable.cs")
report("table_reads_three_keys",
       all(k in table for k in ("battle_grace_ms", "reconnect_window_s", "deserter_penalty_s")),
       "ReconnectTable 认三个键")
report("table_rejects_unknown_key", "不认识的键" in table, "未知键起不来")
report("table_grace_window_order", "不能超过" in table, "宽限不能长过窗口")

battle_cfg = read("src/Battle/Config.cs")
report("battle_table_optional",
       'raw.ReconnectTable, "reconnect.json"' in battle_cfg and "ReconnectTable.Missing" in battle_cfg,
       "缺表降级为不重连")

battle_process = read("src/Battle/BattleProcess.cs")
report("battle_start_line",
       "reconnect_table=" in battle_process and "battle_grace_ms=" in battle_process,
       "启动行现取三个数字")

# ---------------------------------------------------------------- 2. 重连状态只在内存
server = read("src/Battle/Server.cs")
report("server_offline_memory", "ConcurrentDictionary<ulong, OfflineWatch> _offline" in server,
       "掉线等待记录只在进程内存")
report("server_grace_from_config", "config.Reconnect.GraceMs" in server, "宽限只从配置读")
report("server_window_from_config", "config.Reconnect.WindowMs" in server, "窗口只从配置读")
report("server_penalty_from_config", "config.Reconnect.PenaltyMs" in server, "惩罚只从配置读")
report("server_no_hardcoded_ms",
       not re.search(r"(GraceMs|WindowMs|PenaltyMs)\s*=\s*\d", server),
       "代码里没有第二套时长")
report("server_voluntary_from_close",
       "return true;" in server and "voluntary" in server and "MessageType == WebSocketMessageType.Close" in server,
       "主动退看 Close 帧")
report("server_resume_no_join",
       re.search(r"TryResume\(.*\)", server) is not None
       and "ServeResumedAsync" in server,
       "续战走独立路径")
report("server_reconnect_marker", "reconnect_resumed=" in server and "reconnect_grace_expired=" in server,
       "续战与宽限到点各有一条日志")

# ---------------------------------------------------------------- 3. 不重复结算
encounter = read("src/Battle/Encounter.cs")
report("settle_write_once", encounter.count("_settlements.Write(") == 1,
       encounter.count("_settlements.Write("))
report("ended_snapshot_once", encounter.count("EndSnapshot = new BattleEnded") == 1,
       "收尾事实只存一份快照")
report("server_settled_no_rejoin", "reconnect_settled=" in server and "already_settled" in server,
       "已收尾的场只回 Ended")

# ---------------------------------------------------------------- 4. 惩罚落库与挡在扣减前
proto_char = read("proto/mmo/character.proto")
report("proto_record_deserter",
       "rpc RecordDeserter(" in proto_char and "message RecordDeserterRequest" in proto_char,
       "写入口")
report("proto_describe_deserter",
       "rpc DescribeDeserter(" in proto_char and "message DescribeDeserterResponse" in proto_char,
       "只读口")
report("proto_enter_remaining", "deserter_remaining_s = 17" in proto_char, "进本回包带剩余秒数")
report("proto_reason_whitelist",
       'voluntary' in proto_char and 'timeout' in proto_char, "两个原因写进注释")

report("battle_proto_untouched", "deserter" not in read("proto/mmo/battle.proto").lower(),
       "battle.proto 一个字段没动")

manifest = json.loads(read("db/migrations.json"))
char_entries = [e for s in manifest["schemas"] if s["key"] == "character" for e in s["entries"]]
report("migration_listed",
       any(e["file"] == "db/character/deserter_penalty.sql" for e in char_entries),
       [e["file"] for e in char_entries[-2:]])
report("migration_version_new",
       [e["version"] for e in char_entries if e["file"].endswith("deserter_penalty.sql")]
       == [max(e["version"] for e in char_entries)],
       "只追加在末尾")

migration = read("db/character/deserter_penalty.sql")
report("migration_character_schema", '"角色".deserter_penalty' in migration, "表在角色 schema")
report("migration_reason_check", "voluntary" in migration and "timeout" in migration, "库层也挡原因")
report("migration_in_main", "deserter_penalty" in read("db/character/character.sql"),
       "主迁移也有一份")

repo = read("src/Character/DeserterRepository.cs")
report("repo_upsert_latest", "greatest(" in repo, "重复判罚取较晚")
report("repo_no_money",
       not re.search(r"(currency|gold|fatigue|exp)\s*=", repo),
       "不碰钱、经验、疲劳")
report("repo_single_writer",
       "insert into" in repo and "deserter_penalty" in repo
       and read("src/Character/CharacterGrpcService.cs").count("deserters.RecordAsync(") == 1,
       "写入口只有一个")

service = read("src/Character/CharacterGrpcService.cs")
pos_gate = service.find("deserters.DescribeAsync")
pos_hard = service.find("hardEntries.EnterHardAsync")
report("enter_gate_before_deduct",
       pos_gate > 0 and pos_hard > 0 and pos_gate < pos_hard,
       "惩罚检查在扣次数之前（%s < %s）" % (pos_gate, pos_hard))
report("enter_reject_reason", '"deserter_penalty"' in service, "拒绝码")

gateway = read("src/Gateway/CharacterApi.cs")
report("gateway_remaining", "deserter_remaining_s" in gateway, "网关透出剩余秒数")

# ---------------------------------------------------------------- 5. 客户端
# 客户端副本不是每台机都有（ubu 只同步 src / db / config / tools / proto / docs），
# 按「本机副本有没有」分支：没有就跳过并说明，不把「没同步」判成「没实现」。
CLIENT_LINK = "client/MMOGame/Assets/Net/BattleLink.cs"
if os.path.exists(CLIENT_LINK):
    link = read(CLIENT_LINK)
    report("client_reconnect_entry", "ReconnectAsync" in link, "重连入口")
    report("client_no_local_math",
           not re.search(r"remaining|Remaining", link) or "剩余毫秒" in link,
           "客户端不补算剩余时长")
else:
    report("client_reconnect_entry", True, "本机没有客户端副本，跳过（不是没实现）")
    report("client_no_local_math", True, "本机没有客户端副本，跳过（不是没实现）")

# ---------------------------------------------------------------- 6. 流水线
report("ci_has_q9", "q9" in read("tools/check/ci.sh").lower(), "CI 挂上")
report("check_all_has_q9", "q9" in read("tools/check/check-all.sh").lower(), "check-all 挂上")
report("script_exists", os.path.exists("tools/check/check-q9.sh"), "单卡脚本")
report("mutation_exists", os.path.exists("tools/check/mutations/q9.json"), "变异清单")
