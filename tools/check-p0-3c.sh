#!/usr/bin/env bash
# P0-3c 核对：真机上的五职业、蓝条、阶段。
#
# 完成标准（开发架构第 11 节 P0-3 的真机段）：五个职业在手机上能分清；
# 蓝条跟着回包走；换职业换一场；Boss 阶段切换看得见、阶段换了打人的手也换。
#
# 做这几段：
#   1. 编译战斗进程
#   2. 静态断言：客户端把职业参数拼进连接地址、换职业走重连、
#      MemberUpdated/BossPhase 有处理、蓝只在回包里更新、技能键用技能名
#   3. 起一个带两阶段与出手的战斗进程（固定 127.0.0.1:60001）
#   4. 出包装起，adb reverse，等战士开场（满血满蓝、五职业目录、技能名）
#   5. 点 Boss、走近、劈砍命中（伤害 120，蓝和仇恨跟手）
#   6. 换职业按钮走一圈：射手远程出手，法师/牧师/坦克的开场数值逐个对
#   7. 切回战士连打到血线：阶段广播 + Boss 出手把玩家打掉血
#
# 用法：tools/check-p0-3c.sh
# 调试时可 MMO_SKIP_BUILD=1 复用上一次的 APK（客户端没改时）。
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
ADB="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"

BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CLIENT_PROJ="$ROOT/client/MMOGame"
APK="/tmp/mmo-e7-apk/MMOGame.apk"
PKG="com.yunjinfanxing.mmogame"
DEVICE_PORT=60001

PASS=0
FAIL=0
TMP="$(mktemp -d)"
BATTLE_PID=""
LOG="$TMP/logcat.txt"

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
detail() { sed 's/^/       /'; }

# 从 "key=值 key=值" 这一行里取一个字段。
pick() {
  local body="${1#*=}"
  printf '%s' "$body" | tr ' ' '\n' | sed -n "s/^$2=//p" | head -1
}

