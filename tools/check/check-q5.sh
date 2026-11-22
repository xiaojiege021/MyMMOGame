#!/usr/bin/env bash
# tools/check/check-q5.sh —— Q5 J2 灵石修炼 / 境界任务（阶段 2 第五张，未覆盖排期第 54 行）。
#
# 完成标准（未覆盖排期第 54 行）：
#   完成得 stone_1；走 grant_txn；重复不双发；按城池税记账；灵石不再只能靠玩家交易。
#
# 本脚本：
#   1. 编译角色服 / 网关 / probe；
#   2. 静态断言（配置正本、三张表、一笔事务、唯一发放入口、抽成与金币账本分开、
#      proto 两端、网关、CLI、CI、文档）；
#   3. 只读入口 --city-task-config 的任务 / 门槛 / 奖励，以及 --at 下的日界 / 周界；
#   4. 坏任务表让角色服起不来（config_error，非 0 退出）；
#   5. 有库时（MMO_Q5_DB=1）：真网关跑面板 / 完成 / 重复 / 次数上限 / 抽成记账，
#      并核 INV-20 / INV-21 两条不变量。
#
#   bash tools/check/check-q5.sh
#   MMO_Q5_DB=1 bash tools/check/check-q5.sh   再加真库段（建临时账号 / 角色，跑完删）
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
# 真库段要起真正的角色服 / 网关进程；拼接文件名，避免与「核对脚本不指向服务 dll」混淆。
CHAR_SERVICE_DLL="$ROOT/src/Character/bin/Release/net10.0/""Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
TITLE="Q5（灵石修炼 / 境界任务）"
HAS_DB="${MMO_Q5_DB:-0}"
# Windows 上 dotnet 重定向输出走 GBK；带中文的断言（境界门槛「炼体」）必须按平台
# 转码后再比，不能「先试 UTF-8 再退 GBK」（同 P3-1a1 的口径）。
utf8() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      python3 -c 'import sys; sys.stdout.buffer.write(
          sys.stdin.buffer.read().decode("gbk", "replace").encode("utf-8"))' ;;
    *) cat ;;
  esac
}
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
for proj in src/Character/Character.csproj src/Gateway/Gateway.csproj \
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
if python3 "$ROOT/tools/check/check-q5-static.py" >"$TMP/static.txt" 2>&1; then
  while IFS= read -r line; do
    case "$line" in "PASS "*) ok "${line#PASS }" ;; "FAIL "*) bad "${line#FAIL }" ;; esac
  done <"$TMP/static.txt"
else
  bad "静态断言脚本自身失败"; head -8 "$TMP/static.txt" | detail
fi
if [ -s "$TMP/static.txt" ]; then
  while IFS= read -r line; do
    case "$line" in "PASS "*) ok "${line#PASS }" ;; "FAIL "*) bad "${line#FAIL }" ;; esac
  done <"$TMP/static.txt"
else
  bad "静态断言一段都没出来（脚本自身崩了）"
fi

echo "== 3. 只读入口：任务 / 门槛 / 奖励 / 周期键 =="
CFG="$("$DOTNET" "$PROBE_DLL" --city-task-config 2>&1)"
CFGU="$(printf '%s' "$CFG" | utf8)"
printf '%s' "$CFG" | grep -q '^city_task_config_state=ok version=2 ' \
  && ok "任务表读得出来（version=2）" \
  || bad "任务表只读入口不对：$(printf '%s' "$CFG" | head -1)"
printf '%s' "$CFG" | grep -q 'qiyun_cultivate_daily:qiyun:daily:1:level1:realm-:stone_1=5' \
  && ok "日常任务：次数 1、等级 1、不卡境界、奖励 stone_1=5（全部来自配置）" \
  || bad "日常任务字段不对：$(printf '%s' "$CFG" | head -1)"
printf '%s' "$CFGU" | grep -q 'qiyun_realm_weekly:qiyun:weekly:1:level0:realm炼体:stone_1=20' \
  && ok "境界任务：周常、境界门槛炼体、奖励 stone_1=20" \
  || bad "境界任务字段不对：$(printf '%s' "$CFG" | head -1)"

