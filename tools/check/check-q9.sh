#!/usr/bin/env bash
# tools/check/check-q9.sh —— Q9 H4 断线重连 / 逃跑惩罚（阶段 2 第九张）。
#
# 完成标准（未覆盖排期第 58 行）：
#   窗口内重连回同一场；超时 / 主动退有惩罚；不重复结算；
#   buff / DoT 剩余时长按服务端时钟恢复（与 Q2 同一套时钟）。
#
# 本脚本：
#   1. 编译战斗服 / 角色服 / 网关 / 战斗探针；
#   2. 静态断言（配置正本、proto 面、惩罚落库、结算只写一次、流水线挂载）；
#   3. 只读：启动行 reconnect= 的三个数字现取，与 config/reconnect.json 一致；
#   4. 坏重连配置 6 类都以 config_error= 起不来（缺字段 / 越界 / 顺序 / 未知键）；
#   5. 真服 WebSocket（无库）：宽限内无缝续战、宽限过重新入场同一场、
#      窗口到点被拒、主动退立即被拒、结算后重连不重复结算；
#   6. 有库时（MMO_Q9_DB=1）：角色服落 deserter_penalty 一行，/dungeon/enter
#      被拒且疲劳不变，重启战斗服后惩罚还在（说明事实值在库里、不在进程内存）。
#
#   bash tools/check/check-q9.sh
#   MMO_Q9_DB=1 bash tools/check/check-q9.sh   再加真库段
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CHAR_DLL="$ROOT/src/Character/bin/Release/net10.0/""Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
BATTLE_PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
CHAR_PROBE="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TITLE="Q9（H4 断线重连 / 逃跑惩罚）"
HAS_DB="${MMO_Q9_DB:-0}"
TMP="$(mktemp -d)"
TMP="${TMP//\\//}"
PIDS=""
cleanup() {
  for p in $PIDS; do stop_pid "$p"; done
  rm -rf "$TMP" 2>/dev/null || true
  rm -f "$ROOT"/config/battle-q9-*.json 2>/dev/null || true
}
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Battle/Battle.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj \
            tools/probe/BattleProbe/BattleProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 $n"
  else
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ "$BUILD_OK" = "1" ] || { finish "$TITLE"; exit 1; }

echo "== 2. 静态断言 =="
python3 "$ROOT/tools/check/check-q9-static.py" >"$TMP/static.txt" 2>&1
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