cleanup() {
  if [ -n "$BATTLE_PID" ] && kill -0 "$BATTLE_PID" 2>/dev/null; then
    kill -TERM "$BATTLE_PID" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$BATTLE_PID" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$BATTLE_PID" 2>/dev/null || true
    wait "$BATTLE_PID" 2>/dev/null || true
  fi
  if [ -x "$ADB" ]; then
    "$ADB" shell am force-stop "$PKG" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

refresh_log() {
  "$ADB" -s "$DEVICE_ID" logcat -d -v brief -s Unity > "$LOG" 2>/dev/null || true
}

# 轮询 logcat 等一行出现。$1=固定字符串，$2=超时秒。
wait_for() {
  local pattern="$1" timeout="${2:-20}" i
  for i in $(seq 1 $((timeout * 2))); do
    refresh_log
    if grep -q "$pattern" "$LOG"; then
      return 0
    fi
    sleep 0.5
  done
  return 1
}

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P0-3c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Battle/Battle.csproj" -c Release -v q --nologo >"$TMP/build.log" 2>&1; then
  ok "战斗进程编译通过"
else
  bad "战斗进程编译失败"
  grep -E "error " "$TMP/build.log" | head -5 | detail
  echo
  echo "P0-3c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言（客户端）=="
CLIENT="$ROOT/client/MMOGame/Assets/HotUpdate/BattleClient.cs"
HUD="$ROOT/client/MMOGame/Assets/HotUpdate/BattleHud.cs"

if grep -q '"class=" + ClassId' "$CLIENT" && grep -q 'Url.Contains' "$CLIENT"; then
  ok "职业参数拼进连接地址，已带 query 的地址也不拼坏"
else
  bad "连接地址没带职业参数"
fi

if grep -q 'public async void SwitchClass' "$CLIENT" && grep -q '_client.SwitchClass' "$HUD"; then
  ok "换职业走断开重连，由界面按钮触发"
else
  bad "换职业入口不完整"
fi

if grep -q 'BodyOneofCase.MemberUpdated' "$CLIENT" && grep -q 'BodyOneofCase.BossPhase' "$CLIENT"; then
  ok "MemberUpdated 与 BossPhase 都有处理"
else
  bad "成员更新或阶段广播没有处理"
fi

if grep -q 'Mana = result.ManaAfter' "$CLIENT" && grep -q 'Mana = opened.Mana' "$CLIENT" \
   && ! grep -qE 'Mana[[:space:]]*[-+]=' "$CLIENT"; then
  ok "蓝只在开场包和结算回包里更新，客户端不自己加减"
else
  bad "蓝量有客户端自算的成分"
fi

if grep -q 'info.Name : "S"' "$HUD" || grep -q '? info.Name' "$HUD"; then
  ok "技能键标签用服务端下发的技能名"
else
  bad "技能键没用技能名，五个职业分不清"
fi

if grep -qE 'Hp(Max)?[[:space:]]*[-+]=|(damage|Damage)[[:space:]]*[-+]=|Mana(Max)?[[:space:]]*[-+]=' \
     -r "$ROOT/client/MMOGame/Assets/Net" "$ROOT/client/MMOGame/Assets/HotUpdate" 2>/dev/null; then
  bad "客户端在算血量、蓝量或伤害，这些只能来自服务端"
else
  ok "客户端没有算血量、蓝量和伤害"
fi

echo "== 3. 起战斗进程（两阶段 + 出手）=="
if [ ! -x "$ADB" ]; then
  bad "找不到 adb：$ADB"
  echo
  echo "P0-3c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

DEVICE_ID="$("$ADB" devices | awk 'NR>1 && $2=="device"{print $1; exit}')"
if [ -n "$DEVICE_ID" ]; then
  ok "设备在线：${DEVICE_ID}"
else
  bad "没有已授权的设备"
  echo
  echo "P0-3c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

# 设备配置：Boss 2000 血，50% 切狂暴；阶段一出手 80/4s（打得慢，别把战士打死），
# 阶段二 200/2.5s（切过去要能看见挨打变狠）。职业与技能表用仓库原版。
python3 - "$ROOT" "$TMP" "$DEVICE_PORT" <<'PY'
import json, os, sys
root, tmp, port = sys.argv[1], sys.argv[2], sys.argv[3]
battle = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
battle["listen"] = "127.0.0.1:" + port
battle["settlement_dir"] = os.path.join(tmp, "settlements")
battle["character_dir"] = os.path.join(tmp, "characters")
battle["skill_table"] = "skills.json"
battle["class_table"] = "classes.json"
battle["boss"]["hp_max"] = 2000
battle["boss"]["phases"] = [
    {"name": "常态", "hp_pct_from": 100, "hp_pct_to": 50,
     "attack": {"damage": 80, "interval_ms": 4000, "foreswing_ms": 800}},
    {"name": "狂暴", "hp_pct_from": 50, "hp_pct_to": 0,
     "attack": {"damage": 200, "interval_ms": 2500, "foreswing_ms": 500}},
]
json.dump(battle, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)
PY
cp "$ROOT/config/skills.json" "$ROOT/config/classes.json" "$TMP/"

: > "$TMP/battle.log"
"$DOTNET" "$BATTLE_DLL" --config "$TMP/battle.json" >>"$TMP/battle.log" 2>&1 &
BATTLE_PID=$!

READY=0
for _ in $(seq 1 120); do
  if grep -q "^listen=http://127.0.0.1:${DEVICE_PORT}\$" "$TMP/battle.log" 2>/dev/null; then
    READY=1
    break
  fi
  if ! kill -0 "$BATTLE_PID" 2>/dev/null; then
    break
  fi
  sleep 0.1
done

if [ "$READY" = "1" ]; then
  ok "战斗进程固定听 127.0.0.1:${DEVICE_PORT}（两阶段）"
else
  bad "战斗进程没起来"
  tail -10 "$TMP/battle.log" | detail
  echo
  echo "P0-3c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 4. 出包安装启动 =="
if [ "${MMO_SKIP_BUILD:-0}" = "1" ] && [ -f "$APK" ]; then
  ok "复用已有 APK：$(du -h "$APK" | awk '{print $1}')"
else
  echo "       （E7Build 出 APK，几分钟）"
  BUILD_LOG="$TMP/build-apk.log"
  "$UNITY" -batchmode -quit -projectPath "$CLIENT_PROJ" \
    -executeMethod E7Build.Run -logFile "$BUILD_LOG" >/dev/null 2>&1
  BUILD_EXIT=$?
  BUILD_LINE="$(grep -m1 '^build_result=' "$BUILD_LOG" 2>/dev/null || true)"
  [ -n "$BUILD_LINE" ] && printf '  %s\n' "$BUILD_LINE"

  if [ "${BUILD_EXIT:-1}" -eq 0 ] && [ -f "$APK" ]; then
    ok "APK 打包完成：$(du -h "$APK" | awk '{print $1}')"
  else
    bad "APK 没打出来（退出码 ${BUILD_EXIT:-?}）"
    grep -E "error CS|BuildFailed" "$BUILD_LOG" | head -5 | detail
    echo
    echo "P0-3c 核对：$PASS 项通过，$FAIL 项未通过"
    exit 1
  fi
fi

"$ADB" -s "$DEVICE_ID" reverse "tcp:${DEVICE_PORT}" "tcp:${DEVICE_PORT}" >/dev/null 2>&1 \
  && ok "adb reverse 已把 ${DEVICE_PORT} 映到设备" \
  || bad "adb reverse 失败"

"$ADB" -s "$DEVICE_ID" shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1 || true
INSTALL_LOG="$TMP/install.log"
if "$ADB" -s "$DEVICE_ID" install -r -t "$APK" >"$INSTALL_LOG" 2>&1; then
  ok "APK 已装上设备"
else
  bad "安装失败"
  tail -5 "$INSTALL_LOG" | detail
  echo
  echo "P0-3c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

"$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
"$ADB" -s "$DEVICE_ID" shell am force-stop "$PKG" >/dev/null 2>&1 || true
"$ADB" -s "$DEVICE_ID" reverse "tcp:${DEVICE_PORT}" "tcp:${DEVICE_PORT}" >/dev/null 2>&1 || true
if "$ADB" -s "$DEVICE_ID" reverse --list 2>/dev/null | grep -q "tcp:${DEVICE_PORT}"; then
  ok "启动前 reverse 在位：${DEVICE_PORT}"
else
  bad "启动前 reverse 不见了，设备连不到这台机器"
fi

for _ in $(seq 1 12); do
  "$ADB" -s "$DEVICE_ID" shell pidof "$PKG" >/dev/null 2>&1 || break
  sleep 0.5
done

"$ADB" -s "$DEVICE_ID" shell am start -n "${PKG}/com.unity3d.player.UnityPlayerActivity" >/dev/null 2>&1

APP_UP=0
for _ in $(seq 1 40); do
  refresh_log
  if grep -q 'p02a_scene=ok' "$LOG"; then
    APP_UP=1
    break
  fi
  sleep 1
  if [ $(( _ % 8 )) = 0 ]; then
    "$ADB" -s "$DEVICE_ID" shell am start -n "${PKG}/com.unity3d.player.UnityPlayerActivity" >/dev/null 2>&1
  fi
done

if [ "$APP_UP" = "1" ]; then
  ok "应用已启动，场景加载完成"
else
  bad "应用没起来（没等到 p02a_scene）"
  grep -E 'E/Unity|FATAL' "$LOG" 2>/dev/null | head -5 | detail
  echo
  echo "P0-3c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 5. 战士开场 =="
if wait_for 'p03c_opened class=warrior' 20; then
  ok "默认职业战士连上，收到开场包"
  grep -m1 'p03c_opened class=warrior' "$LOG" | sed 's/^.*Unity *: /  /'
else
  bad "没等到战士开场（连接或协议有问题）"
  grep -E 'p02b_connect|p03c_opened|Exception' "$LOG" 2>/dev/null | head -8 | detail
  echo
  echo "P0-3c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

WARRIOR_OPENED="$(grep -m1 'p03c_opened class=warrior' "$LOG")"
if [ "$(pick "$WARRIOR_OPENED" mana)" = "520/520" ]; then
  ok "战士满蓝：520 = 400 基础 + 精神 40 × 3"
else
  bad "战士蓝量不对：$(pick "$WARRIOR_OPENED" mana)"
fi

if [ "$(pick "$WARRIOR_OPENED" caster_hp)" = "1200/1200" ]; then
  ok "战士满血：1200 = 体力 30 × 40"
else
  bad "战士血量不对：$(pick "$WARRIOR_OPENED" caster_hp)"
fi

if [ "$(pick "$WARRIOR_OPENED" classes)" = "5" ]; then
  ok "职业目录随开场包下发：5 个职业"
else
  bad "职业目录数量不对：$(pick "$WARRIOR_OPENED" classes)"
fi

SKILLS_LINE="$(pick "$WARRIOR_OPENED" skills)"
if printf '%s' "$SKILLS_LINE" | grep -q '1001:劈砍' \
  && printf '%s' "$SKILLS_LINE" | grep -q '1002:重斩' \
  && printf '%s' "$SKILLS_LINE" | grep -q '1003:横扫'; then
  ok "战士技能表带名字：${SKILLS_LINE}"
else
  bad "战士技能表不全：${SKILLS_LINE}"
fi

# 换职业按钮与技能键的坐标由界面启动时打出，和命中判定是同一套像素。
HUD_INFO="$(python3 - "$LOG" <<'PY'
import re, sys
text = open(sys.argv[1], encoding='utf-8', errors='ignore').read()
m = re.search(r'p03c_hud=ready screen=(\d+)x(\d+) class_rect=(-?\d+),(-?\d+),(\d+),(\d+) skill_rect=(-?\d+),(-?\d+)', text)
if m:
    print(' '.join(m.groups()))
else:
    print('')
PY
)"

