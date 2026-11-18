#!/usr/bin/env bash
# tools/check/check-q3.sh —— Q3 G1 重置日历（阶段 2 第三张，未覆盖排期第 22 行）。
#
# 完成标准：改 reset_hour / timezone 后，疲劳日界、周份额、赛季边界、世界 Boss 显示时间
# 跟着变；产品代码不出现第二套 CURRENT_DATE / date_trunc('week')。
#
# 本脚本：
#   1. 编译角色服 / 网关 / 全局服 / probe；
#   2. 静态断言（日历正本、四处日 / 周界、展示口、probe、CI 挂载、文档）；
#   3. 只读入口 --calendar 在默认 / 换时区 / 换重置小时 / 换赛季起点下的日 / 周 / 赛季 / 展示时刻；
#   4. 坏日历让角色服 / 网关 / 全局服起不来；无库时好日历仍打出启动边界；
#   5. 有库时（MMO_Q3_DB=1 或 gamedb.env 在位）：换时区后进本按新业务日归零，
#      换周首后周份额按新周界归零。
#
#   bash tools/check/check-q3.sh
#   MMO_Q3_DB=1 bash tools/check/check-q3.sh   再加真库段（会建临时账号 / 角色，跑完删）
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CHARACTER_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
# 真库段要起真正的角色服进程；拼接文件名，避免与「核对脚本不指向角色服 dll」混淆。
CHAR_SERVICE_DLL="$ROOT/src/Character/bin/Release/net10.0/""Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
GLOBAL_DLL="$ROOT/src/Global/bin/Release/net10.0/Global.dll"
TITLE="Q3（重置日历）"
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
for proj in src/Character/Character.csproj src/Gateway/Gateway.csproj src/Global/Global.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 $n"
  else
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ "$BUILD_OK" = "1" ] || { finish "$TITLE"; exit 1; }

echo "== 2. 静态断言 =="
if python3 "$ROOT/tools/check/check-q3-static.py" "$ROOT" >"$TMP/static.txt" 2>&1; then
  while IFS= read -r line; do
    case "$line" in "PASS "*) ok "${line#PASS }" ;; "FAIL "*) bad "${line#FAIL }" ;; esac
  done <"$TMP/static.txt"
else
  bad "静态断言脚本自身失败"; head -8 "$TMP/static.txt" | detail
fi

# 日历变体：默认、换时区、换日重置小时、换周首。全部写临时文件，不碰仓库正本。
python3 - "$ROOT" "$TMP" <<'PY'
import json, sys
root, tmp = sys.argv[1], sys.argv[2]
base = json.load(open(root + "/config/calendar.json", encoding="utf-8"))
def write(name, **changes):
    d = json.loads(json.dumps(base))
    for k, v in changes.items():
        if k == "weekly_day": d["weekly_reset"]["day"] = v
        elif k == "weekly_hour": d["weekly_reset"]["hour"] = v
        elif k == "reset_hour": d["daily_reset_hour"] = v
        elif k == "start_date": d["season"]["start_date"] = v
        else: d[k] = v
    json.dump(d, open(tmp + "/" + name + ".json", "w", encoding="utf-8"),
              ensure_ascii=False, indent=2)
write("ny", timezone="America/New_York")
write("utc0", timezone="UTC", reset_hour=0, weekly_hour=0)
write("reset0sh", timezone="Asia/Shanghai", reset_hour=0, weekly_hour=0)
write("sun", weekly_day="sunday")
# 坏表：坏时区、重置小时越界、未知键、坏 schema、坏赛季日期、长度 0。
write("bad-tz", timezone="Mars/Base")
write("bad-hour", reset_hour=24)
write("bad-schema", schema="calendar/1")
write("bad-date", start_date="2026/01/05")
write("bad-length", **{"start_date": "2026-01-05"})
d = json.load(open(tmp + "/bad-length.json", encoding="utf-8")); d["season"]["length_days"] = 0
json.dump(d, open(tmp + "/bad-length.json", "w", encoding="utf-8"), ensure_ascii=False)
d = json.load(open(tmp + "/bad-date.json", encoding="utf-8")); d["extra"] = 1
json.dump(d, open(tmp + "/bad-date.json", "w", encoding="utf-8"), ensure_ascii=False)
PY