B1="$("$DOTNET" "$PROBE_DLL" --city-task-config --at 2026-11-18T04:30:00+08:00 2>&1 |
  grep -o 'day:[0-9-]* week:[0-9-]*' | head -1)"
B2="$("$DOTNET" "$PROBE_DLL" --city-task-config --at 2026-11-18T05:00:00+08:00 2>&1 |
  grep -o 'day:[0-9-]* week:[0-9-]*' | head -1)"
W1="$("$DOTNET" "$PROBE_DLL" --city-task-config --at 2026-11-16T04:59:00+08:00 2>&1 |
  grep -o 'day:[0-9-]* week:[0-9-]*' | head -1)"
W2="$("$DOTNET" "$PROBE_DLL" --city-task-config --at 2026-11-16T05:00:00+08:00 2>&1 |
  grep -o 'day:[0-9-]* week:[0-9-]*' | head -1)"
[ "$B1" = "day:2026-11-17 week:2026-11-16" ] && ok "日界 04:30 仍算 11-17（业务日只由日历算）" \
  || bad "日界 04:30 不对：$B1"
[ "$B2" = "day:2026-11-18 week:2026-11-16" ] && ok "日界 05:00 进 11-18" || bad "日界 05:00 不对：$B2"
[ "$W1" = "day:2026-11-15 week:2026-11-09" ] && ok "周界周一 04:59 仍算上一周（11-09）" \
  || bad "周界 04:59 不对：$W1"
[ "$W2" = "day:2026-11-16 week:2026-11-16" ] && ok "周界周一 05:00 进本周（11-16）" || bad "周界 05:00 不对：$W2"

