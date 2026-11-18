#!/usr/bin/env bash
# tools/check/check-q6.sh —— Q6 J4 通行证进度（阶段 2 第六张，未覆盖排期第 55 行）。
#
# 完成标准（未覆盖排期第 55 行）：
#   只有点数够才可领该阶；来源（副本 / 主线）按配置；换季重置；不能跳阶白领。
#
# 本脚本：
#   1. 编译角色服 / 网关 / probe；
#   2. 静态断言（配置正本、两张表、一笔事务、门槛在领取事务里、周期键只由日历算、
#      proto 两端、网关、CLI、CI、文档）；
#   3. 只读入口 --pass-progress-config 的来源 / 点数 / 周期键（--at 验日界 / 周界）；
#   4. 坏进度表让只读入口与角色服启动都起不来（config_error，非 0 退出）；
#   5. 有库时（MMO_Q6_DB=1）：真网关跑领取门槛、副本 / 主线加点、次数上限、重放、换季，
#      并核 INV-22 / INV-23。
#
#   bash tools/check/check-q6.sh
#   MMO_Q6_DB=1 bash tools/check/check-q6.sh   再加真库段（建临时账号 / 角色，跑完删）
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
CHAR_SERVICE_DLL="$ROOT/src/Character/bin/Release/net10.0/""Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
GLOBAL_DLL="$ROOT/src/Global/bin/Release/net10.0/Global.dll"
TITLE="Q6（通行证进度）"
HAS_DB="${MMO_Q6_DB:-0}"
TMP="$(mktemp -d)"
TMP="${TMP//\\//}"
PIDS=""
cleanup() {
  for p in $PIDS; do stop_pid "$p"; done
  # MMO_Q6_KEEP=1 保留临时目录与日志：真库段红了要看角色服 / 全局服务的启动日志。
  [ "${MMO_Q6_KEEP:-0}" = "1" ] && { printf 'keep tmp=%s\n' "$TMP"; return 0; }
  rm -rf "$TMP" 2>/dev/null || true
}
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Character/Character.csproj src/Gateway/Gateway.csproj \
            src/Global/Global.csproj tools/probe/CliProbe/CliProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 $n"
  else
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ "$BUILD_OK" = "1" ] || { finish "$TITLE"; exit 1; }

echo "== 2. 静态断言 =="
if python3 "$ROOT/tools/check/check-q6-static.py" >"$TMP/static.txt" 2>&1; then
  while IFS= read -r line; do
    case "$line" in "PASS "*) ok "${line#PASS }" ;; "FAIL "*) bad "${line#FAIL }" ;; esac
  done <"$TMP/static.txt"
else
  bad "静态断言脚本自身失败"; head -8 "$TMP/static.txt" | detail
fi

echo "== 3. 只读入口：点数 / 来源 / 周期键 =="
CFG="$("$DOTNET" "$PROBE_DLL" --pass-progress-config 2>&1)"
printf '%s' "$CFG" | grep -q '^pass_progress_config=ok version=2 ' \
  && ok "进度表读得出来（version=2）" \
  || bad "进度表只读入口不对：$(printf '%s' "$CFG" | head -1)"
printf '%s' "$CFG" | grep -q 'points_per_stage=100 sources=3' \
  && ok "每阶点数与来源数从配置现取" \
  || bad "每阶点数 / 来源数不对：$(printf '%s' "$CFG" | head -1)"
printf '%s' "$CFG" | grep -q 'pass_progress_source=abyss_daily kind=dungeon_clear dungeon=abyss points=20 period=daily max=1' \
  && ok "深渊来源：副本来源、每天 1 次、20 点（全部来自配置）" \
  || bad "深渊来源字段不对：$(printf '%s' "$CFG" | head -3 | tr '\n' ' ')"
printf '%s' "$CFG" | grep -q 'pass_progress_source=mainline_once kind=mainline_stage dungeon=- points=10 period=once max=1' \
  && ok "主线来源：一次、10 点、不挂副本" \
  || bad "主线来源字段不对：$(printf '%s' "$CFG" | head -3 | tr '\n' ' ')"