if [ -z "$HUD_INFO" ]; then
  bad "没拿到界面坐标（p03c_hud）"
  echo
  echo "P0-3c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
set -- $HUD_INFO
SCREEN_W=$1; SCREEN_H=$2
CLASS_X=$3; CLASS_Y=$4; CLASS_W=$5; CLASS_H=$6
SKILL_X=$7; SKILL_Y=$8
ok "界面坐标：${SCREEN_W}x${SCREEN_H}，换职业键=(${CLASS_X},${CLASS_Y})，技能键=(${SKILL_X},${SKILL_Y})"

# adb 的 input 用屏幕左上角当原点，界面矩形是左下角原点，换算后再点中心。
CLASS_CX=$(( CLASS_X + CLASS_W / 2 ))
CLASS_CY=$(( SCREEN_H - CLASS_Y - CLASS_H / 2 ))
SIZE=200
GAP=24
S1_CX=$(( SKILL_X + SIZE / 2 ))
S1_CY=$(( SCREEN_H - SKILL_Y - SIZE / 2 ))
S2_CX=$(( S1_CX + SIZE + GAP ))
S3_CX=$(( S2_CX + SIZE + GAP ))
echo "       换职业键中心=(${CLASS_CX},${CLASS_CY}) S1=(${S1_CX},${S1_CY}) S2=(${S2_CX},${S1_CY}) S3=(${S3_CX},${S1_CY})"

