#!/usr/bin/env python3
"""tools/check/check-q7-static.py —— Q7/H1 静态断言（check-q7.sh 第 3 段调用）。

约定与其它卡的静态段一致：逐条往 stdout 打 `PASS <名字>` / `FAIL <名字>  <原因>`，
由 shell 计数；退出码恒 0（除非脚本自身崩），红了看行不看码。

这一段的四组口径：
  1. 配置正本：人数上限、两个超时、队长继任、分配段只在这一份文件里；
  2. proto 与两端的接口面：请求里带不进人数 / 队长 / deadline；
  3. 队伍只在社交服内存：状态机不碰库、不读配置、不自己算时间；
  4. 角色服与网关：事实覆盖只在带账号的路径，降级只少给不多给。
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


def sha(p):
    try:
        return hashlib.sha256(open(p, "rb").read()).hexdigest()
    except Exception:
        return ""


# ---------------------------------------------------------------- 1. 配置正本
cfg = json.loads(read("config/party.json"))
report("config_schema", cfg.get("schema") == "party-table/1", cfg.get("schema"))
report("config_version", isinstance(cfg.get("version"), int) and cfg["version"] >= 2,
       cfg.get("version"))
report("config_pending", cfg.get("pending") is True,
       "邀请 / 准备 / 等级区间方案没写定，整体标待定")
report("config_keys",
       set(cfg) <= {"schema", "version", "pending", "note", "max_size", "invite_timeout_s",
                    "ready_check_timeout_s", "leader_succession", "loot", "level_range"},
       sorted(cfg))
# 方案 11 写定「小队是 4 人战斗单元」，这个数字的来源是方案正文，不是本卡的占位值。
report("config_max_size_4", cfg.get("max_size") == 4, cfg.get("max_size"))
report("config_invite_timeout", isinstance(cfg.get("invite_timeout_s"), int)
       and 1 <= cfg["invite_timeout_s"] <= 3600, cfg.get("invite_timeout_s"))
report("config_ready_timeout", isinstance(cfg.get("ready_check_timeout_s"), int)
       and 1 <= cfg["ready_check_timeout_s"] <= 600, cfg.get("ready_check_timeout_s"))
report("config_succession", cfg.get("leader_succession") in ("earliest_joined", "disband"),
       cfg.get("leader_succession"))
loot = cfg.get("loot") or {}
# Q8 起 loot 段多了掷点区间；这里只钉「没有第四种键混进来」，取值与区间归 check-q8。
report("config_loot_keys", set(loot) <= {"mode", "roll", "roll_min", "roll_max"}, sorted(loot))
report("config_loot_mode", loot.get("mode") in ("personal", "need_greed"), loot.get("mode"))
report("config_loot_roll", loot.get("roll") in ("none", "server"), loot.get("roll"))
report("config_personal_no_roll",
       not (loot.get("mode") == "personal" and loot.get("roll") != "none"),
       "个人份额不掷点；掷点只在服务端、只在 need_greed 下")
levels = cfg.get("level_range") or {}
report("config_level_keys", set(levels) <= {"enabled", "max_gap"}, sorted(levels))
report("config_level_gap", isinstance(levels.get("max_gap"), int) and levels["max_gap"] >= 1,
       levels.get("max_gap"))
# 运行期字段一律不进配置（第 6.1 节：静态定义在配置，可变状态在库 / 内存）。
runtime_keys = {"members", "member_ids", "ready", "party_id", "leader_character_id",
                "ready_deadline_ms", "all_ready", "invite_count"}
report("config_no_runtime_fields", not (runtime_keys & set(cfg)), sorted(runtime_keys & set(cfg)))

# ------------------------------------------------------------- 2. proto 接口面
proto = read("proto/mmo/social.proto")
for rpc in ("InviteParty", "RespondPartyInvite", "StartPartyReadyCheck", "SetPartyReady",
            "LeaveParty", "DisbandParty", "DescribeParty"):
    report("proto_rpc_" + rpc, re.search(r"rpc\s+" + rpc + r"\s*\(", proto) is not None, "缺 RPC")

party_response = re.search(r"message PartyResponse \{(.*?)\n\}", proto, re.S)
body = party_response.group(1) if party_response else ""
for field, num in (("party_id", 3), ("leader_character_id", 4), ("size", 5),
                   ("max_size", 6), ("all_ready", 7), ("ready_deadline_ms", 8),
                   ("members", 9), ("in_party", 11)):
    report("proto_field_" + field,
           re.search(r"\b" + field + r"\s*=\s*" + str(num) + r"\s*;", body) is not None,
           "PartyResponse 的 " + field + " 应为 = " + str(num))
report("proto_member_fact",
       re.search(r"message PartyMemberFact \{.*?uint64 character_id = 1;.*?bool is_leader = 2;"
                 r".*?bool ready = 3;.*?\}", proto, re.S) is not None,
       "成员事实只三条 + joined_seq")

# 请求里带不进人数 / 队长 / deadline（三个人数类消息都不许有这些字段）。
req_msgs = re.findall(r"message (Party\w*Request) \{(.*?)\n\}", proto, re.S)
report("proto_requests_present", len(req_msgs) == 4, [m[0] for m in req_msgs])
report("proto_requests_no_facts",
       all(not re.search(r"\b(max_size|size|all_ready|ready_deadline_ms|party_id)\b", b)
           for _, b in req_msgs),
       "请求里不许出现人数 / 上限 / deadline / 队伍编号")

# ------------------------------------------------------------ 3. 社交服内存态
runtime_src = read("src/Social/PartyRuntime.cs")
report("runtime_no_db", not re.search(r"Npgsql|dataSource|insert\s+into|update\s+[\"']|delete\s+from",
                                      runtime_src, re.I),
       "队伍只在进程内存，不碰任何 schema")
report("runtime_no_wall_clock", "DateTimeOffset.UtcNow" not in runtime_src,
       "状态机只接受注入的服务端时钟（惰性过期可测）")
report("runtime_no_file_read", "File.ReadAllText" not in runtime_src and "File.Exists" not in runtime_src,
       "状态机不读配置文件，上限只从 PartyTable 来")
report("runtime_uses_table_max", "table.MaxSize" in runtime_src
       and not re.search(r"MaxSize\s*=\s*4\b", runtime_src),
       "人数上限只从配置读，代码里没有第二个数字")
report("runtime_succession_by_seq",
       re.search(r"m\.JoinedSeq\s*<\s*heir\.JoinedSeq", runtime_src) is not None,
       "队长继任按加入顺序取最小")
report("runtime_ready_expiry",
       "ready_check_expired" in runtime_src and "ready_check_missing" in runtime_src,
       "没发起 / 过了时分开报")
report("runtime_invite_expiry", "invite_expired" in runtime_src and "invite_not_found" in runtime_src,
       "过期邀请与没有邀请分开报")
report("runtime_decline_reason", '"declined"' in runtime_src, "拒绝邀请是正常回包")

table_src = read("src/Social/PartyTable.cs")
report("table_validates_schema", "party-table/1" in table_src, "schema 硬校验")
report("table_optional_degrade",
       "LoadIfPresent" in table_src and "LoadIfPresent" in read("src/Social/SocialLoader.cs"),
       "缺表降级：老卡临时配置目录没有 party.json 也要能起服务")
report("table_rejects_unknown", "顶层有未知键" in table_src and "有未知键" in table_src,
       "未知键拒绝")
report("table_unlimited_claim_guard", "不许出现" in table_src and "无上限队伍" in table_src,
       "反口径词：人数上限不能被配置放开")

grpc_src = read("src/Social/SocialGrpcService.cs")
for rpc in ("InviteParty", "RespondPartyInvite", "StartPartyReadyCheck", "SetPartyReady",
            "LeaveParty", "DisbandParty", "DescribeParty"):
    report("grpc_impl_" + rpc,
           re.search(r"override\s+Task<PartyResponse>\s+" + rpc + r"\s*\(", grpc_src) is not None,
           "没实现")
report("grpc_party_unavailable", grpc_src.count("party_unavailable") >= 7,
       "缺表时七个队伍 RPC 都回 party_unavailable")
report("grpc_describe_no_account",
       re.search(r"DescribeParty\(\s*PartyRequest request.*?request\.CharacterId == 0", grpc_src, re.S)
       is not None,
       "只读入口不校验 account（服间直连没有账号）")

process_src = read("src/Social/SocialProcess.cs")
report("process_wires_runtime", "new PartyRuntime(partyTable)" in process_src
       and "AddSingleton(parties)" in process_src, "社交服装配内存队伍")
report("process_missing_party", 'party=missing' in process_src, "缺表时启动日志打 party=missing")
report("process_logs_config", "party_config=ok" in process_src, "启动日志与只读入口同形")

program_src = read("src/Social/Program.cs")
report("program_party_config", 'case "--party-config"' in program_src, "只读入口")
report("program_party_state", 'case "--party-state"' in program_src
       and "PartyProbe.Run" in program_src, "状态机自检入口")
report("program_party_table_override", 'case "--party-table"' in program_src, "坏表核对入口")

probe_src = read("src/Social/PartyProbe.cs")
report("probe_steps_cover_criteria",
       all(k in probe_src for k in ("party_full", "leader_succession", "invite_expired",
                                    "ready_expired", "respond_decline", "disband")),
       "完成标准的每条都有一步")

# --------------------------------------------------- 4. 角色服 / 网关的事实口径
directory_src = read("src/Character/PartyDirectory.cs")
report("directory_degrades_single", "catch (RpcException)" in directory_src
       and "Fact.Single" in directory_src, "社交服不可达按 1 人，只少给不多给")
report("directory_no_cache",
       "static readonly Dictionary" not in directory_src and "static Dictionary" not in directory_src,
       "不缓存队伍（散了要立刻读到散）")

char_src = read("src/Character/CharacterGrpcService.cs")
dungeon_block = re.search(r"Q7/H1：人数以社交服的\*\*队伍事实\*\*为准(.*?)人数超上限", char_src, re.S)
report("enter_uses_party_fact", dungeon_block is not None
       and "partyFact.Size" in (dungeon_block.group(1) if dungeon_block else "")
       and "partySize = partyFact.Size" in (dungeon_block.group(1) if dungeon_block else ""),
       "进场人数用事实覆盖")
report("enter_rejects_not_ready", "party_not_ready" in char_src, "全员未准备不许进本")
settle_block = re.search(r"Q7/H1：\*\*有队伍时\*\*(.*?)P3-1a2 补正", char_src, re.S)
settle_text = settle_block.group(1) if settle_block else ""
report("settle_fact_only_when_in_party", settle_block is not None
       and "account.Length > 0" in settle_text
       and re.search(r"if \(partyFact\.InParty\)", settle_text) is not None
       and "partySize = partyFact.Size" in settle_text,
       "结算只在「有队伍」时用事实：同场多人但没组队（世界 Boss）仍按同场人数")
report("settle_member_ids_from_fact", "settleMemberIds.AddRange(partyFact.MemberIds)" in char_src,
       "同门判定用事实成员")

gateway_src = read("src/Gateway/PartyApi.cs")
routes = ["/party", "/party/invite", "/party/respond", "/party/ready/start",
          "/party/ready", "/party/leave", "/party/disband"]
report("gateway_routes", all('"' + r + '"' in gateway_src for r in routes), routes)
# D10 起鉴权只写一处：这两处调用都走 GatewayAuth.AccountOf（管道里解析过一次的账号），
# 文件里不该再有第二份鉴权定义。
report("gateway_authorizes", gateway_src.count("GatewayAuth.AccountOf(") >= 2,
       gateway_src.count("GatewayAuth.AccountOf("))
report("gateway_owns", gateway_src.count("OwnsAsync(") >= 2, gateway_src.count("OwnsAsync("))
report("gateway_no_party_math",
       not re.search(r"MaxSize\s*[<>]|party_full|party_too_large", gateway_src),
       "网关不算人数、不判上限（只把 reply.MaxSize 原样透出）")
report("gateway_txn_server_side", "NewTxn(" in gateway_src
       and "Guid.NewGuid" in gateway_src, "事务号服务端生成")
gw_process = read("src/Gateway/GatewayProcess.cs")
report("gateway_registered", "PartyApi.Map(app, tokens, config)" in gw_process, "路由没注册")

# ------------------------------------------------------- 5. 生成物与脚本接线
shared = "src/Shared/Generated/Social.cs"
shared_grpc = "src/Shared/Generated/SocialGrpc.cs"
client = "client/MMOGame/Assets/Proto/Generated/Social.cs"
report("generated_shared_has_party", "InviteParty" in read(shared_grpc), shared_grpc)
# 客户端副本不一定在每台机器上（ubu 只同步 src/db/config/tools/proto/docs）：
# 没有就 SKIP，不把「本机没同步」当成生成物分叉。
if os.path.exists(client):
    report("generated_two_sides_equal", sha(shared) == sha(client) and sha(shared) != "",
           "两端生成物必须逐字节相同")
    meta = read(client + ".meta")
    guid = re.search(r"guid:\s*([0-9a-f]{32})", meta)
    report("generated_meta_guid_32", guid is not None, meta[:40])
else:
    out.write("SKIP generated_two_sides_equal  本机没有客户端副本（ubu 只同步服务端目录）\n")
    out.write("SKIP generated_meta_guid_32  本机没有客户端副本\n")
manifest = json.loads(read("proto/manifest.json")) if read("proto/manifest.json") else {}
inputs = json.dumps(manifest, ensure_ascii=False)
report("manifest_covers_social", "proto/mmo/social.proto" in inputs, "D16 清单要记这份 proto")

check_shell = read("tools/check/check-q7.sh")
report("check_sources_lib", "source" in check_shell and "lib-common.sh" in check_shell,
       "新脚本必须 source lib-common.sh")
report("check_budget", len(check_shell.splitlines()) <= 350,
       "行数 " + str(len(check_shell.splitlines())))
report("ci_has_q7", re.search(r"\bq7\b", read("tools/check/ci.sh")) is not None, "CI 没挂 q7")
check_all = read("tools/check/check-all.sh")
# Q7 不是客户端卡，只挂 deps_of 与 ALL_CARDS 两处（client_card_needs_unity 只给 U 卡）。
report("check_all_two_places", len(re.findall(r"\bq7\b", check_all)) >= 2,
       "deps_of / ALL_CARDS 两处都要挂")
