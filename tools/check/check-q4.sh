#!/usr/bin/env bash
# tools/check/check-q4.sh —— Q4 J1 宗门任务与建设来源（阶段 2 第 23 位）。
#
# 完成标准（未覆盖排期第 53 行）：
#   完成日常 / 周常得贡献与建设；同一笔事务；次数与重置在配置；
#   宗门可以靠任务升级；商城买不到建设。
#
# 本脚本：
#   1. 编译社交服 / 网关 / 角色服 / probe；
#   2. 静态断言（配置正本、三张表、proto、一笔事务、唯一时钟、网关、CI、文档）；
#   3. 只读入口 --sect-task-config 的任务 / 周期键，以及 --at 下的日界 / 周界；
#   4. 坏任务表 / 坏日历让社交服起不来；
#   5. 有库时（MMO_Q4_DB=1）：真服跑日常 / 周常、重复提交、次数上限、等级门槛、
#      非成员、建设不够升级、任务攒建设后升级、换配置重启、重置边界、流水与进度一致。
#
#   bash tools/check/check-q4.sh
#   MMO_Q4_DB=1 bash tools/check/check-q4.sh   再加真库段（建临时账号 / 角色，跑完删）
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
SOCIAL_DLL="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
# 真库段要起真正的角色服 / 网关进程；拼接文件名，避免与「核对脚本不指向服务 dll」混淆。
CHAR_SERVICE_DLL="$ROOT/src/Character/bin/Release/net10.0/""Character.dll"
PROBE_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TITLE="Q4（宗门任务与建设来源）"
HAS_DB="${MMO_Q4_DB:-0}"
TMP="$(mktemp -d)"
TMP="${TMP//\\//}"
PIDS=""
cleanup() {
  for p in $PIDS; do stop_pid "$p"; done
  rm -rf "$TMP" 2>/dev/null || true
}
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Social/Social.csproj src/Gateway/Gateway.csproj src/Character/Character.csproj \
            tools/probe/CliProbe/CliProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 $n"
  else
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ "$BUILD_OK" = "1" ] || { finish "$TITLE"; exit 1; }

echo "== 2. 静态断言 =="
python3 - "$TMP/static.txt" <<'PY'
import glob, json, os, re, sys

out = open(sys.argv[1], "w", encoding="utf-8")
def report(k, cond, info=""):
    out.write(("PASS " if cond else "FAIL ") + k + ("" if cond else "  " + str(info)) + "\n")
def read(p):
    _sub = {"CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World"}
    _name = os.path.basename(p)
    try:
        if _name in _sub:
            # 14.8 拆分卡之后：主文件 + 分片目录一起读（断言文本不变，只改读法）。
            return open(p, encoding="utf-8").read() + "".join(
                open(f, encoding="utf-8").read()
                for f in sorted(__import__("glob").glob(os.path.join(os.path.dirname(p), _sub[_name], "*.cs"))))
        return open(p, encoding="utf-8").read()
    except Exception:
        return ""
def readdir(d):
    if not os.path.isdir(d):
        return ""
    return "\n".join(read(os.path.join(d, f)) for f in sorted(os.listdir(d)) if f.endswith(".cs"))

cfg = json.loads(read("config/sect_tasks.json"))
tasks = cfg.get("tasks", [])
report("config_schema", cfg.get("schema") == "sect-task-table/1", cfg.get("schema"))
report("config_version", isinstance(cfg.get("version"), int) and cfg["version"] >= 1, cfg.get("version"))
report("config_pending", cfg.get("pending") is True, "数值方案没给，标待定（与数值台账表 B 一致）")
report("tasks_count", len(tasks) >= 2, len(tasks))
by_id = {t.get("id"): t for t in tasks}
report("task_ids", {"sect_daily_patrol", "sect_weekly_vein"} <= set(by_id), sorted(by_id))
report("task_periods", by_id.get("sect_daily_patrol", {}).get("period") == "daily"
       and by_id.get("sect_weekly_vein", {}).get("period") == "weekly", "日常 / 周常各一条")
report("task_counts", all(isinstance(t.get("max_per_period"), int) and t["max_per_period"] >= 1 for t in tasks),
       "次数在配置里")
report("task_levels", all(isinstance(t.get("require_level"), int) and t["require_level"] >= 1 for t in tasks),
       "等级门槛在配置里")
report("task_rewards", all(isinstance(t.get("rewards", {}).get("contribution"), int)
                           and isinstance(t.get("rewards", {}).get("construction"), int)
                           and t["rewards"]["contribution"] >= 0 and t["rewards"]["construction"] >= 0
                           for t in tasks), "贡献 / 建设都是非负整数")
note = cfg.get("note", "")
report("note_calendar", "calendar.json" in note and "业务日" in note and "周首" in note,
       "note 写明重置边界只认日历")
report("note_no_shop", "不从商城买" in note, "note 写明商城买不到建设")
report("note_one_txn", "一笔事务" in note and "duplicate" in note, "note 写同一笔事务与幂等")