# 唤醒输入链路后点 Boss 拿目标。
TAP_BOSS="$(python3 - "$LOG" <<'PY'
import re, sys
text = open(sys.argv[1], encoding='utf-8', errors='ignore').read()
m = re.search(r'target_screen=9001 x=(-?\d+) y=(-?\d+)', text)
print(f"{m.group(1)} {m.group(2)}" if m else "")
PY
)"

if [ -n "$TAP_BOSS" ]; then
  set -- $TAP_BOSS
  "$ADB" -s "$DEVICE_ID" shell input swipe 20 20 20 20 220 >/dev/null 2>&1
  sleep 0.6
  "$ADB" -s "$DEVICE_ID" shell input swipe "$1" "$2" "$1" "$2" 220 >/dev/null 2>&1
  sleep 1
  refresh_log
  if grep -q 'select_target=9001' "$LOG"; then
    ok "点中 Boss：select_target=9001"
  else
    bad "没点中 Boss"
    grep -E 'p02a_tap|p02a_ray' "$LOG" 2>/dev/null | tail -4 | detail
  fi
else
  bad "没拿到 Boss 的点击坐标"
fi

# 走近：出生 6000mm，劈砍射程 3000mm。向左拖摇杆，一次约 0.6 米。
"$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
STICK_X=$(( SCREEN_W / 5 ))
STICK_Y=$(( SCREEN_H / 2 ))
for _ in 1 2 3 4 5 6 7 8 9 10; do
  "$ADB" -s "$DEVICE_ID" shell input swipe "$(( STICK_X + 400 ))" "$STICK_Y" "$STICK_X" "$STICK_Y" 240 >/dev/null 2>&1
  sleep 0.3
