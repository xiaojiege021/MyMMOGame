"""Q11 静态断言：名额占位与两层容量。

只钉结构保证，不写死配置里的数字（cap、三个时长、窗口时刻都现取）。
FAIL 一行就说明这一条结构性口径破了。
"""
import json
import os
import re
import sys

ROOT = sys.argv[1]


def read(path):
    full = os.path.join(ROOT, path)
    return open(full, encoding="utf-8").read() if os.path.exists(full) else ""


def cfg(path):
    return json.loads(read(path))


def emit(key, ok, info=""):
    print(("PASS " if ok else "FAIL ") + key + ("" if ok else "  " + str(info)[:160]))


wb = cfg("config/world_boss.json")
hold = wb.get("hold", {})

# ---- 配置正本 ----
emit("wb_schema_v2", wb.get("schema") == "world-boss-table/2" and wb.get("version") == 2)
emit("wb_finalized",
     wb.get("pending") is False and "Q11 定稿" in wb.get("note", ""))
emit("wb_timezone_is_calendar", wb.get("timezone") == "calendar")
emit("wb_windows_local",
     len(wb["windows"]) >= 1
     and all(re.fullmatch(r"\d\d:\d\d", w["start_local"]) and "start_utc" not in w
             for w in wb["windows"]))
emit("wb_hold_three_numbers",
     isinstance(hold.get("hold_s"), int) and isinstance(hold.get("renew_s"), int)
     and isinstance(hold.get("lost_s"), int)
     and 5 <= hold["hold_s"] <= 600 and 5 <= hold["lost_s"] <= 600
     and 1 <= hold["renew_s"] <= hold["hold_s"]
     and hold["renew_s"] * 2 <= hold["lost_s"])
emit("wb_hold_note_present",
     "hold_note" in wb and "占位" in wb.get("hold_note", ""))
# 名额只跟位置有关：这张表里长不出奖励、属性、税收、矿脉。
keys = json.dumps(wb, ensure_ascii=False).lower()
emit("wb_no_reward_keys", not any(k in keys for k in (
    "reward", "drop", "loot", "gold", "spirit_stone", "vein", "tax",
    "owner_sect", "shop", "balance", "attribute", "power")))

# ---- 共享表：时刻只由日历表解释 ----
table = read("src/Shared.Domain/WorldBossTable.cs")
emit("table_window_takes_calendar", "WindowOf(DateTimeOffset now, CalendarTable calendar)" in table)
emit("table_uses_calendar_zone",
     "calendar.Zone" in table and "TimeZoneInfo.ConvertTime" in table)
emit("table_no_second_utc_day", "DateTimeOffset(utc.Year" not in table)
emit("table_parses_hold", "ParseHold" in table and "hold.renew_s 的两倍不能超过 lost_s" in table)
emit("table_rejects_old_timezone", 'string.Equals(timezone, "calendar"' in table)
emit("table_pure_math", "StartSeconds" in table and "LocalToUtc" in table)

# ---- 世界服：名额只在进程内存 ----
slots = read("src/World/WorldBossSlots.cs")
emit("slots_exist", "public sealed class WorldBossSlots" in slots)
emit("slots_memory_only",
     not re.search(r"insert into|update \"|delete from|Npgsql", slots, re.I))
emit("slots_cap_from_table", "table.Cap" in slots and not re.search(r"cap\s*==\s*\d+", slots))
emit("slots_three_layers",
     "worldboss_closed" in slots and "worldboss_not_on_entrance_line" in slots
     and "worldboss_full" in slots and "worldboss_line_full" in slots)
emit("slots_hold_renew_release",
     all(x in slots for x in ("public SlotOutcome Hold", "public SlotOutcome Renew",
                              "public SlotOutcome Release")))
emit("slots_expiry_by_clock", "ExpiresAtMs <= nowMs" in slots and "Sweep" in slots)
emit("slots_line_from_probe", "IEntranceLine" in slots and "LineStanding" in slots)
emit("slots_no_reward_fields", not any(k in slots.lower() for k in (
    "reward", "gold", "vein", "tax", "attribute", "spirit_stone")))

# 运行期复核开放地表：入口状态机每次都现算，不在加载期缓存一个「开着」。
ent = read("src/World/WorldBossEntrance.cs")
emit("entrance_recomputed_each_call", "table.WindowOf(now, calendar)" in ent)
gates = read("src/World/RegionGate.cs")
emit("region_gate_is_single_judge", "class RegionGate" in gates)