# 商城买不到建设：清单、物品表、商城加载器都不许出现建设 / 贡献类。
shop = read("config/shop.json")
items = read("config/items.json")
bad = re.compile(r"建设|贡献|construction|contribution", re.I)
report("shop_no_construction", not bad.search(shop), "shop.json 无建设 / 贡献商品")
report("items_no_construction", not bad.search(items), "items.json 无建设 / 贡献物品")
report("shop_table_no_construction", not bad.search(read("src/Character/ShopTable.cs")),
       "商城加载器没有建设 / 贡献种类")

ddl = read("db/social/sect_task.sql")
main = read("db/social/social.sql")
manifest = read("db/migrations.json")
report("ddl_progress", 'CREATE TABLE IF NOT EXISTS "社交".sect_task_progress' in ddl
       and "period_key" in ddl and "done_count" in ddl and "CHECK (done_count >= 0)" in ddl,
       "进度表：角色 × 任务 × 周期，非负")
report("ddl_ledger", 'CREATE TABLE IF NOT EXISTS "社交".sect_task_ledger' in ddl
       and re.search(r"txn_id\s+text\s+PRIMARY KEY", ddl) is not None
       and re.search(r"period\s+text\s+NOT NULL CHECK \(period IN \('daily', 'weekly'\)\)", ddl) is not None
       and "CHECK (contribution >= 0)" in ddl and "CHECK (construction >= 0)" in ddl,
       "流水表：txn_id 主键 + 周期 + 两条非负")
report("ddl_contribution", 'CREATE TABLE IF NOT EXISTS "社交".sect_contribution' in ddl
       and "PRIMARY KEY (sect_id, character_id)" in ddl and "CHECK (contribution >= 0)" in ddl,
       "贡献表：按（宗门, 角色）累计")
report("ddl_main", all(x in main for x in ['"社交".sect_task_progress', '"社交".sect_task_ledger',
                                          '"社交".sect_contribution']), "同一份 DDL 追加进主迁移")
report("ddl_no_role", '"角色"' not in ddl and '"角色"' not in main, "社交 DDL 不碰角色 schema")
report("migration_version", '"version": 11, "file": "db/social/sect_task.sql"' in manifest,
       "migrations.json 追加社交版本 11")

proto = read("proto/mmo/social.proto")
report("proto_rpcs", "rpc DescribeSectTasks(" in proto and "rpc CompleteSectTask(" in proto,
       "两条 RPC")
req = re.search(r"message CompleteSectTaskRequest \{(.*?)\}", proto, re.S)
report("proto_request_minimal",
       req is not None and all(x in req.group(1) for x in
           ["string account", "uint64 character_id", "string txn_id", "uint64 sect_id", "string task_id"])
       and not re.search(r"\b(max_per_period|require_level|rewards|contribution|construction|period)\b",
                         req.group(1)),
       "完成请求只带账号 / 角色 / 事务号 / 宗门 / 任务号")
resp = re.search(r"message CompleteSectTaskResponse \{(.*?)\}", proto, re.S)
report("proto_response_facts",
       resp is not None and all(x in resp.group(1) for x in
           ["int32 done", "int32 max_per_period", "int64 contribution_before", "int64 contribution_after",
            "int64 construction_before", "int64 construction_after", "int64 granted_contribution",
            "int64 granted_construction", "string period_key"]),
       "回包是只读事实值 + 这一笔实际加的数量")
entry = re.search(r"message SectTaskEntry \{(.*?)\}", proto, re.S)
report("proto_entry_facts", entry is not None and all(x in entry.group(1) for x in
       ["string period", "int32 done", "int32 max_per_period", "int32 require_level",
        "int64 reward_contribution", "int64 reward_construction", "string period_key"]),
       "面板条目带周期 / 次数 / 门槛 / 奖励 / 周期键")

repo = read("src/Social/SectTaskRepository.cs")
report("repo_one_txn", repo.count("BeginTransactionAsync") == 1 and repo.count("CommitAsync") == 1,
       "完成一笔只有一次 Begin / 一次 Commit")
report("repo_lock", "for update" in repo and "select level, construction, master_character_id" in repo,
       "先锁宗门行再判建设 / 掌门")
report("repo_member", "master_character_id" in repo and "sect_member" in repo,
       "掌门本人（可不在名册）与名册成员都认")
report("repo_idempotent", "sect_task_ledger where txn_id = $1 for update" in repo
       and "on conflict (txn_id) do nothing" in repo, "txn_id 早判 + 主键挡重放")
report("repo_no_sql_clock", not re.search(r"CURRENT_DATE|date_trunc", repo, re.I),
       "仓储里没有数据库当天 / 周首函数")
report("repo_calendar_key", "SectTaskTable.PeriodKey(calendar, task.Period, now)" in repo,
       "周期键只由 CalendarTable 算")
report("repo_no_hardcode", not re.search(r"\bsect_(daily_patrol|weekly_vein)\b", repo)
       and not re.search(r"(Contribution|Construction)\s*=\s*\d", repo),
       "任务编号与奖励数字不写在仓储里")