done
sleep 1
refresh_log

NEAR_DIST="$(grep 'p02b_move' "$LOG" 2>/dev/null | tail -1 | tr ' ' '\n' | sed -n 's/^distance=//p' | tail -1)"
if [ -n "$NEAR_DIST" ] && [ "$NEAR_DIST" -lt 3000 ]; then
  ok "走近了：距离 ${NEAR_DIST}mm，劈砍够得着"
elif [ -n "$NEAR_DIST" ]; then
  bad "还没走进劈砍射程：${NEAR_DIST}mm"
else
  bad "走位没有回包"
fi

# 劈砍一下：伤害 120，蓝不动（普攻零耗），仇恨 = 120 + 体力 30。
"$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
"$ADB" -s "$DEVICE_ID" shell input swipe "$S1_CX" "$S1_CY" "$S1_CX" "$S1_CY" 220 >/dev/null 2>&1
sleep 2.5
refresh_log

if grep -q 'p02b_cast_end skill=1001 result=hit damage=120 ' "$LOG"; then
  ok "劈砍命中：伤害 120（力量 100 × 2.0 × 0.6）"
else
  bad "劈砍没有命中"
  grep -m3 'p02b_cast' "$LOG" | detail
fi

P03C_CAST="$(grep -m1 'p03c_cast skill=1001 ' "$LOG" || true)"
if [ -n "$P03C_CAST" ]; then
  if [ "$(pick "$P03C_CAST" mana)" = "520" ]; then
    ok "回包带蓝量：520（普攻不耗蓝）"
  else
    bad "普攻后蓝量不对：$(pick "$P03C_CAST" mana)"
  fi
  if [ "$(pick "$P03C_CAST" threat)" = "150" ]; then
    ok "回包带仇恨：150 = 伤害 120 + 体力 30 × 1"
  else
    bad "仇恨不对：$(pick "$P03C_CAST" threat)"
  fi
else
  bad "没有 p03c_cast 行"
fi