key_of() { # $1=来源编号 $2..=--at 参数
  local src="$1"; shift
  "$DOTNET" "$PROBE_DLL" --pass-progress-config "$@" 2>&1 \
    | grep "^pass_progress_source=$src " | sed -n 's/.* key=//p'
}
D1="$(key_of abyss_daily --at 2026-11-18T04:30:00+08:00)"
D2="$(key_of abyss_daily --at 2026-11-18T05:00:00+08:00)"
W1="$(key_of lair_weekly --at 2026-11-16T04:59:00+08:00)"
W2="$(key_of lair_weekly --at 2026-11-16T05:00:00+08:00)"
O1="$(key_of mainline_once)"
[ "$D1" = "2026-11-17" ] && ok "日界 04:30 仍算 11-17（周期键只由日历算）" \
  || bad "日界 04:30 不对：$D1"
[ "$D2" = "2026-11-18" ] && ok "日界 05:00 进 11-18" || bad "日界 05:00 不对：$D2"
[ "$W1" = "2026-11-09" ] && ok "周界周一 04:59 仍算上一周（11-09）" || bad "周界 04:59 不对：$W1"
[ "$W2" = "2026-11-16" ] && ok "周界周一 05:00 进本周（11-16）" || bad "周界 05:00 不对：$W2"
[ "$O1" = "once" ] && ok "once 来源的周期键固定 once（跨季也只给一次）" || bad "once 周期键不对：$O1"