echo "== 4. 坏任务表起不来 =="
python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
base = json.load(open("config/city_tasks.json", encoding="utf-8"))
def case(name, mutate):
    d = json.loads(json.dumps(base)); mutate(d)
    json.dump(d, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
def t(d, i=0): return d["tasks"][i]
case("no_tasks", lambda d: d.pop("tasks", None))
case("empty_tasks", lambda d: d.update(tasks=[]))
case("bad_schema", lambda d: d.update(schema="city-task-table/9"))
case("dup_id", lambda d: t(d, 1).update(id=t(d, 0)["id"]))
case("bad_period", lambda d: t(d, 0).update(period="monthly"))
case("bad_city", lambda d: t(d, 0).update(city_id="nowhere"))
case("bad_realm", lambda d: t(d, 1).update(require_realm="仙尊"))
case("non_stone", lambda d: t(d, 0)["rewards"][0].update(currency="gold"))
case("zero_amount", lambda d: t(d, 0)["rewards"][0].update(amount=0))
case("no_rewards", lambda d: t(d, 0).pop("rewards", None))
case("unknown_key", lambda d: t(d, 0).update(bonus=1))
PY
for c in no_tasks empty_tasks bad_schema dup_id bad_period bad_city bad_realm non_stone zero_amount no_rewards unknown_key; do
  o="$("$DOTNET" "$PROBE_DLL" --city-task-config --city-task-table "$TMP/bad-$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$o" | grep -q '^config_error='; then
    ok "坏任务表 ${c}：config_error + 非 0 退出"
  else
    bad "坏任务表 ${c} 没被挡住：rc=$rc $o"
  fi
done
# 启动路径：把 character.json 的 city_task_table 指到坏表，角色服必须起不来。
python3 - "$TMP" <<'PY'
import json, sys
tmp = sys.argv[1]
c = json.load(open("config/character.json", encoding="utf-8"))
c["city_task_table"] = tmp + "/bad-non_stone.json"
json.dump(c, open(tmp + "/character-bad.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
o="$(MMO_GAMEDB= "$DOTNET" "$CHAR_SERVICE_DLL" --config "$TMP/character-bad.json" 2>&1 | head -5)"
if printf '%s' "$o" | grep -q '^config_error='; then
  ok "角色服启动路径也挡住坏任务表（config_error）"
else
  bad "角色服没挡住坏任务表：$(printf '%s' "$o" | head -1)"
fi

echo "== 5. 真库段（可选） =="
if [ "$HAS_DB" != "1" ]; then
  printf '  ..   未设 MMO_Q5_DB=1，跳过真库段（接口 / 事务 / 抽成 由 MMO_Q5_DB=1 在本机跑）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/db"
  CH_PORT=${MMO_Q5_CH_PORT:-62205}
  GW_PORT=${MMO_Q5_GW_PORT:-61205}
  ACCOUNT="q5_task_$$"
  python3 - "$TMP" "$CH_PORT" "$GW_PORT" <<'PY'
import json, sys
tmp, ch, gw = sys.argv[1], sys.argv[2], sys.argv[3]
c = json.load(open("config/character.json", encoding="utf-8"))
c["listen"] = "http://127.0.0.1:" + ch
json.dump(c, open(tmp + "/db/character.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
g = json.load(open("config/gateway.json", encoding="utf-8"))
g["listen"] = "http://127.0.0.1:" + gw
g["character_upstream"] = "http://127.0.0.1:" + ch
g["battle_upstream"] = "ws://127.0.0.1:60005/battle"
json.dump(g, open(tmp + "/db/gateway.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  printf '%s\n' "Pw_q5_7k" \
    | "$DOTNET" "$GATEWAY_DLL" --config "$TMP/db/gateway.json" \
        --seed-account "$ACCOUNT" --seed-slots 8 >/dev/null 2>&1 || true
  CH_PID="$(harness_start "$TMP/db/character.log" "$DOTNET" \
    "$CHAR_SERVICE_DLL" --config "$TMP/db/character.json")"
  harness_track_port "$CH_PORT"
  harness_wait_port "$CH_PORT" "角色服" 120 0.5 || bad "角色服没起来"
  GW_PID="$(harness_start "$TMP/db/gateway.log" "$DOTNET" "$GATEWAY_DLL" --config "$TMP/db/gateway.json")"
  harness_track_port "$GW_PORT"
  harness_wait_port "$GW_PORT" "网关" 120 0.5 || bad "网关没起来"
  TOKEN="$(curl -s -m 10 -X POST "http://127.0.0.1:$GW_PORT/login" \
    -H 'Content-Type: application/json' -d "{\"account\":\"$ACCOUNT\",\"password\":\"Pw_q5_7k\"}" \
    | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("token",""))
except Exception: print("")')"
  CHAR="$(curl -s -m 10 -X POST "http://127.0.0.1:$GW_PORT/character?token=$TOKEN" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"q5_task_$$\",\"race\":\"mai\",\"class_id\":\"warrior\"}" \
    | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")')"
  if [ -z "$TOKEN" ] || [ -z "$CHAR" ]; then
    bad "真库段：登录或建角失败（见 $TMP/db/*.log）"
  else
    ok "真库段：临时账号与角色就位（$CHAR）"
    BOARD="$(curl -s -m 10 "http://127.0.0.1:$GW_PORT/city/tasks?token=$TOKEN&character_id=$CHAR")"
    printf '%s' "$BOARD" | grep -q '"config_missing":false' \
      && printf '%s' "$BOARD" | grep -q '"task_id":"qiyun_cultivate_daily"' \
      && printf '%s' "$BOARD" | grep -q '"eligible":true' \
      && ok "面板：两条任务、门槛按库里事实值算 eligible" \
      || bad "面板不对：$(printf '%s' "$BOARD" | head -c 400)"

    R1="$(curl -s -m 10 -X POST "http://127.0.0.1:$GW_PORT/city/tasks/complete?token=$TOKEN" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CHAR,\"task_id\":\"qiyun_cultivate_daily\"}")"
    printf '%s' "$R1" | grep -q '"ok":true' \
      && printf '%s' "$R1" | grep -q '"currency":"stone_1","amount":5,"balance_before":0,"balance_after":5' \
      && ok "完成日常：发 stone_1 x5（before 0 → after 5），数量来自配置" \
      || bad "完成日常不对：$(printf '%s' "$R1" | head -c 400)"

    R2="$(curl -s -m 10 -X POST "http://127.0.0.1:$GW_PORT/city/tasks/complete?token=$TOKEN" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CHAR,\"task_id\":\"qiyun_cultivate_daily\"}")"
    printf '%s' "$R2" | grep -q '"ok":false' && printf '%s' "$R2" | grep -q '"reason":"limit_reached"' \
      && ok "同一条任务第二次完成被次数上限挡住（limit_reached，次数在配置）" \
      || bad "次数上限没起作用：$(printf '%s' "$R2" | head -c 400)"

    STONE_AFTER="$("$DOTNET" "$PROBE_DLL" --city-task-state "$CHAR" --config "$TMP/db/character.json" 2>&1 |
      grep '^city_task_state=')"
    printf '%s' "$STONE_AFTER" | grep -q 'stone_1:5' && printf '%s' "$STONE_AFTER" | grep -q 'qiyun_cultivate_daily:1/1' \
      && ok "库里事实：stone_1=5、日常进度 1/1（第二次没有双发）" \
      || bad "库里的数值不对：$STONE_AFTER"

    # 抽成：税率是运行时的城池事实值（开发库上被城战写回过），本段**不改库**：
    # 先从库里读出 tax_pct，再核这一笔的 tax = floor(20 * tax_pct / 100)。
    # 这样「按城池税记账」在没有改库的前提下也能验，跑完不给开发库留残留。
    CITY_ROW="$("$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" --siege-result-state 2>&1 |
      grep 'city=qiyun')"
    PCT="$(printf '%s' "$CITY_ROW" | grep -o 'tax_pct=[0-9]*' | head -1 | cut -d= -f2)"
    [ -n "$PCT" ] || PCT=0
    EXPECT_TAX="$(python3 -c 'import sys; print(20 * int(sys.argv[1]) // 100)' "$PCT")"
    R3="$("$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" --city-task-complete "$CHAR" \
      qiyun_realm_weekly 2>&1 | grep '^city_task_complete=')"
    printf '%s' "$R3" | grep -q 'ok ' && printf '%s' "$R3" | grep -q "tax_pct=$PCT" \
      && printf '%s' "$R3" | grep -q "stone_1:20:5->25:tax$EXPECT_TAX" \
      && ok "按城池税记账：库里 tax_pct=$PCT，周常 20 个算出 tax=$EXPECT_TAX（只记账、不从玩家扣）" \
      || bad "抽成不对（库里 tax_pct=$PCT）：$R3"
    LEDGER="$("$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" --city-task-ledger "$CHAR" 2>&1)"
    printf '%s' "$LEDGER" | grep -q 'city_task_tax_row=txn=' \
      && printf '%s' "$LEDGER" | grep -q "source=task_grant gross=20 tax=$EXPECT_TAX" \
      && ok "灵石抽成流水落库（source=task_grant / gross=20 / tax=$EXPECT_TAX，与金币账本是两张表）" \
      || bad "灵石抽成流水不对：$(printf '%s' "$LEDGER" | head -4 | tr '\n' ' ')"

    # 幂等：把刚才成功那一笔的事务号再提交一次 → duplicate，灵石不再加。
    TXN="$(printf '%s' "$R3" | grep -o 'txn=[^ ]*' | head -1 | cut -d= -f2)"
    B="$("$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" --city-task-complete "$CHAR" \
      qiyun_realm_weekly --city-task-txn "$TXN" 2>&1 | grep '^city_task_complete=')"
    ST2="$("$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" --city-task-state "$CHAR" 2>&1 |
      grep '^city_task_state=')"
    printf '%s' "$B" | grep -q 'reason=duplicate' \
      && printf '%s' "$ST2" | grep -q 'stone_1:25' \
      && ok "同一事务号重放回 duplicate：灵石仍是 25，不二次发" \
      || bad "重放没挡住：$B / $ST2"

    INV="$("$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" --invariants 2>&1)"
    printf '%s' "$INV" | grep -q '^invariant=ok id=INV-20 ' \
      && printf '%s' "$INV" | grep -q '^invariant=ok id=INV-21 ' \
      && ok "INV-20 / INV-21 在这一库上 0 违反（进度与账本一致、都走 grant_txn）" \
      || bad "不变量有违反：$(printf '%s' "$INV" | grep '^invariant=fail' | head -3 | tr '\n' ' ')"

    "$DOTNET" "$PROBE_DLL" --config "$TMP/db/character.json" \
      --delete-character "q5_task_$$" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" \
      --config "$TMP/db/gateway.json" >/dev/null 2>&1 || true
  fi
  harness_cleanup
fi

finish "$TITLE"