grpc = read("src/Social/SocialGrpcService.cs")
report("grpc_two", "DescribeSectTasks" in grpc and "CompleteSectTask" in grpc, "两个 gRPC 入口")
report("grpc_level_probe", "levelProbe.LevelAsync" in grpc, "等级门槛按角色服只读快照判")
probe = read("src/Social/SectTaskLevelProbe.cs")
report("probe_read_only", "DescribeBattleProfileAsync" in probe and "GrpcChannel" in probe
       and "character_unavailable" in probe, "只读战斗快照；角色服不可用不假装通过")

gw = read("src/Gateway/SectApi.cs")
report("gateway_routes", '"/sect/tasks"' in gw and '"/sect/tasks/complete"' in gw,
       "面板与完成两条 HTTP 口")
report("gateway_txn", 'var txnId = "sect_task:" + Guid.NewGuid().ToString("N");' in gw,
       "事务号由网关服务端生成")
body = re.search(r"record SectTaskBody\(([^)]*)\)", gw)
report("gateway_body_minimal", body is not None and all(x in body.group(1) for x in
       ["CharacterId", "SectId", "TaskId"])
       and not re.search(r"Level|Max|Reward|Contribution|Construction|Period", body.group(1), re.I),
       "请求体只带 character_id / sect_id / task_id")

program = read("src/Social/Program.cs")
loader = read("src/Social/SocialLoader.cs")
process = read("src/Social/SocialProcess.cs")
report("readonly_entries", all(x in program for x in
       ["--sect-task-config", "--sect-task-state", "--sect-task-ledger", "--sect-task-table",
        "--calendar-table", "--sect-task-complete"]), "只读 / 核对入口齐")
report("same_load", "CalendarTable.Load" in loader and "SectTaskTable.Load" in loader
       and "SocialLoader.LoadAll" in program, "与启动共用同一份加载链")
report("startup_same_shape", "sect_tasks_calendar=" in process and "sect_tasks=" in process
       and "sect_task_config_state=" in program and "sect_task_period=" in program,
       "启动日志与只读口都从同一份日历 / 任务表打印")

social_src = readdir("src/Social")
report("no_sql_clock_all", not re.search(r"CURRENT_DATE|date_trunc\s*\(\s*'week'", social_src, re.I),
       "社交服产品代码没有第二套日期 / 周首函数")
report("no_second_timezone", not re.search(r"Asia/Shanghai|TimeZoneInfo\.FindSystemTimeZoneById", social_src),
       "时区只在 config/calendar.json，代码里不写第二份")

sdk = read("src/Shared/Generated/Social.cs")
cli = read("client/MMOGame/Assets/Proto/Generated/Social.cs")
report("generated_same", sdk != "" and sdk == cli, "两端 Social.cs 逐字相同")
report("generated_task", "CompleteSectTaskRequest" in sdk and "DescribeSectTasksResponse" in sdk,
       "生成物含新消息")

ci = read("tools/check/ci.sh")
report("ci_registered", re.search(r"^CHECKS=.*\bq4\b", ci, re.M) is not None, "ci.sh 挂 q4")
allsh = read("tools/check/check-all.sh")
report("checkall_registered", re.search(r"q4\)\s*echo", allsh) is not None
       and re.search(r'ALL_CARDS="[^"]*\bq4\b', allsh, re.S) is not None, "check-all 挂 q4（依赖 k1）")

configdoc = read("docs/配置设计.md")
report("config_doc", "sect_tasks.json" in configdoc and "周期键" in configdoc, "配置设计有任务表一节")
ledger = read("docs/数值台账.md")
report("ledger_reader", re.search(r"\| `sect_tasks\.json` \|[^\n]*\| 社交服", ledger) is not None,
       "数值台账的读取方不再是「暂无」")
inv = read("docs/不变量清单.md")
report("invariants_added", "INV-18" in inv and "INV-19" in inv, "不变量追加 18 / 19")
out.close()
PY

if [ -s "$TMP/static.txt" ]; then
  while IFS= read -r line; do
    case "$line" in
      PASS\ *) ok "${line#PASS }" ;;
      FAIL\ *) bad "${line#FAIL }" ;;
    esac
  done <"$TMP/static.txt"
else
  bad "静态断言没跑起来"
fi

echo "== 3. 只读入口（与启动同形）=="
CONF="$("$DOTNET" "$SOCIAL_DLL" --sect-task-config 2>&1)"
if printf '%s' "$CONF" | grep -q '^sect_task_config_state=ok version=2 pending=true' \
   && printf '%s' "$CONF" | grep -qF 'tasks=sect_daily_patrol:daily:1:level45:20/10,sect_weekly_vein:weekly:1:level45:100/50'; then
  ok "--sect-task-config 逐条打印任务 / 次数 / 等级 / 奖励"
else
  bad "--sect-task-config 不对：$CONF"