echo "== 3. 临时配置与启动行（三个数字现取）=="
mkdir -p "$TMP/cfg" "$TMP/settle" "$TMP/chr"
cp "$ROOT"/config/*.json "$TMP/cfg/" 2>/dev/null || true
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
cfg = os.path.join(tmp, "cfg")

b = json.load(open(os.path.join(cfg, "battle.json"), encoding="utf-8"))
b["listen"] = "127.0.0.1:0"
b["settlement_dir"] = os.path.join(tmp, "settle")
b["character_dir"] = os.path.join(tmp, "chr")
json.dump(b, open(os.path.join(cfg, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)

# 三个数字压到毫秒级：核对不按产品的 30s / 120s / 300s 等，全部从这份临时配置现取。
r = json.load(open(os.path.join(cfg, "reconnect.json"), encoding="utf-8"))
r["battle_grace_ms"] = 800
r["reconnect_window_s"] = 2
r["deserter_penalty_s"] = 30
json.dump(r, open(os.path.join(cfg, "reconnect.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY

GRACE="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["battle_grace_ms"])' "$TMP/cfg/reconnect.json")"
WINDOW_MS="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["reconnect_window_s"]*1000)' "$TMP/cfg/reconnect.json")"
PENALTY_MS="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["deserter_penalty_s"]*1000)' "$TMP/cfg/reconnect.json")"

start_battle() {   # $1 = 配置路径，$2 = 日志
  "$DOTNET" "$BATTLE_DLL" --config "$1" >"$2" 2>&1 &
  PIDS="$PIDS $!"
  for _ in $(seq 1 80); do
    grep -q '^listen=' "$2" 2>/dev/null && return 0
    sleep 0.2
  done
  return 1
}

if start_battle "$TMP/cfg/battle.json" "$TMP/battle.log"; then
  ok "战斗服就绪（临时配置，listen 0 随机端口）"
else
  bad "战斗服没起来"; tail -8 "$TMP/battle.log" | detail
fi
LINE="$(grep -m1 '^reconnect_table=' "$TMP/battle.log" 2>/dev/null || true)"
printf '%s' "$LINE" | grep -q "battle_grace_ms=${GRACE}" \
  && ok "启动行宽限现取（${GRACE}）" || bad "启动行宽限不对：$LINE"
printf '%s' "$LINE" | grep -q "reconnect_window_ms=${WINDOW_MS}" \
  && ok "启动行窗口现取（${WINDOW_MS}）" || bad "启动行窗口不对：$LINE"
printf '%s' "$LINE" | grep -q "deserter_penalty_ms=${PENALTY_MS}" \
  && ok "启动行惩罚现取（${PENALTY_MS}）" || bad "启动行惩罚不对：$LINE"
BU="$(printf '%s' "$LINE" >/dev/null; grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/battle.log" | tail -1 | sed 's/^listen=//')"
WU="${BU/http:\/\//ws://}"
[ -n "$WU" ] && ok "拿到战斗服地址（${WU}）" || bad "没拿到监听地址"

echo "== 4. 坏重连配置 6 类都起不来 =="
BAD_RC=0
for c in noschema gracezero windowzero penaltyneg order unknown; do
  # 坏表必须放在一份完整的临时配置目录里当 reconnect.json，否则「起不来」的原因
  # 会是别的表缺失（假绿）。这里复制一份完整配置，只把 reconnect.json 写坏。
  mkdir -p "$TMP/bad-$c"
  cp "$TMP/cfg/"*.json "$TMP/bad-$c/" 2>/dev/null || true
  python3 - "$TMP/cfg/reconnect.json" "$TMP/bad-$c/reconnect.json" "$c" <<'PY'
import json, sys
good, path, case = sys.argv[1], sys.argv[2], sys.argv[3]
d = json.load(open(good, encoding="utf-8"))
if case == "noschema":
    d.pop("schema", None)
elif case == "gracezero":
    d["battle_grace_ms"] = 0
elif case == "windowzero":
    d["reconnect_window_s"] = 0
elif case == "penaltyneg":
    d["deserter_penalty_s"] = -1
elif case == "order":
    d["battle_grace_ms"] = d["reconnect_window_s"] * 1000 + 1
elif case == "unknown":
    d["battle_grace_seconds"] = 30
json.dump(d, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  out="$("$DOTNET" "$BATTLE_DLL" --config "$TMP/bad-$c/battle.json" 2>&1)"; rc=$?
  assert_bad_config "$out" "坏重连配置 ${c}：$(printf '%s' "$out" | head -1 | cut -c1-56)"
  # 再钉一次原因：必须是真的撞在重连表上，不能是别的表缺文件。
  printf '%s' "$out" | grep -qE 'reconnect|battle_grace_ms|reconnect_window_s|deserter_penalty_s|schema' \
    && ok "坏配置 ${c} 的原因指向重连表" \
    || bad "坏配置 ${c} 起不来但不是重连表的问题：$(printf '%s' "$out" | head -1)"
  [ "$rc" -ne 0 ] || BAD_RC=1
done
[ "$BAD_RC" = "0" ] && ok "6 类坏配置都以非 0 退出" || bad "有坏配置以 0 退出"

# 正本反过来：仓库配置必须读得进来，且三个数字与文件现值逐一一致（否则上面全是假红）。
GOOD="$("$DOTNET" "$BATTLE_DLL" --reconnect-state 2>&1 || true)"
printf '%s' "$GOOD" | grep -q '^reconnect_state=ok' \
  && ok "仓库配置可读（--reconnect-state）" \
  || { bad "仓库配置读不进来：$GOOD"; }
REPO_GRACE="$(python3 -c 'import json;print(json.load(open("config/reconnect.json"))["battle_grace_ms"])')"
REPO_WINDOW="$(python3 -c 'import json;print(json.load(open("config/reconnect.json"))["reconnect_window_s"]*1000)')"
REPO_PENALTY="$(python3 -c 'import json;print(json.load(open("config/reconnect.json"))["deserter_penalty_s"]*1000)')"
printf '%s' "$GOOD" | grep -q "grace_ms=${REPO_GRACE} window_ms=${REPO_WINDOW} penalty_ms=${REPO_PENALTY}" \
  && ok "只读入口三个数字与 config 现值一致" \
  || bad "只读入口与 config 不一致：$GOOD"

echo "== 5. 真服 WebSocket（无库）=="
BOSS="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["boss"]["id"])' "$TMP/cfg/battle.json")"
SKILL="$(python3 -c '
import json
rows = json.load(open("config/skills.json"))["skills"]
print(max(rows, key=lambda x: x.get("range_mm", 0))["id"])')"
DUNGEON="$(python3 -c '
import json
rows = json.load(open("config/dungeons.json"))
rows = rows.get("dungeons", rows) if isinstance(rows, dict) else rows
rows = [d for d in rows if d.get("max_players", 1) >= 2]
rows.sort(key=lambda d: d.get("max_players", 1))
print(rows[0]["id"] if rows else "main")')"

run_case() {   # $1 = case, 其余原样传给探针，打印 stdout
  local name="$1"; shift
  "$DOTNET" "$BATTLE_PROBE" --case "$name" "$@" 2>&1
}

# ① 宽限内回来：同一场、同一位置、同一血量（续战，不是重新入场）
OUT="$(run_case q9_seamless --url "$WU/battle?party=q9a&char=41" --wait-ms $((GRACE / 3)))"
printf '%s' "$OUT" | grep -q '^verdict=ok' && ok "宽限内续战：$(printf '%s' "$OUT" | grep -m1 '^case=')" \
  || { bad "宽限内续战没通过"; printf '%s' "$OUT" | head -4 | detail; }

# ② 宽限过了、窗口没到、队友还在：同一场重新入场（满血、出生点），不判逃跑
URL_A="$WU/battle?party=q9b&char=42&dungeon=$DUNGEON&party_size=2"
URL_B="$WU/battle?party=q9b&char=43&dungeon=$DUNGEON&party_size=2"
OUT="$(run_case q9_rejoin --url "$URL_A" --b-url "$URL_B" --wait-ms $((GRACE + 400)))"
printf '%s' "$OUT" | grep -q '^verdict=ok' && ok "宽限后重新入场同一场：$(printf '%s' "$OUT" | grep -m1 '^case=')" \
  || { bad "宽限后重新入场没通过"; printf '%s' "$OUT" | head -4 | detail; }

# ③ 窗口到点没回来：再连 403 deserter_penalty
OUT="$(run_case q9_deserter --url "$WU/battle?party=q9c&char=44" --wait-ms $((WINDOW_MS + 500)))"
printf '%s' "$OUT" | grep -q '^verdict=ok' && ok "窗口超时被判逃跑：$(printf '%s' "$OUT" | grep -m1 '^case=')" \
  || { bad "窗口超时没判逃跑"; printf '%s' "$OUT" | head -4 | detail; }

# ④ 主动退（Close 帧）：不给宽限，立刻被拒
OUT="$(run_case q9_voluntary --url "$WU/battle?party=q9d&char=45" --wait-ms 300)"
printf '%s' "$OUT" | grep -q '^verdict=ok' && ok "主动退立即被判逃跑：$(printf '%s' "$OUT" | grep -m1 '^case=')" \
  || { bad "主动退没被判逃跑"; printf '%s' "$OUT" | head -4 | detail; }

# ⑤ 结算后掉线重连：只开新的一场，不重复结算
BEFORE_SETTLE="$(ls "$TMP/settle" 2>/dev/null | wc -l | tr -d ' ')"
OUT="$(run_case q9_settled_once --url "$WU/battle?party=q9e&char=46&dungeon=$DUNGEON&party_size=2" \
  --b-url "$WU/battle?party=q9e&char=47&dungeon=$DUNGEON&party_size=2" \
  --boss "$BOSS" --ranged "$SKILL" --wait-ms 400)"
printf '%s' "$OUT" | grep -q '^verdict=ok' && ok "结算后重连只回原 Ended：$(printf '%s' "$OUT" | grep -m1 '^case=')" \
  || { bad "结算后重连不通过"; printf '%s' "$OUT" | head -4 | detail; }
AFTER_SETTLE="$(ls "$TMP/settle" 2>/dev/null | wc -l | tr -d ' ')"
[ $((AFTER_SETTLE - BEFORE_SETTLE)) -eq 1 ] \
  && ok "结算记录只多一条（重连没有第二次结算，现取 ${BEFORE_SETTLE}→${AFTER_SETTLE}）" \
  || bad "结算记录多了 $((AFTER_SETTLE - BEFORE_SETTLE)) 条（期望 1）"

echo "-- 日志侧证据（服务端自己打的 kv 行）--"
grep -q 'reconnect_resumed=' "$TMP/battle.log" && ok "战斗服记了续战（reconnect_resumed）" \
  || bad "没有 reconnect_resumed"
grep -q 'reconnect_grace_expired=' "$TMP/battle.log" && ok "战斗服记了宽限到点（reconnect_grace_expired）" \
  || bad "没有 reconnect_grace_expired"
grep -q 'deserter_penalty=.*reason=timeout' "$TMP/battle.log" && ok "超时判罚（reason=timeout）" \
  || bad "没有 timeout 判罚"
grep -q 'deserter_penalty=.*reason=voluntary' "$TMP/battle.log" && ok "主动退判罚（reason=voluntary）" \
  || bad "没有 voluntary 判罚"
grep -q 'reconnect_settled=' "$TMP/battle.log" && ok "已收尾的场只回 Ended（reconnect_settled）" \
  || bad "没有 reconnect_settled"

echo "== 6. 真库段（逃跑惩罚落表 / 进本被挡 / 重启后还在）=="
if [ "$HAS_DB" != "1" ]; then
  printf '       （真库段未跑：MMO_Q9_DB 未置 1；本机段已跑完，不伪造）\n'
elif [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  printf '       （真库段未跑：缺 MMO_GAMEDB / /srv/mmo/gamedb.env；本机段已跑完）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/harness"
  for p in 65000 62000 61000; do harness_track_port "$p"; done
  CC="--config $ROOT/config/character.json"
  CG="--config $ROOT/config/gateway.json"
  GATEWAY="http://127.0.0.1:61000"

  bash "$ROOT/tools/env/db-migrate.sh" >"$TMP/migrate.log" 2>&1 \
    && ok "gamedb 迁移（幂等，本卡只加一张表）" || { bad "迁移失败"; tail -5 "$TMP/migrate.log" | detail; }

  ACCOUNT="q9_$$"; PASSWORD="Q9-$$-$(date +%s)"
  printf '%s\n' "$PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$ACCOUNT" --seed-slots 4 $CG >/dev/null 2>&1
  ok "自造临时账号（口令 / 令牌只在内存）"

  harness_start "$TMP/harness/character.log" "$DOTNET" "$CHAR_DLL" $CC >/dev/null
  harness_start "$TMP/harness/gateway.log" "$DOTNET" "$GATEWAY_DLL" $CG >/dev/null
  harness_wait_ready "$TMP/harness/character.log" "角色服" || bad "角色服没起来"
  harness_wait_ready "$TMP/harness/gateway.log" "网关" || bad "网关没起来"
  harness_wait_http "$GATEWAY/health" 200 \
    && ok "角色服 + 网关就绪" || bad "有服务没起来"

  TOK="$(harness_login 61000 "$ACCOUNT" "$PASSWORD")"
  [ -n "$TOK" ] && ok "登录拿到令牌" || bad "登录没拿到令牌"
  CID="$(curl -sS --noproxy '*' -m 10 -X POST "$GATEWAY/character?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"q9_$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>/dev/null \
    | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")')"
  [ -n "$CID" ] && ok "造一个角色（${CID}）" || bad "角色没建出来"

  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
cfg = os.path.join(tmp, "cfg")
b = json.load(open(os.path.join(cfg, "battle.json"), encoding="utf-8"))
b["listen"] = "127.0.0.1:0"
b["exp_upstream"] = "http://127.0.0.1:62000"
b["character_upstream"] = "http://127.0.0.1:62000"
json.dump(b, open(os.path.join(cfg, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  start_battle "$TMP/cfg/battle.json" "$TMP/battle-db.log" || bad "带角色服的战斗服没起来"
  BU2="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/battle-db.log" | tail -1 | sed 's/^listen=//')"
  WU2="${BU2/http:\/\//ws://}"

  # ① 主动退 → 战斗服判罚 → 角色库有一行（只读口现取，不猜数值）
  OUT="$(run_case q9_voluntary --url "$WU2/battle?party=q9db&char=$CID" --wait-ms 400)"
  printf '%s' "$OUT" | grep -q '^verdict=ok' && ok "真库：主动退被判逃跑" \
    || { bad "真库：主动退没被判"; printf '%s' "$OUT" | head -4 | detail; }
  FACT="$("$DOTNET" "$BATTLE_DLL" --config "$TMP/cfg/battle.json" --deserter-state "$CID" 2>&1 | grep -m1 '^deserter=' || true)"
  printf '%s' "$FACT" | grep -q 'found=true' && ok "真库：角色库有惩罚行（${FACT}）" \
    || bad "真库：角色库没有惩罚行（${FACT}）"
  printf '%s' "$FACT" | grep -q 'reason=voluntary' && ok "真库：原因记 voluntary" \
    || bad "真库：原因不对（${FACT}）"

  # ② /dungeon/enter 被拒，且疲劳不变（惩罚挡在扣减之前）
  enter() {
    curl -sS --noproxy '*' -m 10 -X POST "$GATEWAY/dungeon/enter" \
      -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"party_size\":1,\"dungeon_id\":\"main\"}" 2>&1
  }
  REPLY="$(enter)"
  printf '%s' "$REPLY" | grep -q '"reason":"deserter_penalty"' \
    && ok "真库：/dungeon/enter 回 deserter_penalty" || { bad "真库：进本没被挡"; printf '%s' "$REPLY" | head -3 | detail; }
  printf '%s' "$REPLY" | python3 -c '
import json,sys
d=json.loads(sys.stdin.read().strip())
sys.exit(0 if (not d.get("allowed")) and d.get("deserter_remaining_s",0)>0 and d.get("fatigue_cost",0)==0 else 1)' \
    && ok "真库：allowed=false、剩余秒数 >0、疲劳扣减为 0" \
    || { bad "真库：回包字段不对"; printf '%s' "$REPLY" | head -3 | detail; }
  FATIGUE_BEFORE="$(printf '%s' "$REPLY" | python3 -c 'import json,sys;print(json.load(sys.stdin).get("fatigue",""))')"
  REPLY2="$(enter)"
  FATIGUE_AFTER="$(printf '%s' "$REPLY2" | python3 -c 'import json,sys;print(json.load(sys.stdin).get("fatigue",""))')"
  [ -n "$FATIGUE_BEFORE" ] && [ "$FATIGUE_BEFORE" = "$FATIGUE_AFTER" ] \
    && ok "真库：重复被拒后疲劳没变（${FATIGUE_BEFORE}）" \
    || bad "真库：疲劳变了 ${FATIGUE_BEFORE}→${FATIGUE_AFTER}"

  # ③ 重启战斗服：惩罚还在（说明在库里，不在进程内存）
  harness_kill_port "$(printf '%s' "$WU2" | sed -E 's/.*:([0-9]+)$/\1/')" 2>/dev/null || true
  start_battle "$TMP/cfg/battle.json" "$TMP/battle-db2.log" || bad "重启的战斗服没起来"
  BU3="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/battle-db2.log" | tail -1 | sed 's/^listen=//')"
  WU3="${BU3/http:\/\//ws://}"
  OUT="$(run_case q9_blocked --url "$WU3/battle?party=q9db2&char=$CID")"
  printf '%s' "$OUT" | grep -q '^verdict=ok' \
    && ok "真库：重启战斗服后仍然被挡（惩罚在库里）" \
    || { bad "真库：重启后惩罚丢了"; printf '%s' "$OUT" | head -4 | detail; }
fi

finish "$TITLE"
