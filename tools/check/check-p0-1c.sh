#!/usr/bin/env bash
# P0-1c 核对：本机 WebSocket 连战斗进程，请求能发出，结果能回到客户端。
#
# 做这几件事：
#   1. 生成物和 proto 对得上，收发信封的字段号没被改过
#   2. 编译战斗进程；客户端由 Unity 导入时编译
#   3. 起一个临时战斗进程（端口让系统给，结算落临时目录）
#   4. Unity 批量模式连上去：收开场包、发一个够得着的技能、收结果、再发一次被拒
#   5. 逐项断言客户端拿到的数字和 config 里的一致，前摇没被跳过，拒绝后没有迟到结算
#   6. 反例：连一个没人听的端口，要在 3 秒内报错而不是挂住
#   7. 服务端这侧：一场开一次关一次，Boss 没死就没有结算记录
#   8. 客户端源码不算伤害、不写结算
#
# 用法：tools/check/check-p0-1c.sh
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
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

# 从 "key=值 key=值" 这一行里取一个字段。行首的 p01c_xxx= 先剥掉。
pick() {
  local body="${1#*=}"
  printf '%s' "$body" | tr ' ' '\n' | sed -n "s/^$2=//p" | head -1
}

echo "== 1. 协议与生成物 =="
if bash "$ROOT/tools/gen/gen-proto.sh" --into "$TMP/gen" >"$TMP/gen.log" 2>&1; then
  ok "重新生成成功"
else
  bad "生成失败，见下面几行"
  tail -5 "$TMP/gen.log" | detail
fi

if diff -r -q -x "*.meta" -x "*Grpc.cs" "$TMP/gen" "$ROOT/src/Shared/Generated" >/dev/null 2>&1; then
  ok "服务端生成物与仓库一致"
else
  bad "服务端生成物和 proto 不一致，重跑 tools/gen/gen-proto.sh 并提交"
  diff -r -x "*.meta" "$TMP/gen" "$ROOT/src/Shared/Generated" | head -8 | detail
fi

if diff -r -q -x "*.meta" -x "*Grpc.cs" "$ROOT/src/Shared/Generated" "$ROOT/client/MMOGame/Assets/Proto/Generated" >/dev/null 2>&1; then
  ok "客户端与服务端两份生成物逐字相同"
else
  bad "两端生成物不同"
  diff -r -x "*.meta" "$ROOT/src/Shared/Generated" "$ROOT/client/MMOGame/Assets/Proto/Generated" | head -8 | detail
fi

PROTO="$ROOT/proto/mmo/battle.proto"
# 这条连接上收发什么，靠这两个信封分辨。编号一改，老客户端会读错位。
ENVELOPE_OK=1
grep -q 'SkillRequest skill = 1;' "$PROTO" || ENVELOPE_OK=0
grep -q 'SkillCancelRequest cancel = 2;' "$PROTO" || ENVELOPE_OK=0
grep -q 'BattleOpened opened = 1;' "$PROTO" || ENVELOPE_OK=0
grep -q 'SkillResult result = 2;' "$PROTO" || ENVELOPE_OK=0
grep -q 'SkillRejected rejected = 3;' "$PROTO" || ENVELOPE_OK=0
grep -q 'BattleEnded ended = 5;' "$PROTO" || ENVELOPE_OK=0
if [ "$ENVELOPE_OK" = "1" ]; then
  ok "收发两个信封的字段号没动"
else
  bad "信封字段号被改过，客户端会解析错位"
fi

# P0-1a、P0-1b 定下的字段不能被这次改动碰掉。
if grep -q 'uint32 skill_id = 1;' "$PROTO" \
   && grep -q 'uint64 target_id = 2;' "$PROTO" \
   && grep -q 'int64 cast_time_ms = 3;' "$PROTO" \
   && grep -q 'int64 target_hp_after = 9;' "$PROTO" \
   && grep -q 'SkillRejectReason reason = 4;' "$PROTO"; then
  ok "P0-1a、P0-1b 的字段与编号都还在"