# ---- gRPC 面 ----
proto = read("proto/mmo/world.proto")
emit("proto_slot_rpcs", all(x in proto for x in (
    "rpc HoldWorldBossSlot(", "rpc RenewWorldBossSlot(", "rpc ReleaseWorldBossSlot(")))
resp = proto.split("message DescribeWorldBossResponse {")[1].split("}")[0]
emit("proto_describe_slot_fields", all(x in resp for x in (
    "uint32 current", "uint32 remaining", "uint32 hold_s", "uint32 renew_s", "uint32 lost_s")))
emit("proto_hold_request_only_character",
     "message HoldWorldBossSlotRequest {" in proto
     and proto.split("message HoldWorldBossSlotRequest {")[1].split("}")[0].strip()
     == "uint64 character_id = 1;")
grpc = read("src/World/WorldGrpcService.cs")
emit("grpc_overrides_slots", all(x in grpc for x in (
    "HoldWorldBossSlot(", "RenewWorldBossSlot(", "ReleaseWorldBossSlot(")))
emit("grpc_describe_reads_slots", "worldBossSlots.Describe(" in grpc)

# ---- 战斗服 ----
srv = read("src/Battle/Server.cs")
emit("battle_holds_slot", "worldBossSink.Hold(charId)" in srv)
emit("battle_confirms_on_enter", "worldBossSink.Renew(charId, entered: true)" in srv)
emit("battle_releases_on_leave", 'worldBossSink.Release(charId, "leave")' in srv)
emit("battle_release_on_reject", 'worldBossSink.Release(charId, "rejected")' in srv)
emit("battle_renew_loop", "WorldBossRenewLoopAsync" in srv and "config.WorldBoss.Hold.RenewSeconds" in srv)
emit("battle_reason_passthrough", 'Reason = slot.Reason' in srv)
emit("battle_degrades_when_down", "slot.UpstreamDown" in srv)
emit("battle_cap_still_config", "table.Cap" in srv and not re.search(r"cap\s*==\s*\d{2}\b", srv))
client = read("src/Battle/WorldBossClient.cs")
emit("client_three_slot_calls", all(x in client for x in (
    "HoldWorldBossSlotRequest", "RenewWorldBossSlotRequest", "ReleaseWorldBossSlotRequest")))
emit("client_degraded_reasons",
     'UpstreamDown => Reason is "upstream_off" or "upstream_error"' in client)

# ---- 网关与客户端 ----
api = read("src/Gateway/WorldApi.cs")
emit("gateway_slot_fields", all(x in api for x in (
    "current = result.Current", "remaining = result.Remaining",
    "hold_s = result.HoldS", "renew_s = result.RenewS", "lost_s = result.LostS")))
# 客户端副本不是每台机都有（ubu 是 rsync 的服务端副本）：没有就明说跳过，
# 不当成失败，也不假装验过。
entry_path = os.path.join(ROOT, "client/MMOGame/Assets/Net/WorldBossEntry.cs")
if os.path.exists(entry_path):
    entry = read("client/MMOGame/Assets/Net/WorldBossEntry.cs")
    emit("client_reads_slot_fields", all(x in entry for x in (
        'ReadLong(json, "current")', 'ReadLong(json, "remaining")',
        'ReadLong(json, "hold_s")', 'ReadLong(json, "renew_s")',
        'ReadLong(json, "lost_s")')))
    emit("client_does_not_count_itself",
         "cap - current" not in entry and "Remaining -" not in entry)
else:
    print("PASS client_copy_absent  本机没有 client/MMOGame，客户端两条在 Mac 上跑")

# ---- 流水线 ----
allsh = read("tools/check/check-all.sh")
emit("check_all_has_q11", "q11" in allsh)
ci = read("tools/check/ci.sh")
emit("ci_has_q11", "q11" in ci)
emit("world_config_calendar", "calendar_table" in read("src/World/WorldConfig.cs"))

# 老断言同步：P3-5a / Q3 不再钉 utc 语义。
p35 = read("tools/check/check-p3-5a.sh")
emit("p35_follows_new_schema", "world-boss-table/2" in p35 and "start_local" in p35)
q3 = read("tools/check/check-q3-static.py")
emit("q3_follows_calendar", "world_boss_table_follows_calendar" in q3)