echo "== 6. 换职业走一圈 =="
# 职业目录按 classes.json 顺序循环：战士 → 射手 → 法师 → 牧师 → 坦克 → 战士。
# 期望值：mana = 基础 + 精神 × 系数；hp = 体力 × 40。
switch_round() {
  local cls="$1" mana="$2" hp="$3" names="$4"

  "$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
  "$ADB" -s "$DEVICE_ID" shell input swipe "$CLASS_CX" "$CLASS_CY" "$CLASS_CX" "$CLASS_CY" 220 >/dev/null 2>&1

  if wait_for "p03c_opened class=${cls}" 15; then
    ok "切到${cls}，收到新一场开场包"
  else
    bad "没等到${cls}开场"
    grep -E 'p03c_switch|p03c_opened|p02b_connect' "$LOG" 2>/dev/null | tail -4 | detail
    return
  fi

  local opened_line
  opened_line="$(grep -m1 "p03c_opened class=${cls}" "$LOG")"

  if grep -q "p03c_switch class=${cls}" "$LOG"; then
    ok "按钮触发了换职业：p03c_switch class=${cls}"
  else
    bad "没有 p03c_switch class=${cls}"
  fi

  if [ "$(pick "$opened_line" mana)" = "${mana}/${mana}" ]; then
    ok "${cls}满蓝：${mana}"
  else
    bad "${cls}蓝量不对：$(pick "$opened_line" mana)（应为 ${mana}）"
  fi

  if [ "$(pick "$opened_line" caster_hp)" = "${hp}/${hp}" ]; then
    ok "${cls}满血：${hp}"
  else
    bad "${cls}血量不对：$(pick "$opened_line" caster_hp)（应为 ${hp}）"
  fi

  local skills_line
  skills_line="$(pick "$opened_line" skills)"
  local name missing=0
  for name in $names; do
    printf '%s' "$skills_line" | grep -q "$name" || missing=1
  done
  if [ "$missing" = 0 ]; then
    ok "${cls}技能表：${skills_line}"
  else
    bad "${cls}技能表不全：${skills_line}"
  fi
}

switch_round archer 660 1040 "速射 穿云箭 连珠"

# 射手出生 6000mm，速射射程 25000mm，不用走近就能出手（伤害 = 敏捷 100 × 2.0 × 0.5）。
"$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
"$ADB" -s "$DEVICE_ID" shell input swipe "$S1_CX" "$S1_CY" "$S1_CX" "$S1_CY" 220 >/dev/null 2>&1
sleep 2.5
refresh_log

if grep -q 'p02b_cast_end skill=1101 result=hit damage=100 ' "$LOG"; then
  ok "射手速射命中：伤害 100，不用走近"
else
  bad "射手速射没有命中"
  grep -m3 'p02b_cast' "$LOG" | detail
fi

ARCHER_CAST="$(grep -m1 'p03c_cast skill=1101 ' "$LOG" || true)"
if [ -n "$ARCHER_CAST" ] && [ "$(pick "$ARCHER_CAST" mana)" = "660" ]; then
  ok "射手回包带蓝量：660"
else
  bad "射手回包蓝量不对：$(pick "$ARCHER_CAST" mana)"
fi

switch_round mage 1400 880 "法杖击 火球 寒霜"
switch_round priest 1400 960 "圣光 治疗 回生"
switch_round tank 380 1200 "锤击 盾击 裂地"
switch_round warrior 520 1200 "劈砍 重斩 横扫"

if grep -q 'p03c_switch_skip' "$LOG"; then
  bad "换职业有被跳过的（same_class）"
else
  ok "五次切换全部生效，没有空按"
fi

echo "== 7. 打到血线看阶段 =="
# 切回战士是一场新的：站位回到 6000mm，但目标选择还留在场景里（Boss 编号不变），
# 重新走近就能接着打，不需要再点一遍 Boss。
"$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
for _ in 1 2 3 4 5 6 7 8 9 10; do
  "$ADB" -s "$DEVICE_ID" shell input swipe "$(( STICK_X + 400 ))" "$STICK_Y" "$STICK_X" "$STICK_Y" 240 >/dev/null 2>&1
  sleep 0.3
done
sleep 1
refresh_log

NEAR_DIST="$(grep 'p02b_move' "$LOG" 2>/dev/null | tail -1 | tr ' ' '\n' | sed -n 's/^distance=//p' | tail -1)"
if [ -n "$NEAR_DIST" ] && [ "$NEAR_DIST" -lt 3000 ]; then
  ok "第二场走近了：距离 ${NEAR_DIST}mm"
else
  bad "第二场没走近（${NEAR_DIST:-没有读数}），横扫够不着也得靠重斩打完"
fi