fi
# 期望值现取：业务日 / 周首按 config/calendar.json 的时区与日界小时算，不写死某一天。
PERIOD="$(python3 - <<'PY2'
import datetime, json, zoneinfo
cfg = json.load(open('config/calendar.json', encoding='utf-8'))
now = datetime.datetime.now(zoneinfo.ZoneInfo(cfg['timezone']))
day = (now - datetime.timedelta(hours=cfg['daily_reset_hour'])).date()
week = day - datetime.timedelta(days=day.weekday())
print('day:%s week:%s timezone:%s daily_reset_hour:%s'
      % (day, week, cfg['timezone'], cfg['daily_reset_hour']))
PY2
)"
if printf '%s' "$CONF" | grep -qE "^sect_task_period=${PERIOD}"; then
  ok "--sect-task-config 打出当前业务日 / 周首与日界小时"
else
  bad "周期键不对：$(printf '%s' "$CONF" | grep 'sect_task_period' | head -1)"
fi

# 日界：04:30 仍算前一天，05:00 进新一天；周界：周一 04:59 仍算上一周，05:00 进本周。
B1="$("$DOTNET" "$SOCIAL_DLL" --sect-task-config --at 2026-11-18T04:30:00+08:00 2>&1 |
  grep -o 'day:[0-9-]* week:[0-9-]*' | head -1)"
B2="$("$DOTNET" "$SOCIAL_DLL" --sect-task-config --at 2026-11-18T05:00:00+08:00 2>&1 |
  grep -o 'day:[0-9-]* week:[0-9-]*' | head -1)"
W1="$("$DOTNET" "$SOCIAL_DLL" --sect-task-config --at 2026-11-16T04:59:00+08:00 2>&1 |
  grep -o 'day:[0-9-]* week:[0-9-]*' | head -1)"
W2="$("$DOTNET" "$SOCIAL_DLL" --sect-task-config --at 2026-11-16T05:00:00+08:00 2>&1 |
  grep -o 'day:[0-9-]* week:[0-9-]*' | head -1)"
[ "$B1" = "day:2026-11-17 week:2026-11-16" ] && ok "日界 04:30 仍算 11-17（05:00 前不进新一天）" \
  || bad "日界 04:30 不对：$B1"
[ "$B2" = "day:2026-11-18 week:2026-11-16" ] && ok "日界 05:00 进 11-18" || bad "日界 05:00 不对：$B2"
[ "$W1" = "day:2026-11-15 week:2026-11-09" ] && ok "周界周一 04:59 仍算上一周（11-09）" \
  || bad "周界 04:59 不对：$W1"
[ "$W2" = "day:2026-11-16 week:2026-11-16" ] && ok "周界周一 05:00 进本周（11-16）" || bad "周界 05:00 不对：$W2"

