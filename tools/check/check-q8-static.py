#!/usr/bin/env python3
"""tools/check/check-q8-static.py —— Q8/H2 静态断言（check-q8.sh 第 2 段调用）。

约定与其它卡的静态段一致：逐条往 stdout 打 `PASS <名字>` / `FAIL <名字>  <原因>`，
由 shell 计数；退出码恒 0（除非脚本自身崩），红了看行不看码。

四组口径：
  1. 分配规则只在 config/party.json：区间、模式、掷点方式；代码里没有第二套数字。
  2. proto 面：掷点只在服务端——意图消息里带不进点数，回包只带事实值。
  3. 分配只在战斗服本地结算做一次：角色服只入包，不自己选赢家；分配函数没有 leader。
  4. 客户端只发表态、不本地算分配。
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
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server", "CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World", "SectRepository.cs": "Sect", "SocialGrpcService.cs": "Grpc", "WorldGrpcService.cs": "Grpc", "Program.cs": "Cli"}
    _name = __import__("os").path.basename(p)
    if _name in _sub:
        # 14.8 拆分卡之后：主文件 + 分片目录一起读（断言文本不变，只改读法）。
        _os = __import__("os"); _g = __import__("glob")
        _b = p if _os.path.isabs(p) else _os.path.join(_os.getcwd(), p)
        if not _os.path.exists(_b):
            _d = _os.path.dirname(_os.getcwd())
            while _d and not _os.path.exists(_os.path.join(_d, p)):
                _nd = _os.path.dirname(_d)
                if _nd == _d: break
                _d = _nd
            _b = _os.path.join(_d, p)
        return "".join(open(f, encoding="utf-8").read() for f in
                       [_b] + sorted(_g.glob(_os.path.join(_os.path.dirname(_b), _sub[_name], "*.cs"))))
    try:
        return open(p, encoding="utf-8").read()
    except Exception:
        return ""


# ---------------------------------------------------------------- 1. 配置正本
cfg = json.loads(read("config/party.json"))
loot = cfg.get("loot") or {}
report("config_loot_keys", set(loot) <= {"mode", "roll", "roll_min", "roll_max"}, sorted(loot))
report("config_loot_mode", loot.get("mode") in ("personal", "need_greed"), loot.get("mode"))
report("config_loot_roll", loot.get("roll") in ("none", "server"), loot.get("roll"))
report("config_roll_min", isinstance(loot.get("roll_min"), int) and 1 <= loot["roll_min"] <= 1000,
       loot.get("roll_min"))
report("config_roll_max", isinstance(loot.get("roll_max"), int) and 1 <= loot["roll_max"] <= 1000,
       loot.get("roll_max"))
report("config_roll_order", isinstance(loot.get("roll_min"), int)
       and isinstance(loot.get("roll_max"), int) and loot["roll_min"] <= loot["roll_max"],
       "%s..%s" % (loot.get("roll_min"), loot.get("roll_max")))
report("config_personal_no_roll",
       not (loot.get("mode") == "personal" and loot.get("roll") != "none"),
       "个人份额不掷点")
report("config_pending_bool", isinstance(cfg.get("pending"), bool), cfg.get("pending"))

party_table = read("src/Social/PartyTable.cs")
report("table_loot_keys", all(k in party_table for k in ("roll_min", "roll_max")),
       "PartyTable 认四个 loot 键")
report("table_need_greed_requires_server",
       'mode == "need_greed" && roll != "server"' in party_table
       or ('need_greed' in party_table and 'must be server' in party_table),
       "need_greed 必须显式 server")
report("table_no_hardcoded_range",
       not re.search(r"RollMin\s*=\s*\d+", party_table),
       "区间从配置读，不在代码里给默认数字")

# ---------------------------------------------------------------- 2. proto 面
social = read("proto/mmo/social.proto")
for field in ("loot_mode", "loot_roll", "roll_min", "roll_max"):
    report("proto_party_" + field, field in social, field)
report("proto_party_fields_append_only",
       "loot_mode = 12" in social and "loot_roll = 13" in social,
       "只追加 12..15，不动 1..11")

character = read("proto/mmo/character.proto")
report("proto_loot_scope_rpc", "rpc DescribeLootScope(" in character, "角色服只读口径")
report("proto_loot_scope_request",
       re.search(r"message DescribeLootScopeRequest \{[^}]*uint64 character_id = 1;", character)
       is not None,
       "请求只有角色号")
report("proto_loot_scope_response",
       all(k in character for k in ("member_ids", "loot_mode", "roll_min", "roll_max")),
       "回包全是事实值")

battle = read("proto/mmo/battle.proto")
report("proto_loot_intent", "LootIntentRequest loot_intent =" in battle, "客户端表态入口")
intent_block = re.search(r"message LootIntentRequest \{([^}]*)\}", battle)
report("proto_intent_no_roll", intent_block is not None
       and "roll" not in intent_block.group(1) and "value" not in intent_block.group(1),
       "意图消息里带不进点数")
report("proto_loot_assigned", "LootAssigned loot_assigned =" in battle, "归属广播")
report("proto_intent_words",
       all(w in battle for w in ("need", "greed", "pass")), "只认三个表态词")

generated_server = read("src/Shared/Generated/Character.cs")
report("generated_describe_loot_scope", "DescribeLootScope" in generated_server,
       "服务端生成物已重出")
# 客户端只在有副本的机器上断言（跑核对的另一台机器可能没同步 client/）。
CLIENT = "client/MMOGame/Assets/Proto/Generated/Battle.cs"
generated_client = read(CLIENT)
report("generated_loot_assigned",
       generated_client == "" or "LootAssigned" in generated_client,
       "客户端生成物已重出（本机无副本时跳过）")

# ---------------------------------------------------------------- 3. 服务端分配
allocator = read("src/Battle/LootAllocator.cs")
report("allocator_exists", "class LootAllocator" in allocator, "纯函数分配")
report("allocator_no_leader", "leader" not in allocator.lower(),
       "队长没有特权（结构上没有这个入参）")
report("allocator_single_winner", "byMember[winner].Add(line)" in allocator,
       "每行只写进一个赢家")
report("allocator_range_from_scope", "scope.RollMin" in allocator and "scope.RollMax" in allocator,
       "区间从口径读")
report("allocator_no_party_json", "party.json" not in allocator,
       "战斗服不读 party.json（口径由角色服转出）")

encounter = read("src/Battle/Encounter.cs")
report("encounter_alloc_once", "LootAllocator.Allocate(" in encounter, "结算时分配一次")
report("encounter_intent_memory", "SetLootIntent" in encounter, "表态只在内存")
report("encounter_no_db", "Npgsql" not in encounter, "战斗服不碰库")

exp = read("src/Battle/ExpSettlement.cs")
report("settle_per_member", "IReadOnlyDictionary<ulong, IReadOnlyList<DropLineDef>>" in exp,
       "逐人入包（每人自己的那几行）")
report("scope_resolve", "DescribeLootScope" in exp, "结算前读口径")

char_svc = read("src/Character/CharacterGrpcService.cs")
report("char_subset_check", "MatchesReportedSubset(" in char_svc, "need_greed 允许表内子集")
report("char_loot_scope_rpc", "DescribeLootScope(" in char_svc, "角色服转社交服事实")

drop_table = read("src/Shared.Domain/DungeonDropTable.cs")
report("subset_exists", "public bool MatchesReportedSubset(" in drop_table, "子集匹配")
report("subset_rejects_dup", "seen.Add(line.ItemId)" in drop_table, "同一物品不许拆行")
report("strict_still_exists", "public bool MatchesReported(" in drop_table,
       "严格整份匹配保留（单测仍在用）")

social_svc = read("src/Social/SocialGrpcService.cs")
report("social_returns_loot", "LootMode = loot.Mode" in social_svc, "社交服给口径事实值")

directory = read("src/Character/PartyDirectory.cs")
report("directory_loot_fallback", "PartyLoot.Personal" in directory, "拿不到按 personal")

# ---------------------------------------------------------------- 4. 客户端与流水线
LINK = "client/MMOGame/Assets/Net/BattleLink.cs"
link = read(LINK)
report("client_intent", link == "" or "SendLootIntentAsync" in link,
       "客户端只发表态（本机无副本时跳过）")
report("client_no_roll", "RollValue" not in link and "Dice" not in link, "客户端不掷点")
report("client_no_allocator", "LootAllocator" not in link, "客户端不做分配")

ci = read("tools/check/ci.sh")
report("ci_q8", re.search(r"\bq8\b", ci) is not None, "CI 的 CHECKS 里有 q8")
check_all = read("tools/check/check-all.sh")
report("check_all_q8", re.search(r"\bq8\b", check_all) is not None, "check-all 挂了 q8")