# S1 劈砍 120 / S2 重斩 260，各 2 秒冷却；S3 横扫 340 六秒冷却。
# 打到 1000 伤害（一半血）切阶段。轮着点，每几手看一眼阶段广播。
"$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
PHASE_OK=0
for i in $(seq 1 18); do
  "$ADB" -s "$DEVICE_ID" shell input swipe "$S1_CX" "$S1_CY" "$S1_CX" "$S1_CY" 200 >/dev/null 2>&1
  sleep 1.1
  "$ADB" -s "$DEVICE_ID" shell input swipe "$S2_CX" "$S1_CY" "$S2_CX" "$S1_CY" 200 >/dev/null 2>&1
  sleep 1.1
  if [ $(( i % 3 )) = 0 ]; then
    "$ADB" -s "$DEVICE_ID" shell input swipe "$S3_CX" "$S1_CY" "$S3_CX" "$S1_CY" 200 >/dev/null 2>&1
    sleep 1.1
  fi
  refresh_log
  if grep -q 'p03c_phase' "$LOG"; then
    PHASE_OK=1
    break
  fi
done

if [ "$PHASE_OK" = 1 ]; then
  ok "打到血线切了阶段：$(grep -m1 'p03c_phase' "$LOG" | sed 's/^.*Unity *: //')"
else
  bad "连打 ${i} 手没等到阶段广播"
  grep -E 'p02b_cast_end|p03c_cast' "$LOG" 2>/dev/null | tail -5 | detail
fi

PHASE_LINE="$(grep -m1 'p03c_phase' "$LOG" || true)"
if [ -n "$PHASE_LINE" ]; then
  # pick 会把行内第一个字段剥掉（logcat 前缀里带 = 的缘故），index 恰好是第一个，
  # 这里直接对原始行做匹配，不经过 pick。
  if printf '%s' "$PHASE_LINE" | grep -q 'p03c_phase index=1 name=狂暴'; then
    ok "阶段广播对：index=1 name=狂暴（50% 血线来自配置）"
  else
    bad "阶段广播内容不对：$(printf '%s' "$PHASE_LINE" | sed 's/^.*Unity *: //')"
  fi
fi

# 阶段一切，Boss 重新计时出手；玩家是唯一目标，必挨打。
MEMBER_HIT=0
for _ in $(seq 1 24); do
  refresh_log
  if grep -q 'p03c_member' "$LOG"; then
    MEMBER_HIT=1
    break
  fi
  sleep 0.5
done

if [ "$MEMBER_HIT" = 1 ]; then
  ok "Boss 出手打了玩家：$(grep -m1 'p03c_member' "$LOG" | sed 's/^.*Unity *: //')"
  MEMBER_HP="$(grep 'p03c_member' "$LOG" | tr ' ' '\n' | sed -n 's/^hp=//p' | sed 's|/.*||' | sort -n | head -1)"
  if [ -n "$MEMBER_HP" ] && [ "$MEMBER_HP" -lt 1200 ]; then
    ok "玩家掉血了：最低到 ${MEMBER_HP}（满血 1200）"
  else
    bad "玩家血量没掉：${MEMBER_HP:-没有读数}"
  fi
else
  bad "没有等到 Boss 打人的广播"
fi

# 阶段二的出手要比阶段一狠：等一手狂暴的伤害（200），确认换了攻击参数。
RAGE_HIT=0
for _ in $(seq 1 30); do
  refresh_log
  RAGE_HP="$(grep 'p03c_member' "$LOG" | tr ' ' '\n' | sed -n 's/^hp=//p' | sed 's|/.*||' | sort -n | head -1)"
  if [ -n "$RAGE_HP" ] && [ "$RAGE_HP" -le 1000 ]; then
    RAGE_HIT=1
    break
  fi
  sleep 0.5
done

if [ "$RAGE_HIT" = 1 ]; then
  ok "狂暴的出手更狠：血量最低到 ${RAGE_HP}（阶段二单手 200）"
else
  bad "没看到狂暴伤害（最低 ${RAGE_HP:-没有读数}，应到 1000 以下）"
fi

echo
echo "P0-3c 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
