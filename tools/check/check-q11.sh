#!/usr/bin/env bash
# tools/check/check-q11.sh —— Q11 G3 世界 Boss 时间窗 / 分线（阶段 2 第十张）。
#
# 完成标准（未覆盖排期第 60 行）：
#   按本地时区显示与判定；同一时刻只有一场；分线容量；
#   入口只在已开放地表；名额按 hold / renew / lost 三个时长回收。
#
# 本脚本：
#   1. 编译世界服 / 战斗服 / 网关 / 两个探针；
#   2. 静态断言（配置正本、共享表按日历时区、名额只在进程内存、proto 面、两端接线）；
#   3. 只读状态机（不连线）：发名额 / 重复要 / 续约 / 满员 / 不在入口线 / 分线满 /
#      过期回收 / 释放，逐条对 reason 与事实值；
#   4. 改时区后出现时刻整体平移：同一 UTC 时刻在两份日历下命中不同场次；
#   5. 坏表 8 类都起不来（hold 三个数、schema、version、timezone、跨午夜）；
#   6. 真服（不连库）：世界服 gRPC 把角色送进入口分线 → 战斗服要到名额并进场 →
#      离场把名额还回去；没进线的人被回 worldboss_not_on_entrance_line；
#      世界服不可达时战斗服按本进程人数降级放行。
#
#   bash tools/check/check-q11.sh
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
WD="$ROOT/src/World/bin/Release/net10.0/World.dll"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
WP="$ROOT/tools/probe/WorldProbe/bin/Release/net10.0/WorldProbe.dll"
BP="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
TITLE="Q11（G3 世界 Boss 时间窗 / 分线）"
TMP="$(mktemp -d)"
TMP="${TMP//\\//}"
PIDS=""
cleanup() {
  for p in $PIDS; do stop_pid "$p"; done
  rm -rf "$TMP" 2>/dev/null || true
}
trap cleanup EXIT
utf8() { case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) python3 -c 'import sys; sys.stdout.buffer.write(sys.stdin.buffer.read().decode("gbk","replace").encode("utf-8"))' ;; *) cat ;; esac; }
free_port() { python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1]);s.close()'; }

WB="$ROOT/config/world_boss.json"
CAP="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["cap"])' "$WB")"
ENT="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["entrance"]["id"])' "$WB")"
REGION="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["entrance"]["region_id"])' "$WB")"
BOSS="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["boss"]["id"])' "$WB")"
HOLD_S="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["hold"]["hold_s"])' "$WB")"
RENEW_S="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["hold"]["renew_s"])' "$WB")"
LOST_S="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["hold"]["lost_s"])' "$WB")"

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/World/World.csproj src/Battle/Battle.csproj src/Gateway/Gateway.csproj \
            tools/probe/WorldProbe/WorldProbe.csproj tools/probe/BattleProbe/BattleProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 $n"
  else
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ "$BUILD_OK" = "1" ] || { finish "$TITLE"; exit 1; }

echo "== 2. 静态断言 =="
python3 "$ROOT/tools/check/check-q11-static.py" "$ROOT" >"$TMP/static.txt" 2>&1
S_PASS="$(grep -c '^PASS ' "$TMP/static.txt" 2>/dev/null || true)"
S_FAIL="$(grep -c '^FAIL ' "$TMP/static.txt" 2>/dev/null || true)"
S_PASS="${S_PASS:-0}"; S_FAIL="${S_FAIL:-0}"
if [ "$S_PASS" = "0" ] && [ "$S_FAIL" = "0" ]; then
  bad "静态段一行都没输出（脚本崩了）"; head -10 "$TMP/static.txt" | detail
else
  PASS=$((PASS + S_PASS)); FAIL=$((FAIL + S_FAIL))
  [ "$S_FAIL" = "0" ] && ok "静态断言 $S_PASS 条全过" \
    || { bad "静态断言 $S_FAIL 条红"; grep '^FAIL ' "$TMP/static.txt" | head -12 | detail; }
fi