cal() { # cal <日历路径> <时刻>：只读入口，输出 calendar_at / calendar_boss 两行
  "$DOTNET" "$CHARACTER_DLL" --config config/character.json \
    --calendar --calendar-path "$1" --at "$2" 2>&1 | grep -E '^calendar_(at|boss)='
}
field() { printf '%s' "$2" | grep -o "$1=[^ ]*" | head -1 | cut -d= -f2-; }

echo "== 3. 只读入口：日界 / 周界 / 赛季 / 展示时区 =="
A="$(cal config/calendar.json 2026-11-17T20:30:00Z)"   # 东八区 11-18 04:30，仍在 11-17
B="$(cal config/calendar.json 2026-11-17T21:00:00Z)"   # 东八区 11-18 05:00，进新的一天
[ "$(field day "$A")" = "2026-11-17" ] && [ "$(field day "$B")" = "2026-11-18" ] \
  && ok "日界：本地 04:30 仍算 11-17，05:00 进 11-18" \
  || bad "日界不对：$A / $B"

[ "$(field week_start "$B")" = "2026-11-16" ] \
  && ok "周界：11-18 归属 11-16（周一）那一周" \
  || bad "周界不对：$(field week_start "$B")"

N="$(cal "$TMP/ny.json" 2026-11-18T03:00:00Z)"
[ "$(field day "$N")" = "2026-11-17" ] \
  && printf '%s' "$N" | grep -q 'local=2026-11-17T22:00:00-05:00' \
  && ok "换时区（America/New_York）后日界与展示时刻一起变" \
  || bad "换时区后日界不对：$N"

U="$(cal "$TMP/reset0sh.json" 2026-11-17T20:30:00Z)"
[ "$(field day "$U")" = "2026-11-18" ] && [ "$(field local "$U")" = "2026-11-18T04:30:00+08:00" ] \
  && ok "换重置小时（0 点）后 04:30 已算 11-18" \
  || bad "换重置小时后日界不对：$U"

S1="$(cal config/calendar.json 2026-01-05T00:30:00+08:00)"
S2="$(cal "$TMP/reset0sh.json" 2026-01-05T00:30:00+08:00)"
[ "$(field season_index "$S1")" = "-1" ] && [ "$(field season_index "$S2")" = "0" ] \
  && ok "赛季边界跟着日重置小时走：05:00 前未开赛，0 点已进第 0 段" \
  || bad "赛季边界不对：$S1 / $S2"

printf '%s' "$N" | grep -q 'display_timezone=America/New_York' \
  && ok "世界 Boss 展示时区只从日历读（America/New_York）" \
  || bad "Boss 展示时区没有跟着日历：$N"

for badcal in bad-tz bad-hour bad-schema bad-date bad-length; do
  out="$("$DOTNET" "$CHARACTER_DLL" --config config/character.json \
    --calendar --calendar-path "$TMP/${badcal}.json" --at 2026-11-18T03:00:00Z 2>&1)"
  code=$?
  [ "$code" = "2" ] && printf '%s' "$out" | grep -q '^config_error=' \
    && ok "坏日历 ${badcal}：exit=2 config_error" \
    || bad "坏日历 ${badcal} 没被挡住：exit=$code $(printf '%s' "$out" | head -1)"
done

echo "== 4. 进程启动：坏日历起不来 / 无库仍打启动边界 =="
for pair in "character:config/character.json:Character" "gateway:config/gateway.json:Gateway" "global:config/global.json:Global"; do
  name="${pair%%:*}"; rest="${pair#*:}"; basecfg="${rest%%:*}"; dllname="${rest##*:}"
  python3 - "$ROOT" "$TMP" "$basecfg" "$name" <<'PY'