else
  bad "P0-1a 或 P0-1b 的字段被改动了"
fi

echo "== 2. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet，没法编译战斗进程"
  echo
  echo "P0-1c 核对：$PASS 项通过，$FAIL 项未通过"
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
  echo "P0-1c 核对：$PASS 项通过，$FAIL 项未通过（编译没过，连接不跑）"
  exit 1
fi

if [ ! -x "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  echo
  echo "P0-1c 核对：$PASS 项通过，$FAIL 项未通过（客户端这半边没跑）"
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

# 只在默认职业的技能里挑：取够得着、前摇和冷却都好观察的那一个（射程最远的）。
cls_id = battle["default_class"]
skills = [s for s in json.load(open(os.path.join(root, "config", "skills.json"), encoding="utf-8"))["skills"]
          if s["class_id"] == cls_id]
skill = max(skills, key=lambda s: s["range_mm"])

# 期望伤害不是表里的平数：这一下 = 攻击 × 倍率，攻击 = 主属性 × 职业系数 + 装备固定。
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
  bad "挑中的技能够不着站位，命中这一项没法验"
fi

if [ "$SKILL_RECOVERY" -lt 700 ] && [ "$SKILL_COOLDOWN" -gt 1400 ]; then
  ok "后摇短于等待、冷却长于等待，第二下必然撞冷却（${SKILL_RECOVERY}ms < 700ms < ${SKILL_COOLDOWN}ms）"
else
  bad "技能时序不合适：后摇 ${SKILL_RECOVERY}ms、冷却 ${SKILL_COOLDOWN}ms，第二下不一定撞冷却"
fi

echo "== 4. 起战斗进程 =="
: > "$TMP/battle.log"
"$DOTNET" "$BATTLE_DLL" --config "$TMP/battle.json" >>"$TMP/battle.log" 2>&1 &
BATTLE_PID=$!

WS_URL=""
for _ in $(seq 1 120); do
  if grep -q '^listen=' "$TMP/battle.log" 2>/dev/null; then
    ADDR="$(grep -m1 '^listen=' "$TMP/battle.log" | cut -d= -f2-)"
    WS_URL="$(printf '%s' "$ADDR" | sed 's|^http|ws|')/battle"
    break
  fi
  if ! kill -0 "$BATTLE_PID" 2>/dev/null; then
    break
  fi
  sleep 0.1
done

if [ -n "$WS_URL" ]; then
  ok "战斗进程起来了：${WS_URL}"
else
  bad "战斗进程没起来"
  tail -10 "$TMP/battle.log" | detail
  echo
  echo "P0-1c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 5. 客户端连上去走一轮 =="
UNITY_LOG="$TMP/unity.log"
MMO_BATTLE_URL="$WS_URL" \
MMO_BATTLE_DEAD_URL="ws://127.0.0.1:1/battle" \
MMO_BATTLE_SKILL="$SKILL_ID" \
  "$UNITY" -batchmode -quit -projectPath "$CLIENT_PROJ" \
  -executeMethod P01cCheck.Run -logFile "$UNITY_LOG" >/dev/null 2>&1
UNITY_EXIT=$?

if grep -qE "error CS|Compilation failed" "$UNITY_LOG" 2>/dev/null; then
  bad "客户端编译失败"
  grep -E "error CS" "$UNITY_LOG" | head -5 | detail
fi

if [ "$UNITY_EXIT" -eq 0 ]; then
  ok "客户端跑完，退出码 0"
else
  bad "客户端退出码 ${UNITY_EXIT}"
  grep -E "^p01c_|Exception" "$UNITY_LOG" | head -8 | detail
fi

OPENED="$(grep -m1 '^p01c_opened=' "$UNITY_LOG" 2>/dev/null || true)"
RESULT="$(grep -m1 '^p01c_result=' "$UNITY_LOG" 2>/dev/null || true)"
REJECTED="$(grep -m1 '^p01c_rejected=' "$UNITY_LOG" 2>/dev/null || true)"
DEAD="$(grep -m1 '^p01c_connect_fail=' "$UNITY_LOG" 2>/dev/null || true)"

[ -n "$OPENED" ] && printf '  %s\n' "$OPENED"
[ -n "$RESULT" ] && printf '  %s\n' "$RESULT"
[ -n "$REJECTED" ] && printf '  %s\n' "$REJECTED"
[ -n "$DEAD" ] && printf '  %s\n' "$DEAD"

# 开场包：客户端拿到的双方血量、距离要和配置一致。
if [ -n "$OPENED" ]; then
  ok "客户端收到开场包"
  [ "$(pick "$OPENED" boss)" = "$BOSS_ID" ] \
    && ok "开场包的 Boss 编号是配置里的 ${BOSS_ID}" \
    || bad "开场包的 Boss 编号不对：$(pick "$OPENED" boss)"
  [ "$(pick "$OPENED" boss_hp)" = "$BOSS_HP_MAX" ] \
    && [ "$(pick "$OPENED" boss_hp_max)" = "$BOSS_HP_MAX" ] \
    && ok "开场满血 ${BOSS_HP_MAX}" \
    || bad "开场血量不对：$(pick "$OPENED" boss_hp)/$(pick "$OPENED" boss_hp_max)"
  [ "$(pick "$OPENED" distance_mm)" = "$DIST" ] \
    && ok "开场距离 ${DIST}mm 与配置一致" \
    || bad "开场距离不对：$(pick "$OPENED" distance_mm)mm，应为 ${DIST}mm"
else
  bad "客户端没收到开场包"
fi

# 结果包：命中、伤害、剩余血量逐项对。
if [ -n "$RESULT" ]; then
  ok "客户端收到技能结果"
  [ "$(pick "$RESULT" skill)" = "$SKILL_ID" ] \
    && ok "结果里的技能编号是 ${SKILL_ID}" \
    || bad "结果里的技能编号不对：$(pick "$RESULT" skill)"
  [ "$(pick "$RESULT" target)" = "$BOSS_ID" ] \
    && ok "结果里的目标是 ${BOSS_ID}" \
    || bad "结果里的目标不对：$(pick "$RESULT" target)"
  [ "$(pick "$RESULT" hit)" = "True" ] \
    && ok "这一下命中" \
    || bad "这一下没命中：hit=$(pick "$RESULT" hit)"
  [ "$(pick "$RESULT" damage)" = "$SKILL_DAMAGE" ] \
    && ok "伤害 ${SKILL_DAMAGE} 来自技能表，客户端没改过" \
    || bad "伤害不对：$(pick "$RESULT" damage)，应为 ${SKILL_DAMAGE}"
  EXPECT_HP=$((BOSS_HP_MAX - SKILL_DAMAGE))
  [ "$(pick "$RESULT" hp_after)" = "$EXPECT_HP" ] \
    && ok "结果带回剩余血量 ${EXPECT_HP}，血条照这个显示就行" \
    || bad "剩余血量不对：$(pick "$RESULT" hp_after)，应为 ${EXPECT_HP}"

  ELAPSED="$(pick "$RESULT" elapsed_ms)"
  if [ -n "$ELAPSED" ] && [ "$ELAPSED" -ge $((SKILL_FORESWING * 3 / 4)) ]; then
    ok "从发出到收到结果用了 ${ELAPSED}ms，前摇 ${SKILL_FORESWING}ms 没有被跳过"
  else
    bad "从发出到收到结果只用了 ${ELAPSED:-?}ms，前摇被跳过了"
  fi
else
  bad "客户端没收到技能结果"
fi

# 第二下撞冷却：拒绝也要能回到客户端，并且不带任何结算。
if [ -n "$REJECTED" ]; then
  ok "客户端收到拒绝回执"
  # C# 生成的枚举名是 PascalCase：SKILL_REJECT_COOLDOWN → SkillRejectCooldown。
  [ "$(pick "$REJECTED" reason)" = "SkillRejectCooldown" ] \
    && ok "拒绝原因是冷却没好" \
    || bad "拒绝原因不是冷却：$(pick "$REJECTED" reason)"
  RETRY="$(pick "$REJECTED" retry_after_ms)"
  if [ -n "$RETRY" ] && [ "$RETRY" -gt 0 ]; then
    ok "回执带了还差 ${RETRY}ms"
  else
    bad "回执没带 retry_after_ms"
  fi
else
  bad "第二下没收到拒绝回执"
fi

LEAK="$(grep -m1 '^p01c_reject_leak=' "$UNITY_LOG" 2>/dev/null | cut -d= -f2- || true)"
if [ "${LEAK:-1}" = "0" ]; then
  ok "被拒之后没有迟到的结算，血量没再动"
else
  bad "被拒之后还收到了 ${LEAK:-?} 条结果"
fi

if grep -q '^p01c_closed=ok$' "$UNITY_LOG" 2>/dev/null; then
  ok "客户端正常收尾断开"
else
  bad "客户端没走到正常断开"
fi

# 反例：没人听的端口要在 3 秒内报错，不能一直挂着。
if [ -n "$DEAD" ]; then
  case "$DEAD" in
    *connected_unexpectedly*)
      bad "连一个没人听的端口居然连上了" ;;
    *)
      DEAD_MS="$(pick "$DEAD" elapsed_ms)"
      if [ -n "$DEAD_MS" ] && [ "$DEAD_MS" -lt 3000 ]; then
        ok "连不上时 ${DEAD_MS}ms 就报错，没有挂住"
      else
        bad "连不上时等了 ${DEAD_MS:-?}ms 才报错，太久了"
      fi ;;
  esac