echo "== 4. 坏进度表起不来 =="
python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
base = json.load(open("config/season_pass_progress.json", encoding="utf-8"))
def case(name, mutate):
    d = json.loads(json.dumps(base)); mutate(d)
    json.dump(d, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
def s(d, i=0): return d["sources"][i]
case("no_sources", lambda d: d.pop("sources", None))
case("empty_sources", lambda d: d.update(sources=[]))
case("bad_schema", lambda d: d.update(schema="season-pass-progress/9"))
case("bad_kind", lambda d: s(d).update(kind="login_bonus"))
case("bad_period", lambda d: s(d).update(period="monthly"))
case("missing_dungeon", lambda d: s(d).pop("dungeon_id", None))
case("unknown_dungeon", lambda d: s(d).update(dungeon_id="nowhere"))
case("zero_points", lambda d: s(d).update(points=0))
case("weekly_no_cap", lambda d: s(d, 1).pop("max_per_period", None))
case("dup_id", lambda d: s(d, 1).update(id=s(d, 0)["id"]))
case("mainline_with_dungeon", lambda d: s(d, 2).update(dungeon_id="abyss"))
case("unknown_key", lambda d: s(d).update(currency="gold"))
case("zero_per_stage", lambda d: d.update(points_per_stage=0))
PY
for c in no_sources empty_sources bad_schema bad_kind bad_period missing_dungeon \
         unknown_dungeon zero_points weekly_no_cap dup_id mainline_with_dungeon \
         unknown_key zero_per_stage; do
  o="$("$DOTNET" "$PROBE_DLL" --pass-progress-config --pass-progress-table "$TMP/bad-$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$o" | grep -q '^config_error='; then
    ok "坏进度表 ${c}：config_error + 非 0 退出"
  else
    bad "坏进度表 ${c} 没被挡住：rc=$rc $o"
  fi
done
# 启动路径：把 character.json 的 season_pass_progress_table 指到坏表，角色服必须起不来。
python3 - "$TMP" <<'PY'
import json, sys
tmp = sys.argv[1]
c = json.load(open("config/character.json", encoding="utf-8"))
c["season_pass_progress_table"] = tmp + "/bad-unknown_key.json"
json.dump(c, open(tmp + "/character-bad.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
o="$(MMO_GAMEDB= "$DOTNET" "$CHAR_SERVICE_DLL" --config "$TMP/character-bad.json" 2>&1 | head -5)"
if printf '%s' "$o" | grep -q '^config_error='; then
  ok "角色服启动路径也挡住坏进度表（config_error）"
else
  bad "角色服没挡住坏进度表：$(printf '%s' "$o" | head -1)"
fi

echo "== 5. 真库段（可选） =="
if [ "$HAS_DB" != "1" ]; then
  printf '  ..   未设 MMO_Q6_DB=1，跳过真库段（门槛 / 加点 / 换季 由 MMO_Q6_DB=1 在本机跑）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/db"
  CH_PORT=${MMO_Q6_CH_PORT:-62206}
  GW_PORT=${MMO_Q6_GW_PORT:-61206}
  GL_PORT=${MMO_Q6_GL_PORT:-64106}
  ACCOUNT="q6_pass_$$"
  # 临时进度表：每阶 300 点，副本来源一次给 500 / 500，主线 200。
  # 这样「点数不够 → 够 → 再不够 → 够」四段都能在一次跑里看到。
  python3 - "$TMP" "$CH_PORT" "$GW_PORT" "$GL_PORT" <<'PY'
import json, sys
tmp, ch, gw, gl = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]

def load(p):
    return json.load(open(p, encoding="utf-8"))

progress = load("config/season_pass_progress.json")
progress["points_per_stage"] = 300
for row, points in zip(progress["sources"], (500, 500, 200)):
    row["points"] = points
json.dump(progress, open(tmp + "/db/pass-progress.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)

c = load("config/character.json")
c["listen"] = "http://127.0.0.1:" + ch
c["season_pass_progress_table"] = tmp + "/db/pass-progress.json"
c["global_upstream"] = "http://127.0.0.1:" + gl
json.dump(c, open(tmp + "/db/character.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)

g = load("config/gateway.json")
g["listen"] = "http://127.0.0.1:" + gw
g["character_upstream"] = "http://127.0.0.1:" + ch
g["battle_upstream"] = "ws://127.0.0.1:60006/battle"
# 季是全局服务的事实值，网关与角色服都要指到这一台临时全局服务，不走默认 64000。
g["global_upstream"] = "http://127.0.0.1:" + gl
json.dump(g, open(tmp + "/db/gateway.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)

n = load("config/global.json")
n["listen"] = "http://127.0.0.1:" + gl
n["character_upstream"] = "http://127.0.0.1:" + ch
json.dump(n, open(tmp + "/db/global.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
# 全局服务按「自己那份 global.json 的同目录」找 season.json（season_table_file 只认文件名），
# 所以临时目录里也要放一份，否则当前季读不到、通行证一律 season_unavailable。
json.dump(load("config/season.json"),
          open(tmp + "/db/season.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
# 赛季表还要与同目录的副本表、境界开关表交叉校验（目标副本必须存在、境界必须开放），
# 三份一起放进去，当前季才读得出来。
for name in ("dungeons.json", "realms.json"):
    json.dump(load("config/" + name),
              open(tmp + "/db/" + name, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  # 库要先有 Q6 的两张表：按清单跑一次迁移（没有待应用的条目时只打 skip，不动已有数据）。
  if "$DOTNET" "$PROBE_DLL" --migrate --migrations db/migrations.json \
      --config config/character.json >"$TMP/db/migrate.log" 2>&1; then
    grep -q 'migration=applied id=character/0024' "$TMP/db/migrate.log" \
      && ok "迁移：character/0024 已应用到库" \
      || ok "迁移：character/0024 早已应用（清单 skip）"
  else
    bad "迁移失败：$(tail -3 "$TMP/db/migrate.log" | tr '\n' ' ')"
  fi
  CUR="$(python3 -c 'import json;print(json.load(open("config/season.json",encoding="utf-8"))["current"])')"
  OTHER="$(python3 -c 'import json,sys
cfg=json.load(open("config/season.json",encoding="utf-8"));cur=cfg["current"]
print(next(s["id"] for s in cfg["seasons"] if s["id"]!=cur))')"
  printf '%s\n' "Pw_q6_7k" \
    | "$DOTNET" "$GATEWAY_DLL" --config "$TMP/db/gateway.json" \
        --seed-account "$ACCOUNT" --seed-slots 8 >/dev/null 2>&1 || true
  GL_PID="$(harness_start "$TMP/db/global.log" "$DOTNET" "$GLOBAL_DLL" --config "$TMP/db/global.json")"
  harness_track_port "$GL_PORT"
  harness_wait_port "$GL_PORT" "全局服务" 120 0.5 || bad "全局服务没起来"
  CH_PID="$(harness_start "$TMP/db/character.log" "$DOTNET" \
    "$CHAR_SERVICE_DLL" --config "$TMP/db/character.json")"
  harness_track_port "$CH_PORT"
  harness_wait_port "$CH_PORT" "角色服" 120 0.5 || bad "角色服没起来"
  GW_PID="$(harness_start "$TMP/db/gateway.log" "$DOTNET" "$GATEWAY_DLL" --config "$TMP/db/gateway.json")"
  harness_track_port "$GW_PORT"
  harness_wait_port "$GW_PORT" "网关" 120 0.5 || bad "网关没起来"
  TOKEN="$(curl -s -m 10 -X POST "http://127.0.0.1:$GW_PORT/login" \
    -H 'Content-Type: application/json' -d "{\"account\":\"$ACCOUNT\",\"password\":\"Pw_q6_7k\"}" \
    | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("token",""))
except Exception: print("")')"
  CHAR="$(curl -s -m 10 -X POST "http://127.0.0.1:$GW_PORT/character?token=$TOKEN" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"q6_pass_$$\",\"race\":\"mai\",\"class_id\":\"warrior\"}" \
    | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")')"
  if [ -z "$TOKEN" ] || [ -z "$CHAR" ]; then
    bad "真库段：登录或建角失败（见 $TMP/db/*.log）"
  else
    ok "真库段：临时账号与角色就位（$CHAR），当前季 $CUR"
    panel() { curl -s -m 10 "http://127.0.0.1:$GW_PORT/season/pass?token=$TOKEN&character_id=$CHAR"; }
    claim() {
      curl -s -m 10 -X POST "http://127.0.0.1:$GW_PORT/season/pass/claim?token=$TOKEN" \
        -H 'Content-Type: application/json' \
        -d "{\"character_id\":$CHAR,\"track\":\"$1\",\"stage\":$2}"
    }
    award() { # $1=来源 $2=ref $3=季 [$4=事务号]
      "$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" --pass-season "$3" \
        --pass-point-award "$CHAR" "$1" --pass-point-ref "$2" ${4:+--pass-point-txn "$4"} \
        2>&1 | grep '^pass_award=' | tail -1
    }
    P0="$(panel)"
    printf '%s' "$P0" | grep -q '"points":0' && printf '%s' "$P0" | grep -q '"points_per_stage":300' \
      && printf '%s' "$P0" | grep -q '"unlocked":false' \
      && ok "面板：0 点、每阶 300 点、所有阶未解锁（门槛来自配置）" \
      || bad "面板不对：$(printf '%s' "$P0" | head -c 400)"

    R="$(claim free 1)"
    printf '%s' "$R" | grep -q '"reason":"points_not_enough"' \
      && ok "0 点时领免费轨第 1 阶被拒（points_not_enough，不能白领）" \
      || bad "0 点就领到了：$(printf '%s' "$R" | head -c 300)"

    A1="$(award abyss_daily abyss "$CUR")"
    printf '%s' "$A1" | grep -q 'pass_award=ok .*source=abyss_daily .*period=[0-9-]* .*points=500 before=0 after=500' \
      && ok "副本通关加点：深渊 +500，0 → 500（点数来自配置）" \
      || bad "副本加点不对：$A1"
    A2="$(award abyss_daily abyss "$CUR")"
    printf '%s' "$A2" | grep -q 'reason=limit_reached' \
      && ok "同一天再通关深渊被日次数上限挡住（limit_reached，上限在配置）" \
      || bad "日上限没起作用：$A2"

    R="$(claim free 1)"
    printf '%s' "$R" | grep -q '"ok":true' && printf '%s' "$R" | grep -q '"stage":1' \
      && ok "500 点够第 1 阶门槛（300），免费轨第 1 阶领到" \
      || bad "够点却领不到：$(printf '%s' "$R" | head -c 300)"
    R="$(claim free 2)"
    printf '%s' "$R" | grep -q '"reason":"points_not_enough"' \
      && ok "第 2 阶门槛 600 点，500 点仍不够（不能跳阶白领）" \
      || bad "第 2 阶门槛没起作用：$(printf '%s' "$R" | head -c 300)"

    A3="$(award lair_weekly lair "$CUR")"
    printf '%s' "$A3" | grep -q 'pass_award=ok .*source=lair_weekly .*before=500 after=1000' \
      && ok "副本通关加点：巢穴周常 +500，500 → 1000" \
      || bad "周常加点不对：$A3"
    TXN="$(printf '%s' "$A3" | grep -o 'txn=[^ ]*' | head -1 | cut -d= -f2)"
    A4="$(award lair_weekly lair "$CUR" "$TXN")"
    printf '%s' "$A4" | grep -q 'reason=duplicate' && printf '%s' "$A4" | grep -q 'after=1000' \
      && ok "同一事务号重放回 duplicate：点数仍是 1000，不二次加" \
      || bad "重放没挡住：$A4"
    R="$(claim free 2)"
    printf '%s' "$R" | grep -q '"ok":true' \
      && ok "1000 点够第 2 阶门槛（600），免费轨第 2 阶领到" \
      || bad "1000 点仍领不到第 2 阶：$(printf '%s' "$R" | head -c 300)"

    ST="$(award mainline_once 1 "$CUR")"
    printf '%s' "$ST" | grep -q 'pass_award=ok .*source=mainline_once' \
      && ok "主线推进加点：mainline_once +200" \
      || bad "主线加点不对：$ST"
    ST2="$(award mainline_once 2 "$OTHER")"
    printf '%s' "$ST2" | grep -q 'reason=limit_reached' \
      && ok "once 来源换到 ${OTHER} 季也不再给（跨季只一次，换季不重置这一项）" \
      || bad "once 来源跨季又给了一次：$ST2"

    OTH="$(award abyss_daily abyss "$OTHER")"
    SOTH="$("$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" --pass-season "$OTHER" \
      --pass-points "$CHAR" 2>&1 | grep '^pass_points=')"
    printf '%s' "$OTH" | grep -q 'before=0 after=500' \
      && printf '%s' "$SOTH" | grep -q 'points=500' \
      && ok "换季重置：${OTHER} 季从 0 起算（旧季的点不带过来）" \
      || bad "换季重置不对：$OTH / $SOTH"
    SCUR="$("$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" --pass-season "$CUR" \
      --pass-points "$CHAR" 2>&1 | grep '^pass_points=')"
    printf '%s' "$SCUR" | grep -q 'points=1200' \
      && ok "本季点数 1200（500 + 500 + 200），换季没有动旧季的行" \
      || bad "本季点数不对：$SCUR"

    LED="$(TXN="$TXN" "$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" \
      --pass-season "$CUR" --pass-ledger "$CHAR" 2>&1)"
    printf '%s' "$LED" | grep -q 'source=abyss_daily kind=dungeon_clear' \
      && printf '%s' "$LED" | grep -q 'source=mainline_once kind=mainline_stage' \
      && ok "加点流水落库：副本与主线两种来源都在，带周期键与引用" \
      || bad "加点流水不对：$(printf '%s' "$LED" | head -4 | tr '\n' ' ')"

    INV="$("$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" --invariants 2>&1)"
    printf '%s' "$INV" | grep -q '^invariant=ok id=INV-22 ' \
      && printf '%s' "$INV" | grep -q '^invariant=ok id=INV-23 ' \
      && ok "INV-22 / INV-23 在这一库上 0 违反（点数与流水一致、once 只一笔）" \
      || bad "不变量有违反：$(printf '%s' "$INV" | grep '^invariant=fail' | head -3 | tr '\n' ' ')"

    "$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" \
      --delete-character "q6_pass_$$" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" \
      --config "$TMP/db/gateway.json" >/dev/null 2>&1 || true
  fi
  harness_cleanup
fi

finish "$TITLE"