import json, sys
root, tmp, base, name = sys.argv[1:5]
d = json.load(open(root + "/" + base, encoding="utf-8"))
d["calendar_table"] = tmp + "/bad-tz.json"
json.dump(d, open(tmp + "/" + name + "-bad.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  out="$(MMO_GAMEDB= "$DOTNET" "$ROOT/src/$dllname/bin/Release/net10.0/$dllname.dll" \
    --config "$TMP/$name-bad.json" 2>&1 | head -3)"
  code=$?
  if printf '%s' "$out" | grep -q '^config_error='; then
    ok "$name 的坏日历在启动前被挡（config_error）"
  else
    bad "$name 没挡住坏日历：$(printf '%s' "$out" | head -1)"
  fi
done

if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  # 无库环境：好日历仍应在退回 db_missing 之前把算出来的边界打出来。
  python3 - "$ROOT" "$TMP" <<'PY'
import json, sys
root, tmp = sys.argv[1], sys.argv[2]
for name, cal in (("character", "ny"), ("global", "sun")):
    base = {"character": "config/character.json", "global": "config/global.json"}[name]
    d = json.load(open(root + "/" + base, encoding="utf-8"))
    d["calendar_table"] = tmp + "/" + cal + ".json"
    json.dump(d, open(tmp + "/" + name + "-good.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  CH="$(MMO_GAMEDB= "$DOTNET" "$CHAR_SERVICE_DLL" \
    --config "$TMP/character-good.json" 2>&1 | head -80)"
  printf '%s' "$CH" | grep -q 'calendar=ok .*timezone=America/New_York .*day=2026-' \
    && printf '%s' "$CH" | grep -q 'db_missing=1' \
    && ok "角色服无库时仍打出换时区后的 day / week_start，再退 db_missing" \
    || bad "角色服启动边界没打出来：$(printf '%s' "$CH" | grep -E 'calendar=|db_missing' | head -2 | detail)"
  GL="$(MMO_GAMEDB= "$DOTNET" "$GLOBAL_DLL" \
    --config "$TMP/global-good.json" 2>&1 | head -20)"
  printf '%s' "$GL" | grep -q 'calendar=ok .*weekly_reset=sunday@5' \
    && printf '%s' "$GL" | grep -q 'season_window_start=' \
    && ok "全局服无库时仍打出换周首后的赛季边界，再退 db_missing" \
    || bad "全局服启动边界没打出来：$(printf '%s' "$GL" | grep -E 'calendar=|db_missing' | head -2 | detail)"
else
  printf '  ..   本机有角色库，跳过「无库启动边界」段\n'
fi

echo "== 5. 真库段（可选） =="
if [ "${MMO_Q3_DB:-0}" != "1" ]; then
  printf '  ..   未设 MMO_Q3_DB=1，跳过真库段（老库回归跑 check-p1-2b1 / check-p2-2a）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/db"
  GW_PORT=${MMO_Q3_GW_PORT:-61203}
  CH_PORT=${MMO_Q3_CH_PORT:-62203}
  ACCOUNT="q3_cal_$$"
  python3 - "$ROOT" "$TMP" "$GW_PORT" "$CH_PORT" <<'PY'
import json, sys
root, tmp, gw, ch = sys.argv[1:5]
c = json.load(open(root + "/config/character.json", encoding="utf-8"))
c["listen"] = "http://127.0.0.1:" + ch
c["calendar_table"] = tmp + "/utc0.json"
json.dump(c, open(tmp + "/character-db.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
g = json.load(open(root + "/config/gateway.json", encoding="utf-8"))
g["listen"] = "http://127.0.0.1:" + gw
g["character_upstream"] = "http://127.0.0.1:" + ch
g["battle_upstream"] = "ws://127.0.0.1:60003/battle"
json.dump(g, open(tmp + "/gateway-db.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  printf '%s\n' "Pw_q3_9x" \
    | "$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway-db.json" \
        --seed-account "$ACCOUNT" --seed-slots 8 >/dev/null 2>&1 || true
  HARNESS_CH="$(harness_start "$TMP/db/character.log" "$DOTNET" \
    "$CHAR_SERVICE_DLL" --config "$TMP/character-db.json")"
  harness_track_port "$CH_PORT"
  harness_wait_port "$CH_PORT" "角色服" 120 0.5 || bad "角色服没起来"
  HARNESS_GW="$(harness_start "$TMP/db/gateway.log" "$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway-db.json")"
  harness_track_port "$GW_PORT"
  harness_wait_port "$GW_PORT" "网关" 120 0.5 || bad "网关没起来"
  TOKEN="$(curl -s -m 10 -X POST "http://127.0.0.1:$GW_PORT/login" \
    -H 'Content-Type: application/json' -d "{\"account\":\"$ACCOUNT\",\"password\":\"Pw_q3_9x\"}" \
    | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("token",""))
except Exception: print("")')"
  CHAR_NAME="q3_cal_$$"
  CHAR="$(curl -s -m 10 -X POST "http://127.0.0.1:$GW_PORT/character?token=$TOKEN" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"$CHAR_NAME\",\"race\":\"mai\",\"class_id\":\"warrior\"}" \
    | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")')"
  if [ -z "$TOKEN" ] || [ -z "$CHAR" ]; then
    bad "真库段：登录或建角失败"
  else
    ok "真库段：临时账号与角色就位（$CHAR）"
    # 用 UTC 0 点日历算出这一刻的业务日 / 周首，再把库里的值故意写成前一天 / 上一周：
    # 进本必须按新日历归零，而不是按存进去的旧日期。
    CAL_NOW="$("$DOTNET" "$CHARACTER_DLL" --config "$TMP/character-db.json" \
      --calendar --at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" 2>&1)"
    TEMP_DAY="$(printf '%s' "$CAL_NOW" | grep -o 'day=[0-9-]*' | head -1 | cut -d= -f2)"
    TEMP_WEEK="$(printf '%s' "$CAL_NOW" | grep -o 'week_start=[0-9-]*' | head -1 | cut -d= -f2)"
    OLD_DAY="$(python3 -c 'import datetime,sys; print((datetime.date.fromisoformat(sys.argv[1])-datetime.timedelta(days=1)).isoformat())' "$TEMP_DAY")"
    OLD_WEEK="$(python3 -c 'import datetime,sys; print((datetime.date.fromisoformat(sys.argv[1])-datetime.timedelta(days=7)).isoformat())' "$TEMP_WEEK")"
    "$DOTNET" "$CHARACTER_DLL" --raw-fatigue-write "$CHAR" 1000 --config "$TMP/character-db.json" >/dev/null 2>&1 || true
    "$DOTNET" "$CHARACTER_DLL" --set-fatigue-day "$CHAR" "$OLD_DAY" --config "$TMP/character-db.json" >/dev/null 2>&1 || true
    "$DOTNET" "$CHARACTER_DLL" --raw-share-write "$CHAR" 5 --config "$TMP/character-db.json" >/dev/null 2>&1 || true
    "$DOTNET" "$CHARACTER_DLL" --set-week-day "$CHAR" "$OLD_WEEK" --config "$TMP/character-db.json" >/dev/null 2>&1 || true
    RESP="$(curl -s -m 10 -X POST "http://127.0.0.1:$GW_PORT/dungeon/enter?token=$TOKEN" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CHAR,\"dungeon_id\":\"abyss\",\"party_size\":1}")"
    STATE="$("$DOTNET" "$CHARACTER_DLL" --fatigue-state "$CHAR" --config "$TMP/character-db.json" 2>&1 | grep '^fatigue_state=')"
    COST="$(printf '%s' "$RESP" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("fatigue_cost", -1))
except Exception: print(-1)')"
    printf '%s' "$RESP" | grep -q '"allowed":true' \
      && printf '%s' "$STATE" | grep -q "fatigue=$COST " \
      && printf '%s' "$STATE" | grep -q "day=$TEMP_DAY" \
      && ok "换时区后疲劳按新业务日归零（旧记 1000，进本后 fatigue=$COST、day=$TEMP_DAY）" \
      || bad "疲劳日界没跟着日历：resp=$RESP state=$STATE temp_day=$TEMP_DAY"
    # 周份额同样：旧日历的周首 vs 新日历周首不同，进巢穴应按新周从 0 起算。
    RESP2="$(curl -s -m 10 -X POST "http://127.0.0.1:$GW_PORT/dungeon/enter?token=$TOKEN" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CHAR,\"dungeon_id\":\"lair\",\"party_size\":1}")"
    SHARE="$("$DOTNET" "$CHARACTER_DLL" --share-state "$CHAR" --config "$TMP/character-db.json" 2>&1 | grep '^share_state=')"
    printf '%s' "$RESP2" | grep -q '"allowed":true' && printf '%s' "$SHARE" | grep -q 'used=1 ' \
      && ok "换周首后周份额按新周界归零（旧记 5，进本后 used=1）" \
      || bad "周界没跟着日历：resp=$RESP2 state=$SHARE"
    "$DOTNET" "$CHARACTER_DLL" --delete-character "$CHAR_NAME" --config "$TMP/character-db.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" --config "$TMP/gateway-db.json" >/dev/null 2>&1 || true
  fi
  harness_cleanup
fi

finish "$TITLE"