echo "== 3. 只读状态机：名额怎么发、怎么回收 =="
AT="$(python3 - "$ROOT" <<'PY'
import datetime, json, sys, zoneinfo
root = sys.argv[1]
wb = json.load(open(root + "/config/world_boss.json", encoding="utf-8"))
cal = json.load(open(root + "/config/calendar.json", encoding="utf-8"))
z = zoneinfo.ZoneInfo(cal["timezone"])
now = datetime.datetime.now(z)
h, m = (int(x) for x in wb["windows"][0]["start_local"].split(":"))
dur = wb["windows"][0]["duration_s"]
start = datetime.datetime(now.year, now.month, now.day, h, m, tzinfo=z)
if not (start <= now < start + datetime.timedelta(seconds=dur)):
    start += datetime.timedelta(days=1)
print(int((start + datetime.timedelta(seconds=5)).timestamp() * 1000))
PY
)"
# Windows 上 python3 的输出行尾是 \r\n，`$(...)` 只剥 \n，末位会拖一个 \r；
# 带 \r 的时间戳传给 `--at` 会让只读入口直接起不来（本机踩过，同 P3-4a2 的口径）。
AT="${AT%$'\r'}"
SO="$("$DOTNET" "$WD" --world-boss-state --at "$AT" 2>&1 | utf8)"
step_ok() {   # $1 = 期望片段，$2 = 说明
  if printf '%s' "$SO" | grep -q "$1"; then ok "$2"
  else bad "$2"; printf '%s' "$SO" | grep 'world_boss_slot=' | head -12 | detail; fi
}
step_ok "step=hold ok=true reason=none duplicate=false current=1 cap=$CAP" "发名额：第一个拿到位置（current=1 / cap=${CAP}）"
step_ok "step=hold_again ok=true reason=none duplicate=true current=1" "同一个人重复要名额是幂等的，不占第二个位置"
step_ok "step=renew_entered ok=true" "进场确认：续约接得住"
step_ok "step=hold_over_cap ok=false reason=worldboss_full" "第 cap+1 个人回 worldboss_full（上限只从配置读）"
step_ok "step=renew_unknown ok=false reason=slot_missing" "没占过的人续约回 slot_missing"
step_ok "step=release ok=true reason=leave" "离场把名额还回去"
step_ok "step=release_again ok=false reason=slot_missing" "还两次只认第一次，不动别人的名额"
step_ok "step=not_on_entrance_line ok=false reason=worldboss_not_on_entrance_line" "人不在入口那条分线上，一个位置都不给"
step_ok "step=line_full ok=false reason=worldboss_line_full" "入口分线站立人数超上限也不给"
step_ok "step=renew_after_lost ok=false reason=slot_missing" "超过 hold_s 没进场：名额被回收，续约回 slot_missing"
step_ok "world_boss_slot=summary current=0 remaining=$CAP cap=$CAP hold_s=$HOLD_S renew_s=$RENEW_S lost_s=$LOST_S" \
  "汇总：三个时长现取（hold=${HOLD_S} renew=${RENEW_S} lost=${LOST_S}），过期后 remaining 回满"
step_ok "counters granted=$CAP .*rejected_full=1" "计数：发过 $CAP 个名额、挡过 1 次满员"