else
  bad "没跑到连不上的反例"
fi

echo "== 6. 服务端这侧 =="
OPEN_COUNT="$(grep -c '^session_open=' "$TMP/battle.log" 2>/dev/null || true)"
CLOSE_COUNT="$(grep -c '^session_close=' "$TMP/battle.log" 2>/dev/null || true)"
if [ "${OPEN_COUNT:-0}" = "1" ] && [ "${CLOSE_COUNT:-0}" = "1" ]; then
  ok "服务端开了 1 场、关了 1 场"
else
  bad "服务端开的场次对不上：open=${OPEN_COUNT:-0} close=${CLOSE_COUNT:-0}"
  tail -6 "$TMP/battle.log" | detail
fi

SETTLE_COUNT="$(find "$TMP/settlements" -type f 2>/dev/null | wc -l | tr -d ' ')"
if [ "$SETTLE_COUNT" = "0" ]; then
  ok "Boss 没打死，结算目录是空的"
else
  bad "Boss 没打死却写了 ${SETTLE_COUNT} 条结算"
fi

if kill -0 "$BATTLE_PID" 2>/dev/null; then
  ok "客户端断开后战斗进程还在"
else
  bad "客户端一断，战斗进程就退了"
fi

echo "== 7. 客户端不算伤害 =="
if grep -q '127.0.0.1\|localhost' "$ROOT/client/MMOGame/Assets/Net" -r 2>/dev/null; then
  bad "客户端收发层写死了地址，端口得由进本流程给"
else
  ok "客户端没写死地址"
fi

if grep -qE 'Hp(Max)?[[:space:]]*[-+]=|(damage|Damage)[[:space:]]*[-+]=' \
     -r "$ROOT/client/MMOGame/Assets/Net" "$ROOT/client/MMOGame/Assets/HotUpdate" 2>/dev/null; then
  bad "客户端在算血量和伤害，这些只能来自服务端"
else
  ok "客户端没有算血量和伤害"
fi

if grep -qE 'File\.(Write|Append|Create)|Directory\.Create|Npgsql|StreamWriter' \
     -r "$ROOT/client/MMOGame/Assets/Net" "$ROOT/client/MMOGame/Assets/HotUpdate" 2>/dev/null; then
  bad "客户端在写结算，这些只能来自服务端"
else
  ok "客户端只读结算号，不落盘"
fi

echo
echo "P0-1c 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
