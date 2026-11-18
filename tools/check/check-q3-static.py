#!/usr/bin/env python3
"""Q3/G1 的静态断言：日历唯一正本、五处时钟收口、没有第二套数据库日期函数。

由 check-q3.sh 调用，不单独使用；输出 PASS/FAIL 行给 shell 计数。
"""
import glob
import json
import os
import re
import sys

root = sys.argv[1]
os.chdir(root)


def read(path):
    return open(path, encoding="utf-8").read() if os.path.exists(path) else ""


def cfg(path):
    return json.loads(read(path)) if os.path.exists(path) else {}


def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + str(info)[:180]))


# 1. 日历正本
cal = cfg("config/calendar.json")
report("calendar_schema", cal.get("schema") == "calendar/2" and cal.get("version", 0) >= 2)
report("calendar_not_pending", cal.get("pending") is False)
report("calendar_keys", set(cal) == {
    "schema", "version", "pending", "note", "timezone",
    "daily_reset_hour", "weekly_reset", "season"})
report("calendar_timezone", isinstance(cal.get("timezone"), str) and "/" in cal["timezone"])
report("calendar_daily_hour", isinstance(cal.get("daily_reset_hour"), int)
       and 0 <= cal["daily_reset_hour"] <= 23)
report("calendar_weekly", isinstance(cal.get("weekly_reset"), dict)
       and set(cal["weekly_reset"]) == {"day", "hour"})
report("calendar_season", isinstance(cal.get("season"), dict)
       and set(cal["season"]) == {"start_date", "length_days"})

# 2. 共享库与三个读取方
cal_src = read("src/Shared.Domain/CalendarTable.cs")
report("calendar_shared_file", "public sealed class CalendarTable" in cal_src)
report("calendar_shared_api", all(x in cal_src for x in (
    "DayKey(", "WeekStartDate(", "WeekStartInstant(", "SeasonOf(", "FormatLocal(")))
report("calendar_config_loaders", all(x in read(p) for p, x in (
    ("src/Character/CharacterConfig.cs", 'DefaultCalendarTable = "config/calendar.json"'),
    ("src/Gateway/GatewayConfig.cs", 'DefaultCalendarTable = "config/calendar.json"'),
    ("src/Global/GlobalConfig.cs", 'DefaultCalendarTable = "config/calendar.json"'))))
report("calendar_boot_load", all(x in read(p) for p, x in (
    ("src/Character/CharacterProcess.cs", "CalendarTable.Load(config.CalendarTable)"),
    ("src/Gateway/GatewayProcess.cs", "CalendarTable.Load(config.CalendarTable)"),
    ("src/Global/GlobalProcess.cs", "CalendarTable.Load(config.CalendarTable)"))))

# 3. 角色服的日 / 周界只从日历算，SQL 里没有第二套日期函数
repos = {
    "entry": read("src/Character/DungeonEntryRepository.cs"),
    "hot": read("src/Character/HotSpringRepository.cs"),
    "weekly": read("src/Character/WeeklyShareRepository.cs"),
    "hard": read("src/Character/HardEntryRepository.cs"),
}
report("repos_take_calendar", all("CalendarTable calendar" in t for t in repos.values()))
report("entry_day_from_calendar", "calendar.DayKey(" in repos["entry"]
       and "fatigue_day <> $3" in repos["entry"] and "fatigue_day = $3" in repos["entry"])
report("hot_day_from_calendar", "calendar.DayKey(" in repos["hot"]
       and "fatigue_day <> $3" in repos["hot"] and "fatigue_day = $3" in repos["hot"])
report("weekly_from_calendar", "calendar.WeekStartDate(" in repos["weekly"]
       and "weekly_share_day <> $3" in repos["weekly"]
       and "weekly_share_day = $3" in repos["weekly"])
report("hard_from_calendar", "calendar.WeekStartInstant(" in repos["hard"]
       and "entered_at >= $3" in repos["hard"])

all_cs = {}
for path in glob.glob("src/**/*.cs", recursive=True):
    if "/bin/" in path or "/obj/" in path or "/Shared/Generated/" in path:
        continue
    all_cs[path] = read(path)
banned = re.compile(r"current_date|CURRENT_DATE|date_trunc", re.I)
hits = [p for p, t in all_cs.items() if banned.search(t)]
report("no_second_db_clock", not hits, ",".join(hits[:6]))

# 4. 展示口：世界 Boss 时刻与赛季边界从日历格式化
world_api = read("src/Gateway/WorldApi.cs")
season_api = read("src/Gateway/SeasonApi.cs")
report("boss_display_fields", all(x in world_api for x in (
    "display_timezone", "starts_at_local", "ends_at_local", "next_starts_at_local",
    "calendar.FormatLocal(")))
report("season_display_fields", all(x in season_api for x in (
    "season_index", "season_starts_at_local", "season_ends_at_local",
    "calendar.SeasonOf(")))
report("season_api_still_five_proto_fields",
       len(re.findall(r"^\s*\w+\s+\w+\s*=\s*\d+;",
                      read("proto/mmo/trade.proto").split(
                          "message DescribeSeasonResponse {")[1].split("}")[0], re.M)) == 5)
# Q11 起世界 Boss 的窗口时刻也按 config/calendar.json 的时区解释：Q3 留下的
# 展示字段（*_at_local）不变，出现时刻本身改由日历表折算，这里只钉住那条口径。
report("world_boss_table_follows_calendar",
       '"timezone": "calendar"' in read("config/world_boss.json")
       and "start_local" in read("config/world_boss.json"))

# 5. probe 只读入口与测试挂载
parser = read("tools/probe/CliProbe/CliParser.cs")
calendar_cli = read("tools/probe/CliProbe/CalendarCli.cs")
report("calendar_probe_wired", "--calendar" in parser and "CalendarCli.Parse" in parser
       and "calendar_at=" in calendar_cli)
report("calendar_probe_in_chain", "CalendarCli.TryReadOnly()" in read(
    "tools/probe/CliProbe/CliEntry.cs"))
ci = read("tools/check/ci.sh")
report("ci_registered", re.search(r"\bq3\b", ci) is not None)
allsh = read("tools/check/check-all.sh")
report("check_all_registered", "q3" in allsh and "ALL_CARDS" in allsh)
report("world_api_mounted", "WorldApi.Map(app, tokens, config, calendar)" in read(
    "src/Gateway/GatewayProcess.cs"))
report("season_api_mounted", "SeasonApi.Map(app, tokens, config, calendar)" in read(
    "src/Gateway/GatewayProcess.cs"))

# 6. 文档与台账同步
ledger = read("docs/数值台账.md")
report("ledger_calendar_green", "calendar/2 · v2" in ledger
       and "| `calendar.json` | 日 / 周 / 赛季重置时刻与时区；代码尚未读取" not in ledger)
report("ledger_pending_count", "### 2.1 整份暂取（顶层 `pending=true`，23 份）" in ledger)
design = read("docs/配置设计.md")
report("config_design_section", "## 14. calendar.json" in design
       and "calendar/2" in design and "--calendar" in design)