echo "== 4. 改时区后出现时刻整体平移 =="
UTCCAL="$TMP/calendar-utc.json"
python3 - "$ROOT" "$UTCCAL" <<'PY'
import json, sys
root, dst = sys.argv[1:3]
d = json.load(open(root + "/config/calendar.json", encoding="utf-8"))
d["timezone"] = "UTC"
json.dump(d, open(dst, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
read -r AT_LOCAL AT_UTC <<EOF
$(python3 - "$ROOT" <<'PY'
import datetime, json, sys, zoneinfo
root = sys.argv[1]
wb = json.load(open(root + "/config/world_boss.json", encoding="utf-8"))
cal = json.load(open(root + "/config/calendar.json", encoding="utf-8"))
z = zoneinfo.ZoneInfo(cal["timezone"])
h, m = (int(x) for x in wb["windows"][0]["start_local"].split(":"))
day = datetime.datetime(2026, 11, 18, h, m, 5, tzinfo=z)
print(int(day.timestamp() * 1000), int(datetime.datetime(2026, 11, 18, h, m, 5,
      tzinfo=datetime.timezone.utc).timestamp() * 1000))
PY
)
EOF
# 同上：`read` 不剥 \r，两个时间戳都要手动抹掉。
AT_LOCAL="${AT_LOCAL%$'\r'}"
AT_UTC="${AT_UTC%$'\r'}"
wk() { printf '%s' "$("$DOTNET" "$WD" --world-boss-state --at "$1" ${2:+--calendar "$2"} 2>&1 | utf8)" | grep -m1 '^world_boss_window='; }
W_LOCAL="$(wk "$AT_LOCAL")"
W_UTC="$(wk "$AT_LOCAL" "$UTCCAL")"
printf '%s' "$W_LOCAL" | grep -q "open=true key=$ENT-20261118-w1 " \
  && ok "本地 12:05：按日历表算，开的是本地 w1 那一场" \
  || bad "本地时区没开在 w1：$W_LOCAL"
printf '%s' "$W_UTC" | grep -q 'open=false' \
  && ok "同一个 UTC 时刻换成 UTC 日历：这一刻不在任何窗口（说明时刻整体平移了）" \
  || bad "换时区后仍在窗口里：$W_UTC"
W_UTC2="$(wk "$AT_UTC" "$UTCCAL")"
printf '%s' "$W_UTC2" | grep -q "open=true key=$ENT-20261118-w1 " \
  && ok "UTC 12:05：换成 UTC 日历后开的还是 w1，钥匙仍由服务端算" \
  || bad "UTC 日历下窗口不对：$W_UTC2"
CAL_LINE="$(printf '%s' "$("$DOTNET" "$WD" --world-boss-state --at "$AT_LOCAL" 2>&1 | utf8)" | grep -m1 '^world_boss_calendar=')"
printf '%s' "$CAL_LINE" | grep -q 'timezone=Asia/Shanghai' \
  && ok "只读入口回的是日历时区（${CAL_LINE}）" || bad "日历时区没打出来：$CAL_LINE"

echo "== 5. 坏表 8 类都起不来 =="
for c in hold_zero renew_gt_hold renew_half schema version_zero timezone_utc no_hold midnight; do
  python3 - "$WB" "$TMP/bad-$c.json" "$c" <<'PY'
import json, sys
src, dst, case = sys.argv[1:4]
d = json.load(open(src, encoding="utf-8"))
if case == "hold_zero":
    d["hold"]["hold_s"] = 0
elif case == "renew_gt_hold":
    d["hold"]["renew_s"] = d["hold"]["hold_s"] + 1
elif case == "renew_half":
    d["hold"]["renew_s"] = d["hold"]["lost_s"]
elif case == "schema":
    d["schema"] = "world-boss-table/1"
elif case == "version_zero":
    d["version"] = 0
elif case == "timezone_utc":
    d["timezone"] = "utc"
elif case == "no_hold":
    d.pop("hold", None)
elif case == "midnight":
    d["windows"][0]["start_local"] = "23:50"
json.dump(d, open(dst, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  wout="$("$DOTNET" "$WD" --world-boss-state --world-boss-config "$TMP/bad-$c.json" 2>&1 | utf8)"; wrc=$?
  bout="$("$DOTNET" "$BD" --world-boss-table-path "$TMP/bad-$c.json" 2>&1 | utf8)"; brc=$?
  if [ "$wrc" -ne 0 ] && [ "$brc" -ne 0 ] \
     && printf '%s' "$wout" | grep -q '^config_error=' && printf '%s' "$bout" | grep -q '^config_error='; then
    ok "坏表 ${c}：两端都打 config_error= 且非 0 退出"
  else
    bad "坏表 ${c} 没挡住（世界服 rc=$wrc / 战斗服 rc=${brc}）：$(printf '%s' "$wout" | head -1)"
  fi
done

echo "== 6. 真服（不连库）：分线 → 名额 → 进场 → 还名额 =="
mkdir -p "$TMP/cfg" "$TMP/set-a" "$TMP/set-b" "$TMP/chr-a" "$TMP/chr-b"
cp "$ROOT"/config/*.json "$TMP/cfg/" 2>/dev/null || true
WPORT="$(free_port)"
python3 - "$ROOT" "$TMP" "$WPORT" <<'PY'
import json, os, sys
root, tmp, port = sys.argv[1], sys.argv[2], sys.argv[3]
cfg = os.path.join(tmp, "cfg")
w = json.load(open(os.path.join(cfg, "world.json"), encoding="utf-8"))
w["listen"] = "http://127.0.0.1:" + port
json.dump(w, open(os.path.join(cfg, "world.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
# 全天开：核对的是名额与链路，不是时刻边界（时刻由第 3、4 段验）。
d = json.load(open(os.path.join(cfg, "world_boss.json"), encoding="utf-8"))
d["windows"] = [{"id": "wall", "start_local": "00:00", "duration_s": 86400}]
json.dump(d, open(os.path.join(cfg, "world_boss.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
for name, upstream in (("a", "http://127.0.0.1:" + port), ("b", "")):
    b = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
    b.update(listen="127.0.0.1:0",
             settlement_dir=os.path.join(tmp, "set-" + name),
             character_dir=os.path.join(tmp, "chr-" + name),
             world_boss_table=os.path.join(cfg, "world_boss.json"))
    if upstream:
        b["world_upstream"] = upstream
    else:
        b.pop("world_upstream", None)
    # 配置写在 cfg/ 里：加载器按配置文件的目录找其余的表，那里有完整的一份副本。
    json.dump(b, open(os.path.join(cfg, "battle-" + name + ".json"), "w", encoding="utf-8"),
              ensure_ascii=False)
PY
"$DOTNET" "$WD" --config "$TMP/cfg/world.json" --world-boss-config "$TMP/cfg/world_boss.json" \
  >"$TMP/world.log" 2>&1 & PIDS="$PIDS $!"
wait_ready "$TMP/world.log" '^ready' "世界服" || true
GRPC="http://127.0.0.1:$WPORT"
"$DOTNET" "$BD" --config "$TMP/cfg/battle-a.json" >"$TMP/battle-a.log" 2>&1 & PIDS="$PIDS $!"
"$DOTNET" "$BD" --config "$TMP/cfg/battle-b.json" >"$TMP/battle-b.log" 2>&1 & PIDS="$PIDS $!"
UA=""; UB=""
for _ in $(seq 1 120); do
  UA="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/battle-a.log" 2>/dev/null | tail -1 | sed 's/^listen=//')"
  UB="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/battle-b.log" 2>/dev/null | tail -1 | sed 's/^listen=//')"
  [ -n "$UA" ] && [ -n "$UB" ] && break
  sleep 0.2
done
[ -n "$UA" ] && [ -n "$UB" ] && ok "真服：世界服与两份战斗服都就绪" || bad "真服进程没起来"

IN="$("$DOTNET" "$WP" --case boss_enter --grpc "$GRPC" --account q11a --char 9001 --region "$REGION" 2>&1 | utf8)"
printf '%s' "$IN" | grep -q 'entered=true' \
  && ok "进线：角色 9001 站进 ${REGION} 的一条分线（$(printf '%s' "$IN" | sed -n 's/.*line=\([^ ]*\).*/\1/p')）" \
  || bad "进线失败：$IN"

WA="${UA/http:\/\//ws://}"; WB2="${UB/http:\/\//ws://}"
R="$("$DOTNET" "$BP" --url "$WA/battle?worldboss=$ENT&char=9001&class=warrior" \
  --case worldboss_join --expect open --boss "$BOSS" 2>&1 | utf8)"
printf '%s' "$R" | grep -q '^verdict=ok' \
  && ok "在分线上的人拿到名额并进场（$(printf '%s' "$R" | grep -m1 '^case=')）" \
  || bad "占位后没进去：$(printf '%s' "$R" | head -3 | detail)"
grep -q "world_boss_slot=op=hold character=9001 ok=true" "$TMP/world.log" \
  && ok "世界服日志：名额发给了 9001" || bad "世界服没记发名额"
grep -q "worldboss_slot=op=confirm character=9001 ok=true" "$TMP/battle-a.log" \
  && ok "战斗服日志：进场后确认（entered=true，续约从 hold_s 换成 renew_s）" || bad "进场确认没走"
for _ in $(seq 1 40); do
  grep -q "world_boss_slot=op=release character=9001" "$TMP/world.log" && break
  sleep 0.2
done
grep -q "world_boss_slot=op=release character=9001 ok=true reason=leave" "$TMP/world.log" \
  && ok "离场：名额还回世界服（不是等 hold_s 到期自己掉）" \
  || { bad "离场没还名额"; grep -E 'world_boss_slot=|worldboss_slot=' "$TMP/world.log" "$TMP/battle-a.log" | tail -6 | detail; }

R="$("$DOTNET" "$BP" --url "$WA/battle?worldboss=$ENT&char=9002&class=warrior" \
  --case worldboss_join --expect not_on_entrance_line --boss "$BOSS" 2>&1 | utf8)"
printf '%s' "$R" | grep -q '^verdict=ok' \
  && ok "没进线的人被回 worldboss_not_on_entrance_line（绕开表世界连不进来）" \
  || bad "没挡住绕开表世界的连接：$(printf '%s' "$R" | head -3 | detail)"
grep -q "reason=worldboss_not_on_entrance_line" "$TMP/battle-a.log" \
  && ok "拒绝原因由世界服给、战斗服原样透传" || bad "拒绝原因没透传"

R="$("$DOTNET" "$BP" --url "$WB2/battle?worldboss=$ENT&char=9003&class=warrior" \
  --case worldboss_join --expect open --boss "$BOSS" 2>&1 | utf8)"
printf '%s' "$R" | grep -q '^verdict=ok' \
  && ok "世界服不可达：按本进程人数降级放行（不因为上游挂了把人挡在门外）" \
  || bad "上游不可达时进不去：$(printf '%s' "$R" | head -3 | detail)"
grep -q "degraded=upstream_off" "$TMP/battle-b.log" \
  && ok "战斗服日志写明降级依据（degraded=upstream_off fallback=party_connections）" \
  || bad "降级没有留下痕迹"

# 打掉这一场：事实值交回入口，入口关掉、名字只记第一次（这条放在最后，
# 因为入口一关，前面那些占位用例就都会先撞 worldboss_fought）。
WKEY="$(grep -oE 'window=[^ ]+' "$TMP/world.log" | head -1 | sed 's/^window=//')"
REC="$("$DOTNET" "$WP" --case boss_record --grpc "$GRPC" --entrance "$ENT" --window "$WKEY" \
  --killed true --sect 7001 --sect-name 太虚道宗 --damage 6200 2>&1 | utf8)"
printf '%s' "$REC" | grep -q 'ok=true' && printf '%s' "$REC" | grep -q 'closed=true' \
  && ok "打掉这一场：入口关掉、伤害最高的宗门记在入口上（$(printf '%s' "$REC" | grep -oE 'owner_name=[^ ]+')）" \
  || bad "入口没关或没记名字：$REC"
REC2="$("$DOTNET" "$WP" --case boss_record --grpc "$GRPC" --entrance "$ENT" --window "$WKEY" \
  --killed true --sect 7002 --sect-name 别家 --damage 6300 2>&1 | utf8)"
printf '%s' "$REC2" | grep -q 'duplicate=true' && printf '%s' "$REC2" | grep -q 'owner=7001' \
  && ok "同一场第二次报回 duplicate，名字不被后到的覆盖（谁先报谁算）" \
  || bad "第二次报改了名字或没判重：$REC2"

finish "$TITLE"
