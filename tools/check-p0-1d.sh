#!/usr/bin/env bash
# P0-1d 核对：前摇里打断没有伤害；进程重启后，没写入的奖励不会出现。
#
# 做这几件事：
#   1. 协议与源码口径：取消的字段号没动、取消要退回冷却、断线不写结算、结算只写在收尾那一处
#   2. 编译战斗进程
#   3. 起进程 A（端口让系统给，结算落临时目录）
#   4. 客户端在前摇里发取消：撤掉要 accepted，撤掉后不能有迟到的结算，冷却退回后立刻能再发
#   5. 打掉一截血但不通关，直接 kill -9。重启应当仍是零条结算、新一场满血
#   6. 打满四下通关写一条结算，再 kill -9。重启后仍是那一条，不补发，新一场满血
#   7. 客户端这侧不许算伤害、不许碰结算
#
# 用法：tools/check-p0-1d.sh
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CLIENT_PROJ="$ROOT/client/MMOGame"

PASS=0
FAIL=0
TMP="$(mktemp -d)"
BATTLE_PID=""

cleanup() {
  if [ -n "$BATTLE_PID" ] && kill -0 "$BATTLE_PID" 2>/dev/null; then
    kill -TERM "$BATTLE_PID" 2>/dev/null || true
    wait "$BATTLE_PID" 2>/dev/null || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
detail() { sed 's/^/       /'; }

# 从 "p01d_xxx=key=值 key=值" 这一行里取一个字段。
# 先剥掉 p01d_ 前缀按空格拆词找 key=；找不到就取前缀后面那一段本身
# （客户端有些行是 p01d_xxx=值 的形式，没有内层 key）。
pick() {
  local body="${1#*=}"
  local v
  v="$(printf '%s' "$body" | tr ' ' '\n' | sed -n "s/^$2=//p" | head -1)"
  if [ -n "$v" ]; then
    printf '%s' "$v"
  else
    printf '%s' "${body%% *}"
  fi
}

settlement_count() {
  find "$1" -type f -name "*.json" 2>/dev/null | wc -l | tr -d ' '
}

# 起一个战斗进程，等它把 listen= 打出来，回填 WS_URL。
# 参数：日志文件、配置文件。
start_battle() {
  local log="$1" cfg="$2"
  : > "$log"
  "$DOTNET" "$BATTLE_DLL" --config "$cfg" >>"$log" 2>&1 &
  BATTLE_PID=$!
  WS_URL=""
  local i
  for i in $(seq 1 150); do
    if grep -q '^listen=' "$log" 2>/dev/null; then
      local addr
      addr="$(grep -m1 '^listen=' "$log" | cut -d= -f2-)"
      WS_URL="$(printf '%s' "$addr" | sed 's|^http|ws|')/battle"
      return 0
    fi
    if ! kill -0 "$BATTLE_PID" 2>/dev/null; then
      return 1
    fi
    sleep 0.1
  done
  return 1
}

# 跑一段客户端核对。参数：模式、日志文件。输出写进 UNITY_LOG。
run_client() {
  local mode="$1" log="$2"
  MMO_BATTLE_URL="$WS_URL" \
  MMO_P01D_MODE="$mode" \
  MMO_BATTLE_SKILL="$SKILL_ID" \
  MMO_BATTLE_BOSS="$BOSS_ID" \
    "$UNITY" -batchmode -quit -projectPath "$CLIENT_PROJ" \
    -executeMethod P01dCheck.Run -logFile "$log" >/dev/null 2>&1
  return $?
}

echo "== 1. 协议与源码口径 =="
PROTO="$ROOT/proto/mmo/battle.proto"
if grep -q 'SkillCancelRequest cancel = 2;' "$PROTO" \
   && grep -q 'SkillCancelled cancelled = 4;' "$PROTO" \
   && grep -q 'bool accepted = 3;' "$PROTO"; then
  ok "取消请求与回执的字段号没动"
else
  bad "取消请求或回执的字段号被改过"
fi

ENC="$ROOT/src/Battle/Encounter.cs"
# P0-3 起冷却挪到每个成员自己的表上（member.ReadyAtMs），
# 但「取消成功要把这一手的冷却退回出手前的值」这条规则没变。
if grep -q 'bool accepted = 3;' "$PROTO" 2>/dev/null && grep -q 'ReadyAtMs\[pending.Skill.Id\] = pending.PrevReadyAtMs;' "$ENC"; then
  ok "取消成功的分支把冷却退回出手前的值"
else
  bad "取消之后没有把冷却退回去，撤掉一次会白等一段冷却"
fi

# 结算只能写在收尾那一处。断线、取消都不能顺手把奖励写出去。
WRITE_SITES="$(grep -c '_settlements.Write(' "$ENC" 2>/dev/null || true)"
if [ "${WRITE_SITES:-0}" = "1" ]; then
  ok "结算写入只有一处"
else
  bad "结算写入出现了 ${WRITE_SITES:-0} 处，奖励可能从不该写的地方漏出去"
fi

if python3 - "$ENC" <<'PY'
import re, sys
src = open(sys.argv[1], encoding="utf-8").read()
m = re.search(r'public void Abandon\(\)(.*?)\n    \}', src, re.S)
body = m.group(1) if m else ""
sys.exit(0 if m and "Write(" not in body else 1)
PY
then
  ok "断线走 Abandon，不写结算"
else
  bad "断线那条路上会写结算，重启后就会多出没打完的奖励"
fi

if grep -qE 'File\.(Write|Append|Create)|Directory\.Create|Npgsql|StreamWriter' \
     -r "$ROOT/client/MMOGame/Assets/Net" "$ROOT/client/MMOGame/Assets/HotUpdate" 2>/dev/null; then
  bad "客户端在写结算，这些只能来自服务端"
else
  ok "客户端只读结算号，不落盘"
fi

if grep -qE 'Hp(Max)?[[:space:]]*[-+]=|(damage|Damage)[[:space:]]*[-+]=' \
     -r "$ROOT/client/MMOGame/Assets/Net" "$ROOT/client/MMOGame/Assets/HotUpdate" 2>/dev/null; then
  bad "客户端在算血量和伤害"
else
  ok "客户端没有算血量和伤害"
fi

echo "== 2. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet，没法编译战斗进程"
  echo
  echo "P0-1d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Battle/Battle.csproj" -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "战斗进程编译通过"
else
  bad "战斗进程编译失败"
  grep -E "error " "$TMP/build.log" | head -5 | detail
fi

if [ "$FAIL" -gt 0 ]; then
  echo
  echo "P0-1d 核对：$PASS 项通过，$FAIL 项未通过（前面已有未通过项，后面依赖它的不跑）"
  exit 1
fi

if [ ! -x "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  echo
  echo "P0-1d 核对：$PASS 项通过，$FAIL 项未通过（客户端这半边没跑）"
  exit 1
fi

echo "== 3. 准备临时配置 =="
mkdir -p "$TMP/settlements"
python3 - "$ROOT" "$TMP" <<'PY' > "$TMP/expect.env"
import json, os, sys

root, tmp = sys.argv[1], sys.argv[2]
battle = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
battle["listen"] = "127.0.0.1:0"
battle["settlement_dir"] = os.path.join(tmp, "settlements")
battle["character_dir"] = os.path.join(tmp, "characters")
battle["skill_table"] = os.path.join(root, "config", "skills.json")
battle["class_table"] = os.path.join(root, "config", "classes.json")
battle["dungeon_table"] = os.path.join(root, "config", "dungeons.json")
json.dump(battle, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)

cls_id = battle["default_class"]
skills = [s for s in json.load(open(os.path.join(root, "config", "skills.json"), encoding="utf-8"))["skills"]
          if s["class_id"] == cls_id]
skill = max(skills, key=lambda s: s["range_mm"])

# 期望伤害 = 攻击 × 倍率，和 p0-1c 同一个算法。
classes = json.load(open(os.path.join(root, "config", "classes.json"), encoding="utf-8"))
cls = next(c for c in classes["classes"] if c["id"] == cls_id)
primary = cls["attributes"].get(cls["primary"], 0) if cls["primary"] != "none" else 0
attack = primary * cls["attack_per_primary"] + cls["equipment_attack"]
damage = int(round(attack * skill["damage_multiplier"]))

a, b = battle["caster"]["spawn_mm"], battle["boss"]["spawn_mm"]
dist = int(round(((a["x"] - b["x"]) ** 2 + (a["y"] - b["y"]) ** 2) ** 0.5))

print("BOSS_ID=%d" % battle["boss"]["id"])
print("BOSS_HP_MAX=%d" % battle["boss"]["hp_max"])
print("DIST=%d" % dist)
print("SKILL_ID=%d" % skill["id"])
print("SKILL_DAMAGE=%d" % damage)
print("SKILL_RANGE=%d" % skill["range_mm"])
print("SKILL_FORESWING=%d" % skill["foreswing_ms"])
print("SKILL_RECOVERY=%d" % skill["recovery_ms"])
print("SKILL_COOLDOWN=%d" % skill["cooldown_ms"])
PY

# shellcheck disable=SC1090
. "$TMP/expect.env"

if [ "$SKILL_RANGE" -gt "$DIST" ]; then
  ok "挑中的技能够得着站位（${SKILL_RANGE}mm > ${DIST}mm）"
else
  bad "挑中的技能够不着站位"
fi

# 打断要落在前摇里，观察窗要长过前摇，两次出手之间要让冷却过去。
if [ "$SKILL_FORESWING" -gt 300 ] && [ "$SKILL_COOLDOWN" -gt "$SKILL_RECOVERY" ]; then
  ok "时序够用：前摇 ${SKILL_FORESWING}ms、后摇 ${SKILL_RECOVERY}ms、冷却 ${SKILL_COOLDOWN}ms"
else
  bad "技能时序不合适：前摇 ${SKILL_FORESWING}ms、冷却 ${SKILL_COOLDOWN}ms"
fi

# 打满四下能通关，打断那一轮打两下又不会打死。
CASTS_TO_CLEAR=$(( (BOSS_HP_MAX + SKILL_DAMAGE - 1) / SKILL_DAMAGE ))
if [ "$CASTS_TO_CLEAR" -ge 2 ] && [ "$CASTS_TO_CLEAR" -le 6 ]; then
  ok "打 ${CASTS_TO_CLEAR} 下能通关，够跑完一轮还能留出打断的余地"
else
  bad "打 ${CASTS_TO_CLEAR} 下才能通关，节奏不适合这张卡"
fi

echo "== 4. 起进程 A =="
if start_battle "$TMP/a.log" "$TMP/battle.json"; then
  ok "战斗进程起来了：${WS_URL}"
else
  bad "战斗进程没起来"
  tail -10 "$TMP/a.log" | detail
  echo
  echo "P0-1d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if grep -q '^resume=off$' "$TMP/a.log" && grep -q '^settlements=0$' "$TMP/a.log"; then
  ok "启动时声明不恢复场次，结算目录是空的"
else
  bad "启动日志里 resume 或 settlements 不对"
  grep -E '^resume=|^settlements=' "$TMP/a.log" | detail
fi

echo "== 5. 前摇里打断 =="
CANCEL_LOG="$TMP/cancel.log"
run_client cancel "$CANCEL_LOG"
CANCEL_EXIT=$?

if grep -qE "error CS|Compilation failed" "$CANCEL_LOG" 2>/dev/null; then
  bad "客户端编译失败"
  grep -E "error CS" "$CANCEL_LOG" | head -5 | detail
fi

if [ "$CANCEL_EXIT" -eq 0 ]; then
  ok "客户端跑完打断这一轮，退出码 0"
else
  bad "客户端退出码 ${CANCEL_EXIT}"
  grep -E "^p01d_|Exception" "$CANCEL_LOG" | head -8 | detail
fi

OPENED="$(grep -m1 '^p01d_opened=' "$CANCEL_LOG" 2>/dev/null)"
OPENED_HP="$(pick "$OPENED" hp)"
SESSION_A="$(pick "$OPENED" session)"
if [ "${OPENED_HP:-}" = "$BOSS_HP_MAX" ]; then
  ok "开场满血 ${OPENED_HP}/${BOSS_HP_MAX}"
else
  bad "开场血量不是满的：${OPENED_HP:-?}/${BOSS_HP_MAX}"
fi

CANCEL="$(grep -m1 '^p01d_cancel=' "$CANCEL_LOG" 2>/dev/null)"
if [ "$(pick "$CANCEL" accepted)" = "True" ]; then
  ok "前摇里取消被接受（$(pick "$CANCEL" elapsed_ms)ms 拿到回执）"
else
  bad "前摇里取消没被接受：${CANCEL:-没这行}"
fi

LEAK="$(grep -m1 '^p01d_cancel_leak=' "$CANCEL_LOG" 2>/dev/null)"
if [ -z "$LEAK" ]; then
  bad "没有 cancel_leak 这一行"
else
  LEAK_COUNT="$(pick "$LEAK" cancel_leak)"
  LEAK_DAMAGE="$(pick "$LEAK" late_damage)"
  if [ "${LEAK_COUNT:-1}" = "0" ] && [ "${LEAK_DAMAGE:-1}" = "0" ]; then
    ok "打断之后没有迟到的结算，伤害是 0"
  else
    bad "打断之后还是结算了 ${LEAK_COUNT:-?} 次，伤害 ${LEAK_DAMAGE:-?}"
  fi
fi

AFTER="$(grep -m1 '^p01d_after_cancel=' "$CANCEL_LOG" 2>/dev/null)"
AFTER_KIND="$(pick "$AFTER" after_cancel)"
AFTER_HP="$(pick "$AFTER" hp_after)"
EXPECT_AFTER_HP=$(( BOSS_HP_MAX - SKILL_DAMAGE ))
if [ "$AFTER_KIND" = "result" ] && [ "${AFTER_HP:-}" = "$EXPECT_AFTER_HP" ]; then
  ok "取消把冷却退回出手前，同一技能立刻能再发（hp ${BOSS_HP_MAX}→${AFTER_HP}）"
else
  bad "取消之后没能立刻再发：${AFTER:-没这行}"
fi

LATE="$(grep -m1 '^p01d_late_cancel=' "$CANCEL_LOG" 2>/dev/null)"
if [ "$(pick "$LATE" accepted)" = "False" ]; then
  ok "已经结算完再发取消，回执是撤不回来"
else
  bad "结算之后取消居然还能撤掉：${LATE:-没这行}"
fi

PARTIAL="$(pick "$(grep -m1 '^p01d_partial_hp=' "$CANCEL_LOG" 2>/dev/null)" partial_hp)"
EXPECT_PARTIAL=$(( BOSS_HP_MAX - 2 * SKILL_DAMAGE ))
if [ "${PARTIAL:-}" = "$EXPECT_PARTIAL" ]; then
  ok "没通关那一轮把血打到 ${PARTIAL}/${BOSS_HP_MAX}"
else
  bad "没通关那一轮血量对不上：${PARTIAL:-?}，应为 ${EXPECT_PARTIAL}"
fi

echo "== 6. 打了一半崩掉，重启是干净的 =="
kill -9 "$BATTLE_PID" 2>/dev/null || true
wait "$BATTLE_PID" 2>/dev/null || true
BATTLE_PID=""

CRASH_COUNT="$(settlement_count "$TMP/settlements")"
if [ "$CRASH_COUNT" = "0" ]; then
  ok "打了一半就崩，结算目录还是零条"
else
  bad "没通关却写了 ${CRASH_COUNT} 条结算"
fi

if start_battle "$TMP/b.log" "$TMP/battle.json"; then
  ok "崩完能重新起来"
else
  bad "崩完起不来"
  tail -10 "$TMP/b.log" | detail
fi

RESTART_COUNT="$(grep -m1 '^settlements=' "$TMP/b.log" 2>/dev/null | cut -d= -f2)"
if [ "${RESTART_COUNT:-}" = "0" ]; then
  ok "重启扫到的结算仍是零条"
else
  bad "重启扫到 ${RESTART_COUNT:-?} 条结算"
fi

REOPEN_LOG="$TMP/reopen-b.log"
run_client reopen "$REOPEN_LOG"
REOPEN_EXIT=$?
REOPEN_OPENED="$(grep -m1 '^p01d_opened=' "$REOPEN_LOG" 2>/dev/null)"
REOPEN_HP="$(pick "$REOPEN_OPENED" hp)"
SESSION_B="$(pick "$REOPEN_OPENED" session)"
if [ "$REOPEN_EXIT" -eq 0 ] && [ "${REOPEN_HP:-}" = "$BOSS_HP_MAX" ]; then
  ok "重启后重进是满血 ${REOPEN_HP}/${BOSS_HP_MAX}，崩之前的进度没被带回来"
else
  bad "重启后血量不是满的：${REOPEN_HP:-?}/${BOSS_HP_MAX}（退出码 ${REOPEN_EXIT}）"
fi

if [ -n "${SESSION_A:-}" ] && [ -n "${SESSION_B:-}" ] && [ "$SESSION_A" != "$SESSION_B" ]; then
  ok "重启后是新的一场（session ${SESSION_A} → ${SESSION_B}）"
else
  bad "重启后的场次号没变：${SESSION_A:-?} / ${SESSION_B:-?}"
fi

echo "== 7. 通关写一条，崩掉不补发 =="
CLEAR_LOG="$TMP/clear.log"
run_client clear "$CLEAR_LOG"
CLEAR_EXIT=$?
CLEARED="$(grep -m1 '^p01d_cleared=' "$CLEAR_LOG" 2>/dev/null)"
SETTLEMENT_ID="$(pick "$CLEARED" settlement_id)"
EXP_GAIN="$(pick "$CLEARED" exp_gain)"

if [ "$CLEAR_EXIT" -eq 0 ] && [ "$(pick "$CLEARED" cleared)" = "True" ] \
   && [ -n "$SETTLEMENT_ID" ] && [ "${EXP_GAIN:-0}" -gt 0 ]; then
  ok "通关拿到事务号 ${SETTLEMENT_ID}，经验 ${EXP_GAIN}"
else
  bad "通关没拿到事务号：${CLEARED:-没这行}（退出码 ${CLEAR_EXIT}）"
fi

CLEAR_COUNT="$(settlement_count "$TMP/settlements")"
if [ "$CLEAR_COUNT" = "1" ]; then
  ok "通关写了 1 条结算"
else
  bad "通关之后结算有 ${CLEAR_COUNT} 条，应为 1 条"
fi

kill -9 "$BATTLE_PID" 2>/dev/null || true
wait "$BATTLE_PID" 2>/dev/null || true
BATTLE_PID=""

if start_battle "$TMP/c.log" "$TMP/battle.json"; then
  ok "写完结算再崩，仍然能起来"
else
  bad "写完结算之后起不来"
  tail -10 "$TMP/c.log" | detail
fi

AFTER_CRASH_COUNT="$(settlement_count "$TMP/settlements")"
if [ "$AFTER_CRASH_COUNT" = "1" ]; then
  ok "崩完重启仍是那 1 条，没有补发"
else
  bad "崩完重启结算变成 ${AFTER_CRASH_COUNT} 条"
fi

FINAL_LOG="$TMP/reopen-c.log"
run_client reopen "$FINAL_LOG"
FINAL_EXIT=$?
FINAL_OPENED="$(grep -m1 '^p01d_opened=' "$FINAL_LOG" 2>/dev/null)"
FINAL_HP="$(pick "$FINAL_OPENED" hp)"
if [ "$FINAL_EXIT" -eq 0 ] && [ "${FINAL_HP:-}" = "$BOSS_HP_MAX" ]; then
  ok "通关崩掉后重进仍是满血 ${FINAL_HP}/${BOSS_HP_MAX}，旧奖励不会变成新一场的进度"
else
  bad "通关崩掉后血量不是满的：${FINAL_HP:-?}/${BOSS_HP_MAX}"
fi

FINAL_COUNT="$(settlement_count "$TMP/settlements")"
if [ "$FINAL_COUNT" = "1" ]; then
  ok "重进一场又结束，结算还是 1 条"
else
  bad "重进之后结算变成 ${FINAL_COUNT} 条"
fi

# 服务端这侧：进程 A 那一场的开与关都留了日志。
OPEN_A="$(grep -c '^session_open=' "$TMP/a.log" 2>/dev/null || true)"
CLOSE_A="$(grep -c '^session_close=' "$TMP/a.log" 2>/dev/null || true)"
if [ "${OPEN_A:-0}" = "1" ] && [ "${CLOSE_A:-0}" = "1" ]; then
  ok "进程 A 那场开了一次、关了一次"
else
  bad "进程 A 的场次记录对不上：open=${OPEN_A:-0} close=${CLOSE_A:-0}"
fi

echo
echo "P0-1d 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