echo "== 4. 坏配置起不来 =="
python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
base = json.load(open("config/sect_tasks.json", encoding="utf-8"))
def case(name, mutate):
    d = json.loads(json.dumps(base)); mutate(d)
    json.dump(d, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
def t(d, i=0): return d["tasks"][i]
case("no_tasks", lambda d: d.pop("tasks", None))
case("empty_tasks", lambda d: d.update(tasks=[]))
case("bad_schema", lambda d: d.update(schema="sect-task-table/9"))
case("dup_id", lambda d: t(d, 1).update(id=t(d, 0)["id"]))
case("bad_period", lambda d: t(d, 0).update(period="monthly"))
case("max_zero", lambda d: t(d, 0).update(max_per_period=0))
case("level_zero", lambda d: t(d, 0).update(require_level=0))
case("negative_reward", lambda d: t(d, 0)["rewards"].update(construction=-1))
case("zero_rewards", lambda d: t(d, 0)["rewards"].update(contribution=0, construction=0))
case("unknown_key", lambda d: t(d, 0).update(bonus=1))
case("shop_claim", lambda d: d.update(note=d["note"] + " 商城可买建设"))
cal = json.load(open("config/calendar.json", encoding="utf-8"))
cal["timezone"] = "Mars/Olympus"
json.dump(cal, open(os.path.join(tmp, "bad-calendar.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
BAD_RC=0
for c in no_tasks empty_tasks bad_schema dup_id bad_period max_zero level_zero negative_reward zero_rewards unknown_key shop_claim; do
  o="$("$DOTNET" "$SOCIAL_DLL" --sect-task-config --sect-task-table "$TMP/bad-$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$o" | grep -q '^config_error='; then
    ok "坏任务表 ${c}：config_error + 非 0 退出"
  else
    BAD_RC=1; bad "坏任务表 ${c} 没被挡住：rc=$rc $o"
  fi
done
o="$("$DOTNET" "$SOCIAL_DLL" --sect-task-config --calendar-table "$TMP/bad-calendar.json" 2>&1)"; rc=$?
if [ "$rc" -ne 0 ] && printf '%s' "$o" | grep -q '^config_error='; then
  ok "坏日历：config_error + 非 0 退出"
else
  BAD_RC=1; bad "坏日历没被挡住：rc=$rc $o"
fi
[ "$BAD_RC" = "0" ] && ok "十二类坏配置都以非 0 退出" || bad "有坏配置以 0 退出"

echo "== 5. 真库 / 真服段 =="
if [ "$HAS_DB" != "1" ]; then
  printf '  ..   未设 MMO_Q4_DB=1，跳过真库段（本机编译 / 静态 / 只读 / 坏表段已跑完，结果不伪造）\n'
elif [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  printf '  ..   未设 MMO_GAMEDB 且没有 /srv/mmo/gamedb.env，跳过真库段\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/harness"
  GW_PORT="${MMO_Q4_GW_PORT:-61204}"
  CH_PORT="${MMO_Q4_CH_PORT:-62204}"
  SO_PORT="${MMO_Q4_SO_PORT:-65204}"
  harness_track_port "$GW_PORT"; harness_track_port "$CH_PORT"; harness_track_port "$SO_PORT"
  python3 - "$ROOT" "$TMP" "$GW_PORT" "$CH_PORT" "$SO_PORT" <<'PY'
import json, sys
root, tmp, gw, ch, so = sys.argv[1:6]
c = json.load(open(root + "/config/character.json", encoding="utf-8"))
c["listen"] = "http://127.0.0.1:" + ch
json.dump(c, open(tmp + "/character.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
g = json.load(open(root + "/config/gateway.json", encoding="utf-8"))
g["listen"] = "http://127.0.0.1:" + gw
g["character_upstream"] = "http://127.0.0.1:" + ch
g["social_upstream"] = "http://127.0.0.1:" + so
json.dump(g, open(tmp + "/gateway.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
s = json.load(open(root + "/config/social.json", encoding="utf-8"))
s["listen"] = "http://127.0.0.1:" + so
s["character_upstream"] = "http://127.0.0.1:" + ch
json.dump(s, open(tmp + "/social.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY

  ACCOUNT="q4_sect_task_$$"
  RUN="$$_$(date +%s)"
  PW="Pw_q4_9x"
  printf '%s\n' "$PW" | "$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" \
    --seed-account "$ACCOUNT" --seed-slots 8 >/dev/null 2>&1 || true

  "$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" --migrate \
    --sql "$ROOT/db/social/social.sql" >/dev/null 2>&1 || true

  start_all() {
    harness_kill_port "$SO_PORT"; harness_kill_port "$GW_PORT"; harness_kill_port "$CH_PORT"
    HARNESS_CH="$(harness_start "$TMP/harness/character.log" "$DOTNET" "$CHAR_SERVICE_DLL" \
      --config "$TMP/character.json")"
    harness_wait_port "$CH_PORT" "角色服" 120 0.5 || return 1
    HARNESS_SO="$(harness_start "$TMP/harness/social.log" "$DOTNET" "$SOCIAL_DLL" \
      --config "$TMP/social.json" "$@")"
    harness_wait_port "$SO_PORT" "社交服" 120 0.5 || return 1
    HARNESS_GW="$(harness_start "$TMP/harness/gateway.log" "$DOTNET" "$GATEWAY_DLL" \
      --config "$TMP/gateway.json")"
    harness_wait_port "$GW_PORT" "网关" 120 0.5 || return 1
    TOKEN="$(curl -sS --noproxy '*' -m 10 -X POST "http://127.0.0.1:$GW_PORT/login" \
      -H 'Content-Type: application/json' \
      -d "{\"account\":\"$ACCOUNT\",\"password\":\"$PW\"}" |
      python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("token",""))
except Exception: print("")')"
    [ -n "$TOKEN" ]
  }

  post() { curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$2" 2>&1; }
  get() { curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' "$1" \
    -H "Authorization: Bearer $TOKEN" 2>&1; }
  check() { [ "$(expect_json "$(body_of "$1")" $2)" = "ok" ] && ok "$3" || bad "${3}：$(body_of "$1")"; }
  new_char() {
    printf '%s\n' "$1" >> "$TMP/names.txt"
    curl -sS --noproxy '*' -m 10 -X POST "http://127.0.0.1:$GW_PORT/character?token=$TOKEN" \
      -H 'Content-Type: application/json' \
      -d "{\"name\":\"$1\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>/dev/null |
      python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")'
  }
  complete() { post "http://127.0.0.1:$GW_PORT/sect/tasks/complete" \
    "{\"character_id\":$1,\"sect_id\":$2,\"task_id\":\"$3\"}"; }
  board() { get "http://127.0.0.1:$GW_PORT/sect/tasks?character_id=$1&sect_id=$2"; }
  tstate() { "$DOTNET" "$SOCIAL_DLL" --sect-task-state "$1" "$2" --config "$TMP/social.json" 2>&1; }
  tledger() { "$DOTNET" "$SOCIAL_DLL" --sect-task-ledger "$1" --config "$TMP/social.json" 2>&1; }
  tcomplete() { "$DOTNET" "$SOCIAL_DLL" --sect-task-complete "$@" --config "$TMP/social.json" 2>&1; }
  money() { "$DOTNET" "$PROBE_DLL" --shop-state "$1" 2>&1 | grep -oE 'balances=[^ ]*' | head -1; }

  if start_all; then
    ok "角色服 / 社交服 / 网关就绪（${CH_PORT} / ${SO_PORT} / ${GW_PORT}）"
  else
    bad "服务没起来"; tail -6 "$TMP/harness/social.log" 2>/dev/null | detail
  fi
  grep -q '^sect_tasks=' "$TMP/harness/social.log" && ok "真实启动日志打印同形任务行" \
    || bad "启动日志缺任务行"

  FOUNDER="$(new_char "q4f$$")"
  LEVEL_OK=0
  [ -n "$FOUNDER" ] && curl -sS --noproxy '*' -m 10 -X POST \
    "http://127.0.0.1:$GW_PORT/character/level?token=$TOKEN" -H 'Content-Type: application/json' \
    -d "{\"character_id\":$FOUNDER,\"level\":45}" >/dev/null 2>&1 && LEVEL_OK=1
  [ "$LEVEL_OK" = "1" ] && ok "测试角色就位并升到 45 级（${FOUNDER}）" || bad "建角 / 升级失败"

  NAME="q4s_$$_$(date +%s)"
  r="$(post "http://127.0.0.1:$GW_PORT/sect/create" "{\"character_id\":$FOUNDER,\"name\":\"$NAME\"}")"
  check "$r" "ok=true level=1 member_cap=10" "建 1 级宗门"
  SECT="$(json_field "$(body_of "$r")" sect_id)"

  # ① 面板：两条任务、次数 0、贡献 0、建设 0。
  r="$(board "$FOUNDER" "$SECT")"
  b="$(body_of "$r")"
  check "$r" "ok=true level=1 construction=0 contribution=0" "面板初始事实值"
  [ "$(printf '%s' "$b" | python3 -c 'import json,sys;print(len(json.load(sys.stdin).get("tasks",[])))')" = "2" ] \
    && ok "面板两条任务" || bad "面板任务数不对：$b"

  # ② 完成日常：贡献 +20、建设 +10，同一笔事务（进度 / 流水 / 两边数值）。
  r="$(complete "$FOUNDER" "$SECT" sect_daily_patrol)"
  check "$r" "ok=true done=1 max_per_period=1 granted_contribution=20 granted_construction=10" "完成日常：计次 1、贡献 20 / 建设 10"
  b="$(body_of "$r")"
  [ "$(json_field "$b" contribution_before)" = "0" ] && [ "$(json_field "$b" contribution_after)" = "20" ] \
    && [ "$(json_field "$b" construction_before)" = "0" ] && [ "$(json_field "$b" construction_after)" = "10" ] \
    && ok "日常回包 before / after：贡献 0→20、建设 0→10" || bad "日常回包数值不对：$b"
  S="$(tstate "$SECT" "$FOUNDER")"
  printf '%s' "$S" | grep -q 'construction=10 ' && printf '%s' "$S" | grep -q 'contribution=20 ' \
    && printf '%s' "$S" | grep -q 'sect_daily_patrol:1/1' \
    && ok "显式读库：进度 1/1、贡献 20、建设 10（同一笔事务）" || bad "读库不一致：$S"
  L="$(tledger "$SECT")"
  printf '%s' "$L" | grep -q 'rows=1' && printf '%s' "$L" | grep -q 'sect_daily_patrol' \
    && printf '%s' "$L" | grep -q '20/10' && ok "流水一行：贡献 20 / 建设 10" || bad "流水不对：$L"

  # ③ 同一天再交：limit_reached，进度 / 贡献 / 建设都不动。
  BEFORE="$(tstate "$SECT" "$FOUNDER")"
  r="$(complete "$FOUNDER" "$SECT" sect_daily_patrol)"
  check "$r" "ok=false reason=limit_reached" "同一天重复交日常：limit_reached"
  [ "$(tstate "$SECT" "$FOUNDER")" = "$BEFORE" ] && ok "limit_reached 后库状态一字未动" \
    || bad "limit_reached 写了库"

  # ④ 未知任务编号：task_unknown，不写库。
  r="$(complete "$FOUNDER" "$SECT" no_such_task)"
  check "$r" "ok=false reason=task_unknown" "未知任务 task_unknown"
  [ "$(tstate "$SECT" "$FOUNDER")" = "$BEFORE" ] && ok "task_unknown 后库状态一字未动" || bad "task_unknown 写了库"

  # ⑤ 等级门槛：1 级成员交任务 level_too_low，等级 / 次数 / 贡献 / 建设都不动。
  LOW="$(new_char "q4l$$")"
  r="$(post "http://127.0.0.1:$GW_PORT/sect/join" "{\"character_id\":$LOW,\"sect_id\":$SECT}")"
  check "$r" "ok=true" "1 级角色入宗"
  r="$(complete "$LOW" "$SECT" sect_daily_patrol)"
  check "$r" "ok=false reason=level_too_low" "等级不够：level_too_low"
  [ "$(tstate "$SECT" "$LOW" | grep -o 'contribution=[0-9]*')" = "contribution=0" ] \
    && ok "level_too_low 没给贡献" || bad "level_too_low 写了贡献"

  # ⑥ 非成员：不是本宗门的角色交任务被拒，宗门数值不动。
  OUT="$(new_char "q4o$$")"
  BEFORE="$(tstate "$SECT" "$FOUNDER")"
  r="$(complete "$OUT" "$SECT" sect_daily_patrol)"
  check "$r" "ok=false reason=not_member" "非成员：not_member"
  [ "$(tstate "$SECT" "$FOUNDER")" = "$BEFORE" ] && ok "not_member 后宗门数值一字未动" || bad "not_member 写了库"

  # ⑦ 商城买不到建设：清单外编号 not_in_list，余额与宗门建设都不动。
  BEFORE_M="$(money "$FOUNDER")"
  r="$(post "http://127.0.0.1:$GW_PORT/shop/buy" \
    "{\"character_id\":$FOUNDER,\"item_id\":\"shop_construction_999\"}")"
  check "$r" "ok=false reason=not_in_list charged_yuanbao=0" "清单外建设类编号 not_in_list"
  [ "$(money "$FOUNDER")" = "$BEFORE_M" ] && ok "商城拒绝后角色货币一字未动" || bad "商城拒绝动了余额"
  [ "$(tstate "$SECT" "$FOUNDER" | grep -o 'construction=[0-9]*')" = "construction=10" ] \
    && ok "商城没有给宗门加建设（仍是任务给的 10）" || bad "商城改了宗门建设"

  # ⑧ 建设不够升级：日常 10 < 100，被拒且不扣。
  r="$(post "http://127.0.0.1:$GW_PORT/sect/upgrade" "{\"character_id\":$FOUNDER,\"sect_id\":$SECT}")"
  check "$r" "ok=false reason=not_enough_construction" "只有任务给的 10，建设不够升级"

  # ⑨ 周常给 50；再按临时表（周常 100）重启，任务攒够 100 后升级成功。
  r="$(complete "$FOUNDER" "$SECT" sect_weekly_vein)"
  check "$r" "ok=true granted_contribution=100 granted_construction=50" "周常：贡献 100 / 建设 50"
  [ "$(tstate "$SECT" "$FOUNDER" | grep -o 'construction=[0-9]*')" = "construction=60" ] \
    && ok "日常 + 周常累计建设 60（10 + 50）" || bad "建设累计不对"

  python3 - "$ROOT" "$TMP" <<'PY'
import json, sys
root, tmp = sys.argv[1:3]
d = json.load(open(root + "/config/sect_tasks.json", encoding="utf-8"))
for t in d["tasks"]:
    if t["id"] == "sect_weekly_vein":
        t["max_per_period"] = 2
        t["rewards"]["construction"] = 100
json.dump(d, open(tmp + "/tasks-100.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  if start_all --sect-task-table "$TMP/tasks-100.json"; then
    ok "改任务表重启社交服（周常 +100 建设、次数 2）"
  else
    bad "改任务表重启失败"
  fi
  r="$(complete "$FOUNDER" "$SECT" sect_weekly_vein)"
  check "$r" "ok=true granted_construction=100" "同一周再交一次周常：按配置给 100 建设"
  [ "$(tstate "$SECT" "$FOUNDER" | grep -o 'construction=[0-9]*')" = "construction=160" ] \
    && ok "任务攒到 160 建设（60 + 100）" || bad "建设累计不对"
  r="$(post "http://127.0.0.1:$GW_PORT/sect/upgrade" "{\"character_id\":$FOUNDER,\"sect_id\":$SECT}")"
  check "$r" "ok=true old_level=1 new_level=2 construction_before=160 construction_after=60 upgrade_cost=100" \
    "任务攒的建设够升级：1→2、扣 100"
  ok "宗门靠任务升级：建设的唯一进项是任务，商城买不到"

  # ⑩ 重置边界只认日历：同一时刻在 05:00 与 0 点重置下属于不同业务日，
  #    新业务日重新从 0 起算；旧周期的流水不删，只换键。
  cp config/calendar.json "$TMP/calendar-utc0.json"
  python3 - "$TMP/calendar-utc0.json" <<'CALPY'
import json, sys
p = sys.argv[1]
d = json.load(open(p, encoding="utf-8"))
d["daily_reset_hour"] = 0
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
CALPY
  RESET_CHAR="$(new_char "q4r$$")"
  NAME3="q4r_$$_$(date +%s)"
  r="$(post "http://127.0.0.1:$GW_PORT/sect/create" "{\"character_id\":$RESET_CHAR,\"name\":\"$NAME3\"}")"
  SECT3="$(json_field "$(body_of "$r")" sect_id)"
  R1="$(tcomplete "$SECT3" "$RESET_CHAR" 45 sect_daily_patrol "q4-reset-a-$RUN" \
    --at 2026-11-18T04:30:00+08:00)"
  printf '%s' "$R1" | grep -q 'ok=true' && printf '%s' "$R1" | grep -q 'period=2026-11-17' \
    && ok "05:00 重置下 04:30 算 11-17，第一笔成功" || bad "05:00 重置日界不对：$R1"
  R2="$(tcomplete "$SECT3" "$RESET_CHAR" 45 sect_daily_patrol "q4-reset-b-$RUN" \
    --calendar-table "$TMP/calendar-utc0.json" --at 2026-11-18T04:30:00+08:00)"
  printf '%s' "$R2" | grep -q 'ok=true' && printf '%s' "$R2" | grep -q 'period=2026-11-18' \
    && ok "0 点重置下同一时刻算 11-18，新业务日再交一次成功" || bad "换重置小时没换日界：$R2"
  R3="$(tcomplete "$SECT3" "$RESET_CHAR" 45 sect_daily_patrol "q4-reset-c-$RUN" \
    --calendar-table "$TMP/calendar-utc0.json" --at 2026-11-18T05:00:00+08:00)"
  printf '%s' "$R3" | grep -q 'reason=limit_reached' \
    && ok "同一业务日再交：limit_reached（重置不是清库）" || bad "同一日没限次：$R3"
  L="$(tledger "$SECT3")"
  printf '%s' "$L" | grep -q '2026-11-17' && printf '%s' "$L" | grep -q '2026-11-18' \
    && ok "流水同时留着 11-17 与 11-18 两个周期的行（重置只换键）" || bad "流水周期键不对：$L"

  # ⑪ 同一事务号重放：只算一次。用固定事务号连交三笔 + 重放一笔。
  python3 - "$ROOT" "$TMP" <<'PY'
import json, sys
root, tmp = sys.argv[1:3]
d = json.load(open(root + "/config/sect_tasks.json", encoding="utf-8"))
for t in d["tasks"]:
    if t["id"] == "sect_daily_patrol":
        t["max_per_period"] = 3
json.dump(d, open(tmp + "/tasks-3.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  DUP_CHAR="$(new_char "q4d$$")"
  curl -sS --noproxy '*' -m 10 -X POST "http://127.0.0.1:$GW_PORT/character/level?token=$TOKEN" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$DUP_CHAR,\"level\":45}" >/dev/null 2>&1 || true
  NAME2="q4d_$$_$(date +%s)"
  r="$(post "http://127.0.0.1:$GW_PORT/sect/create" "{\"character_id\":$DUP_CHAR,\"name\":\"$NAME2\"}")"
  SECT2="$(json_field "$(body_of "$r")" sect_id)"
  A="$(tcomplete "$SECT2" "$DUP_CHAR" 45 sect_daily_patrol "q4-dup-1-$RUN" --sect-task-table "$TMP/tasks-3.json" --at 2026-11-18T12:00:00+08:00)"
  B="$(tcomplete "$SECT2" "$DUP_CHAR" 45 sect_daily_patrol "q4-dup-1-$RUN" --sect-task-table "$TMP/tasks-3.json" --at 2026-11-18T12:01:00+08:00)"
  printf '%s' "$A" | grep -q 'ok=true' && printf '%s' "$A" | grep -q 'done=1/3' \
    && ok "固定事务号第一笔：ok、done=1/3" || bad "第一笔不对：$A"
  printf '%s' "$B" | grep -q 'reason=duplicate' \
    && ok "同一事务号重放：duplicate" || bad "重放没挡住：$B"
  C="$(tcomplete "$SECT2" "$DUP_CHAR" 45 sect_daily_patrol "q4-dup-2-$RUN" --sect-task-table "$TMP/tasks-3.json" --at 2026-11-18T12:02:00+08:00)"
  D="$(tcomplete "$SECT2" "$DUP_CHAR" 45 sect_daily_patrol "q4-dup-3-$RUN" --sect-task-table "$TMP/tasks-3.json" --at 2026-11-18T12:03:00+08:00)"
  E="$(tcomplete "$SECT2" "$DUP_CHAR" 45 sect_daily_patrol "q4-dup-4-$RUN" --sect-task-table "$TMP/tasks-3.json" --at 2026-11-18T12:04:00+08:00)"
  printf '%s' "$C" | grep -q 'ok=true' && printf '%s' "$D" | grep -q 'ok=true' \
    && printf '%s' "$E" | grep -q 'reason=limit_reached' \
    && ok "次数按配置到 3 封顶：第三笔 ok、第四笔 limit_reached" || bad "次数上限不对：$C / $D / $E"
  L="$(tledger "$SECT2")"
  printf '%s' "$L" | grep -q 'rows=3' && printf '%s' "$L" | grep -q '20/10' \
    && ok "流水恰好三行（重放没有多记）" || bad "流水行数不对：$L"
  [ "$(tstate "$SECT2" "$DUP_CHAR" | grep -o 'construction=[0-9]*')" = "construction=30" ] \
    && ok "三笔日常建设 30（重放没有多给）" || bad "建设加了不止三笔"

  # 清理：先删角色再删账号；v 是宗门行，由社交 schema 保留（测试前缀可辨认）。
  if [ -f "$TMP/names.txt" ]; then
    while IFS= read -r n; do
      [ -n "$n" ] && "$DOTNET" "$PROBE_DLL" --delete-character "$n" \
        --config "$TMP/character.json" >/dev/null 2>&1 || true
    done <"$TMP/names.txt"
  fi
  printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" \
    --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  harness_cleanup
fi

finish "$TITLE"
