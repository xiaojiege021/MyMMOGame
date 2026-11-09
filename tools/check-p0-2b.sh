#!/usr/bin/env bash
# P0-2b 核对：前摇、走位、回包表现。
#
# 默认只跑编辑器侧（快）：
#   1. 生成物和 proto 对得上；移动、技能表下发的字段号没动，P0-1a~1d 的字段还在
#   2. 战斗进程编译得过；客户端由 Unity 导入时编译
#   3. 静态断言：前摇时长从开场包读、血量只认回包、界面用新输入、客户端不算伤害
#   4. 起一个临时战斗进程，Unity 批量模式连上去走一遍：
#      远处打不到 → 一次跨 4000mm 被夹 → 逐步走近 → 打中 → 前摇里撤掉 → 走到场外被拉回
#
# 加 --device 再跑真机段（要先插好已授权的平板）：
#   5. 战斗进程固定听 127.0.0.1:60001，adb reverse 把这个端口映到平板上
#   6. E7Build 出 APK → 安装 → 启动 → 点 Boss → 点 S1（远处被拒）→ 拖摇杆走近
#      → 点 S1（命中）→ 点 S3 后撤 → 从 logcat 逐条断言
#
# 用法：tools/check-p0-2b.sh [--device]
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

DEVICE=0
[ "${1:-}" = "--device" ] && DEVICE=1

PASS=0
FAIL=0
TMP="$(mktemp -d)"
BATTLE_PID=""
APP_PID=""

cleanup() {
  if [ -n "$BATTLE_PID" ] && kill -0 "$BATTLE_PID" 2>/dev/null; then
    kill -TERM "$BATTLE_PID" 2>/dev/null || true
    # 还有连接挂着时进程可能停在 StopAsync 里不退，等两秒就强杀，别把端口留在那儿。
    for _ in $(seq 1 20); do
      kill -0 "$BATTLE_PID" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$BATTLE_PID" 2>/dev/null || true
    wait "$BATTLE_PID" 2>/dev/null || true
  fi
  if [ -n "$APP_PID" ] && [ -x "$ADB" ]; then
    "$ADB" shell am force-stop "$PKG" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

# 真机段要换端口，先把上一个进程停干净，否则端口被占直接起不来。
stop_battle() {
  if [ -n "$BATTLE_PID" ] && kill -0 "$BATTLE_PID" 2>/dev/null; then
    kill -TERM "$BATTLE_PID" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$BATTLE_PID" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$BATTLE_PID" 2>/dev/null || true
    wait "$BATTLE_PID" 2>/dev/null || true
  fi
  BATTLE_PID=""
}

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
detail() { sed 's/^/       /'; }

# 从 "key=值 key=值" 这一行里取一个字段，行首的 p02b_xxx= 先剥掉。
pick() {
  local body="${1#*=}"
  printf '%s' "$body" | tr ' ' '\n' | sed -n "s/^$2=//p" | head -1
}

echo "== 1. 协议与生成物 =="
if bash "$ROOT/tools/gen-proto.sh" --into "$TMP/gen" >"$TMP/gen.log" 2>&1; then
  ok "重新生成成功"
else
  bad "生成失败"
  tail -5 "$TMP/gen.log" | detail
fi

if diff -r -q -x "*.meta" "$TMP/gen" "$ROOT/src/Shared/Generated" >/dev/null 2>&1; then
  ok "服务端生成物与仓库一致"
else
  bad "服务端生成物和 proto 不一致，重跑 tools/gen-proto.sh 并提交"
fi

if diff -r -q -x "*.meta" "$ROOT/src/Shared/Generated" "$ROOT/client/MMOGame/Assets/Proto/Generated" >/dev/null 2>&1; then
  ok "客户端与服务端两份生成物逐字相同"
else
  bad "两端生成物不同"
fi

PROTO="$ROOT/proto/mmo/battle.proto"
ENVELOPE_OK=1
grep -q 'SkillRequest skill = 1;' "$PROTO" || ENVELOPE_OK=0
grep -q 'SkillCancelRequest cancel = 2;' "$PROTO" || ENVELOPE_OK=0
grep -q 'MoveRequest move = 3;' "$PROTO" || ENVELOPE_OK=0
grep -q 'BattleOpened opened = 1;' "$PROTO" || ENVELOPE_OK=0
grep -q 'SkillResult result = 2;' "$PROTO" || ENVELOPE_OK=0
grep -q 'SkillRejected rejected = 3;' "$PROTO" || ENVELOPE_OK=0
grep -q 'SkillCancelled cancelled = 4;' "$PROTO" || ENVELOPE_OK=0
grep -q 'BattleEnded ended = 5;' "$PROTO" || ENVELOPE_OK=0
grep -q 'MoveState moved = 6;' "$PROTO" || ENVELOPE_OK=0
if [ "$ENVELOPE_OK" = "1" ]; then
  ok "收发信封的字段号没动，移动占了 3 和 6"
else
  bad "信封字段号被改过，客户端会解析错位"
fi

# P0-1a 定的请求与结果字段、P0-1b 的取消、P0-1c 的 target_hp_after 都不能动。
LEGACY_OK=1
grep -q 'uint32 skill_id = 1;' "$PROTO" || LEGACY_OK=0
grep -q 'uint64 target_id = 2;' "$PROTO" || LEGACY_OK=0
grep -q 'int64 cast_time_ms = 3;' "$PROTO" || LEGACY_OK=0
grep -q 'bool hit = 4;' "$PROTO" || LEGACY_OK=0
grep -q 'int64 damage = 5;' "$PROTO" || LEGACY_OK=0
grep -q 'uint32 cooldown_ms = 8;' "$PROTO" || LEGACY_OK=0
grep -q 'int64 target_hp_after = 9;' "$PROTO" || LEGACY_OK=0
grep -q 'bool accepted = 3;' "$PROTO" || LEGACY_OK=0
if [ "$LEGACY_OK" = "1" ]; then
  ok "P0-1a~P0-1d 定下的字段和编号都还在"
else
  bad "前几张卡的字段被改动，老客户端会读错"
fi

# 技能表随开场下发，客户端才知道每个技能前摇多长。
DOWNLOAD_OK=1
grep -q 'repeated SkillInfo skills = 9;' "$PROTO" || DOWNLOAD_OK=0
grep -q 'uint32 foreswing_ms = 4;' "$PROTO" || DOWNLOAD_OK=0
grep -q 'uint32 arena_radius_mm = 10;' "$PROTO" || DOWNLOAD_OK=0
grep -q 'uint32 move_speed_mm_per_s = 11;' "$PROTO" || DOWNLOAD_OK=0
if [ "$DOWNLOAD_OK" = "1" ]; then
  ok "技能表与场地参数随开场包下发"
else
  bad "开场包没下发技能表或场地参数，客户端只能写死数字"
fi

echo "== 2. 编译 =="
if "$DOTNET" build "$ROOT/src/Battle/Battle.csproj" -c Release -v q --nologo >"$TMP/build.log" 2>&1; then
  ok "战斗进程编译通过"
else
  bad "战斗进程编译失败"
  tail -8 "$TMP/build.log" | detail
  echo
  echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 3. 静态断言 =="
HUD="$ROOT/client/MMOGame/Assets/HotUpdate/BattleHud.cs"
CLIENT="$ROOT/client/MMOGame/Assets/HotUpdate/BattleClient.cs"
HERO="$ROOT/client/MMOGame/Assets/HotUpdate/HeroAvatar.cs"

if grep -q 'info.ForeswingMs' "$CLIENT" && ! grep -qE 'ForeswingMs = [0-9]+;' "$CLIENT"; then
  ok "前摇时长从开场包读，前端没写死"
else
  bad "客户端自己写死了前摇时长"
fi

if grep -q 'BossHp = result.TargetHpAfter' "$CLIENT"; then
  ok "血量只认回包里的 target_hp_after"
else
  bad "客户端没有按回包改血量"
fi

if grep -q 'Pointer.current' "$HUD" && grep -q 'Pointer.current' "$HERO"; then
  ok "界面与走位走新输入系统（activeInputHandler=1 下旧 API 是死的）"
else
  bad "界面或走位用了旧输入 API，真机上按不动"
fi

if grep -qE 'Hp(Max)?[[:space:]]*[-+]=|(damage|Damage)[[:space:]]*[-+]=' \
     -r "$ROOT/client/MMOGame/Assets/Net" "$ROOT/client/MMOGame/Assets/HotUpdate" 2>/dev/null; then
  bad "客户端在算血量和伤害，这些只能来自服务端"
else
  ok "客户端没有算血量和伤害"
fi

if grep -qE 'File\.(Write|Append|Create)|Directory\.Create' -r "$ROOT/client/MMOGame/Assets/HotUpdate" 2>/dev/null; then
  bad "热更侧在写文件，结算不该落在这里"
else
  ok "热更侧不落盘"
fi

if grep -q 'BattleHud.HitTest' "$ROOT/client/MMOGame/Assets/HotUpdate/TargetPicker.cs"; then
  ok "点选避开技能键所在区域"
else
  bad "点技能键会顺带换目标"
fi

echo "== 4. 起战斗进程并走一轮 =="
python3 - "$ROOT/config/battle.json" "$TMP/battle.json" "$TMP" <<'PY'
import json, sys
src, dst, tmp = sys.argv[1], sys.argv[2], sys.argv[3]
cfg = json.load(open(src, encoding='utf-8'))
cfg['listen'] = '127.0.0.1:0'
cfg['settlement_dir'] = tmp + '/settlements'
cfg['skill_table'] = 'skills.json'
json.dump(cfg, open(dst, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
cp "$ROOT/config/skills.json" "$TMP/skills.json"

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
  echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

UNITY_LOG="$TMP/unity.log"
MMO_BATTLE_URL="$WS_URL" \
  "$UNITY" -batchmode -quit -projectPath "$CLIENT_PROJ" \
  -executeMethod P02bCheck.Run -logFile "$UNITY_LOG" >/dev/null 2>&1
UNITY_EXIT=$?

if grep -qE "error CS|Compilation failed" "$UNITY_LOG" 2>/dev/null; then
  bad "客户端编译失败"
  grep -E "error CS" "$UNITY_LOG" | head -5 | detail
fi

if [ "$UNITY_EXIT" -eq 0 ]; then
  ok "客户端跑完，退出码 0"
else
  bad "客户端退出码 ${UNITY_EXIT}"
  grep -E "^p02b_check|Exception" "$UNITY_LOG" | head -8 | detail
fi

OPENED_LINE="$(grep -m1 '^p02b_check_open ' "$UNITY_LOG" 2>/dev/null || true)"
[ -n "$OPENED_LINE" ] && printf '  %s\n' "$OPENED_LINE"

check_item() {
  local name="$1" label="$2"
  if grep -q "^p02b_check_ok ${name} " "$UNITY_LOG" 2>/dev/null; then
    ok "$label"
  else
    bad "$label"
    grep -m1 "^p02b_check_bad ${name} " "$UNITY_LOG" 2>/dev/null | detail
  fi
}

check_item opened                 "开场包：双方满血"
check_item opened_skills          "开场包带 3 个技能"
check_item opened_arena           "场地半径 8000mm、速度 3000mm/s 来自配置"
check_item opened_distance        "开局站位 6000mm"
check_item far_rejected           "站在 6000mm 时近战被拒（距离不够）"
check_item far_hp_unchanged       "被拒之后 Boss 血量没动"
check_item move_clamped           "一次跨 4000mm 的走位被服务端夹住"
check_item move_step_limited      "夹取后只走了一个上报间隔的量"
check_item move_near              "一步一步走近到 2000mm"
check_item near_hit               "走近之后同一技能打中"
check_item hit_damage             "伤害 120 来自技能表"
check_item cancel_accepted        "前摇里撤掉，服务端接受"
check_item cancel_no_damage       "撤掉之后没有迟到的结算"
check_item out_of_arena_clamped   "走到场外被场地半径拉回"

if grep -q '^p02b_check_summary=ok' "$UNITY_LOG" 2>/dev/null; then
  ok "编辑器核对汇总全过"
else
  bad "编辑器核对有未通过项"
  grep '^p02b_check_bad ' "$UNITY_LOG" 2>/dev/null | head -5 | detail
fi

if [ "$DEVICE" != "1" ]; then
  echo "== 5. 真机段 =="
  echo "       （加 --device 跑真机段；本次跳过）"
  echo
  echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
  [ "$FAIL" -eq 0 ] && exit 0 || exit 1
fi

echo "== 5. 真机段 =="
if [ ! -x "$ADB" ]; then
  bad "找不到 adb：$ADB"
  echo
  echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

DEVICE_ID="$("$ADB" devices | awk 'NR>1 && $2=="device"{print $1; exit}')"
if [ -n "$DEVICE_ID" ]; then
  ok "设备在线：${DEVICE_ID}"
else
  bad "没有已授权的设备"
  echo
  echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

# 真机段要固定端口：平板里连 127.0.0.1:60001，靠 adb reverse 转到这台机器上。
stop_battle

python3 - "$ROOT/config/battle.json" "$TMP/device.json" "$TMP" "$DEVICE_PORT" <<'PY'
import json, sys
src, dst, tmp, port = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
cfg = json.load(open(src, encoding='utf-8'))
cfg['listen'] = '127.0.0.1:' + port
cfg['settlement_dir'] = tmp + '/device-settlements'
cfg['skill_table'] = 'skills.json'
json.dump(cfg, open(dst, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY

: > "$TMP/device.log"
"$DOTNET" "$BATTLE_DLL" --config "$TMP/device.json" >>"$TMP/device.log" 2>&1 &
BATTLE_PID=$!

READY=0
for _ in $(seq 1 120); do
  if grep -q "^listen=http://127.0.0.1:${DEVICE_PORT}\$" "$TMP/device.log" 2>/dev/null; then
    READY=1
    break
  fi
  if ! kill -0 "$BATTLE_PID" 2>/dev/null; then
    break
  fi
  sleep 0.1
done

if [ "$READY" = "1" ]; then
  ok "战斗进程固定听 127.0.0.1:${DEVICE_PORT}"
else
  bad "战斗进程没在 ${DEVICE_PORT} 上起来"
  tail -10 "$TMP/device.log" | detail
  echo
  echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

"$ADB" -s "$DEVICE_ID" reverse "tcp:${DEVICE_PORT}" "tcp:${DEVICE_PORT}" >/dev/null 2>&1 \
  && ok "adb reverse 已把 ${DEVICE_PORT} 映到平板" \
  || bad "adb reverse 失败"

echo "       （E7Build 出 APK，几分钟）"
BUILD_LOG="$TMP/build-apk.log"
if [ "${MMO_SKIP_BUILD:-0}" = "1" ] && [ -f "$APK" ]; then
  # 调试用：客户端没改时复用上一个包，省掉七十多秒。
  ok "复用已有 APK：$(du -h "$APK" | awk '{print $1}')"
else
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
    echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
    exit 1
  fi
fi

"$ADB" -s "$DEVICE_ID" shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1 || true
INSTALL_LOG="$TMP/install.log"
if "$ADB" -s "$DEVICE_ID" install -r -t "$APK" >"$INSTALL_LOG" 2>&1; then
  ok "APK 已装上设备"
else
  bad "安装失败"
  tail -5 "$INSTALL_LOG" | detail
  echo
  echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

"$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
"$ADB" -s "$DEVICE_ID" shell am force-stop "$PKG" >/dev/null 2>&1 || true

# 出包那几分钟里 adb daemon 可能重连，reverse 规则会跟着丢。
# 启动前再挂一次，并以 --list 的实际结果为准。
"$ADB" -s "$DEVICE_ID" reverse "tcp:${DEVICE_PORT}" "tcp:${DEVICE_PORT}" >/dev/null 2>&1 || true
if "$ADB" -s "$DEVICE_ID" reverse --list 2>/dev/null | grep -q "tcp:${DEVICE_PORT}"; then
  ok "启动前 reverse 在位：${DEVICE_PORT}"
else
  bad "启动前 reverse 不见了，平板连不到这台机器"
fi

# 等进程真的退掉，否则下一次 am start 会被系统忽略，界面上看是「启动了」其实没跑。
for _ in $(seq 1 12); do
  "$ADB" -s "$DEVICE_ID" shell pidof "$PKG" >/dev/null 2>&1 || break
  sleep 0.5
done

"$ADB" -s "$DEVICE_ID" shell am start -n "${PKG}/com.unity3d.player.UnityPlayerActivity" >/dev/null 2>&1

# 刚装完的系统还在优化，am start 有时不起作用。以「场景打印出 p02a_scene」为准，
# 不要用 pidof——force-stop 之后旧进程还可能在那儿，会把启动失败当成成功。
APP_UP=0
for _ in $(seq 1 40); do
  "$ADB" -s "$DEVICE_ID" logcat -d -v brief -s Unity > "$TMP/boot.log" 2>/dev/null || true
  if grep -q 'p02a_scene=ok' "$TMP/boot.log"; then
    APP_UP=1
    break
  fi
  sleep 1
  if [ $(( _ % 8 )) -eq 0 ]; then
    "$ADB" -s "$DEVICE_ID" shell am start -n "${PKG}/com.unity3d.player.UnityPlayerActivity" >/dev/null 2>&1
  fi
done

if [ "$APP_UP" = "1" ]; then
  ok "应用已启动，场景加载完成"
else
  bad "应用没起来（没等到 p02a_scene）"
  grep -E 'E/Unity|FATAL' "$TMP/boot.log" 2>/dev/null | head -5 | detail
  echo
  echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

LOG="$TMP/logcat.txt"
OPENED_OK=0
for _ in $(seq 1 90); do
  "$ADB" -s "$DEVICE_ID" logcat -d -v brief -s Unity > "$LOG" 2>/dev/null || true
  if grep -q 'p02b_opened' "$LOG"; then
    OPENED_OK=1
    break
  fi
  if grep -q 'p02b_connect=fail' "$LOG"; then
    # 连不上就别等满 90 秒，早点把原因摆出来。
    break
  fi
  sleep 1
done

if [ "$OPENED_OK" = "1" ]; then
  ok "平板连上战斗进程，收到开场包"
  grep -m1 'p02b_opened' "$LOG" | sed 's/^.*Unity *: /  /'
else
  bad "平板没连上战斗进程"
  grep -E 'p02b_connect|p02b_recv|p02a_scene|Exception' "$LOG" 2>/dev/null | head -8 | detail
  echo
  echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

SCENE_OK=0
for _ in $(seq 1 40); do
  if grep -q 'p02b_scene=ready' "$LOG"; then
    SCENE_OK=1
    break
  fi
  sleep 1
  "$ADB" -s "$DEVICE_ID" logcat -d -v brief -s Unity > "$LOG" 2>/dev/null || true
done

if [ "$SCENE_OK" = "1" ]; then
  ok "玩家与界面已就位"
else
  bad "场景没准备好（没有 p02b_scene=ready）"
fi

# 按目标编号取 adb 口径的点击坐标。
TAP_BOSS="$(python3 - "$LOG" <<'PY'
import re, sys
text = open(sys.argv[1], encoding='utf-8', errors='ignore').read()
m = re.search(r'target_screen=9001 x=(-?\d+) y=(-?\d+)', text)
print(f"{m.group(1)} {m.group(2)}" if m else "")
PY
)"

if [ -n "$TAP_BOSS" ]; then
  set -- $TAP_BOSS
  # 平板刚把焦点交给 Unity 时，第一次注入的触摸会被系统吃掉。
  # 先点一下左上角把输入链路叫醒，再点目标。
  "$ADB" -s "$DEVICE_ID" shell input swipe 20 20 20 20 220 >/dev/null 2>&1
  sleep 0.6
  "$ADB" -s "$DEVICE_ID" shell input swipe "$1" "$2" "$1" "$2" 220 >/dev/null 2>&1
  sleep 1
  "$ADB" -s "$DEVICE_ID" logcat -d -v brief -s Unity > "$LOG" 2>/dev/null || true
  if grep -q 'select_target=9001' "$LOG"; then
    ok "点中 Boss：select_target=9001"
  else
    bad "没点中 Boss"
    grep -E 'p02a_tap|p02a_ray' "$LOG" 2>/dev/null | tail -4 | detail
  fi
else
  bad "没拿到 Boss 的点击坐标"
fi

# 技能键的位置由界面启动时打出来，和判定用的是同一套像素。
RECT="$(python3 - "$LOG" <<'PY'
import re, sys
text = open(sys.argv[1], encoding='utf-8', errors='ignore').read()
m = re.search(r'skill_rect=(-?\d+),(-?\d+)', text)
print(f"{m.group(1)} {m.group(2)}" if m else "")
PY
)"

if [ -z "$RECT" ]; then
  bad "没拿到技能键的位置"
  echo
  echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

set -- $RECT
SKILL_X=$1
SKILL_Y=$2
SIZE=200
GAP=24

# 屏幕尺寸以界面自己打的那行为准。wm size 是物理竖屏，和 Unity 的横屏坐标不是一套。
SCREEN_SIZE="$(python3 - "$LOG" <<'PY'
import re, sys
text = open(sys.argv[1], encoding='utf-8', errors='ignore').read()
m = re.search(r'p02b_hud=ready screen=(\d+)x(\d+)', text)
print(f"{m.group(1)} {m.group(2)}" if m else "")
PY
)"

if [ -z "$SCREEN_SIZE" ]; then
  bad "没拿到屏幕尺寸"
  echo
  echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

set -- $SCREEN_SIZE
SCREEN_W=$1
SCREEN_H=$2

S1_CX=$(( SKILL_X + SIZE / 2 ))
S1_CY=$(( SCREEN_H - SKILL_Y - SIZE / 2 ))
S2_CX=$(( S1_CX + SIZE + GAP ))
S3_CX=$(( S2_CX + SIZE + GAP ))
CANCEL_CX=$S3_CX
CANCEL_CY=$(( SCREEN_H - (SKILL_Y + SIZE + GAP + 45) ))

echo "       技能键中心 S1=(${S1_CX},${S1_CY}) S2=(${S2_CX},${S1_CY}) S3=(${S3_CX},${S1_CY}) 撤键=(${CANCEL_CX},${CANCEL_CY})"

# 远处点 S1：3000mm 的近战够不着 6000mm，服务端必须拒，界面停住且血量不变。
"$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
"$ADB" -s "$DEVICE_ID" shell input swipe "$S1_CX" "$S1_CY" "$S1_CX" "$S1_CY" 220 >/dev/null 2>&1
sleep 2
"$ADB" -s "$DEVICE_ID" logcat -d -v brief -s Unity > "$LOG" 2>/dev/null || true

if grep -q 'p02b_cast_end .*result=rejected' "$LOG"; then
  ok "远处点近战被拒：$(grep -m1 'p02b_cast_end .*result=rejected' "$LOG" | sed 's/^.*Unity *: //')"
else
  bad "远处点近战没有被拒"
  grep -m3 'p02b_cast' "$LOG" | detail
fi

if grep -q 'p02b_cast_end .*reason=SkillRejectOutOfRange' "$LOG"; then
  ok "拒绝原因是距离不够"
else
  bad "拒绝原因不是距离"
fi

if grep -q 'p02b_cast_start' "$LOG"; then
  ok "按下之后先播了前摇"
else
  bad "没有前摇就开始等结果"
fi

# 走近：玩家出生在 Boss 右侧 6 米，要向左拖摇杆。一次 400px 约走 0.6 米。
"$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
STICK_X=$(( SCREEN_W / 5 ))
STICK_Y=$(( SCREEN_H / 2 ))
for _ in 1 2 3 4 5 6 7 8 9 10; do
  "$ADB" -s "$DEVICE_ID" shell input swipe "$(( STICK_X + 400 ))" "$STICK_Y" "$STICK_X" "$STICK_Y" 240 >/dev/null 2>&1
  sleep 0.3
done
sleep 1
"$ADB" -s "$DEVICE_ID" logcat -d -v brief -s Unity > "$LOG" 2>/dev/null || true

MOVE_LINE="$(grep 'p02b_move' "$LOG" 2>/dev/null | tail -1 || true)"
if [ -n "$MOVE_LINE" ]; then
  ok "走位生效：$(printf '%s' "$MOVE_LINE" | sed 's/^.*Unity *: //')"
else
  bad "走位没有任何回包"
fi

NEAR_DIST="$(printf '%s' "$MOVE_LINE" | tr ' ' '\n' | sed -n 's/^distance=//p' | tail -1)"
if [ -n "$NEAR_DIST" ] && [ "$NEAR_DIST" -lt 4500 ]; then
  ok "走近了：距离 ${NEAR_DIST}mm，比开局 6000mm 明显近"
else
  bad "距离没有明显变小（${NEAR_DIST:-没有读数}）"
fi

# 走近后点 S1 应该打中。
"$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
"$ADB" -s "$DEVICE_ID" shell input swipe "$S1_CX" "$S1_CY" "$S1_CX" "$S1_CY" 220 >/dev/null 2>&1
sleep 2
"$ADB" -s "$DEVICE_ID" logcat -d -v brief -s Unity > "$LOG" 2>/dev/null || true

if grep -q 'p02b_cast_end .*result=hit' "$LOG"; then
  ok "走近后命中：$(grep -m1 'p02b_cast_end .*result=hit' "$LOG" | sed 's/^.*Unity *: //')"
else
  bad "走近后还是没打中"
  grep -m3 'p02b_cast' "$LOG" | detail
fi

# 前摇里撤掉：S3 前摇 900ms，点完 200ms 就点撤键。
"$ADB" -s "$DEVICE_ID" logcat -c >/dev/null 2>&1 || true
"$ADB" -s "$DEVICE_ID" shell input swipe "$S3_CX" "$S1_CY" "$S3_CX" "$S1_CY" 220 >/dev/null 2>&1
sleep 0.2
"$ADB" -s "$DEVICE_ID" shell input swipe "$CANCEL_CX" "$CANCEL_CY" "$CANCEL_CX" "$CANCEL_CY" 220 >/dev/null 2>&1
sleep 1.5
"$ADB" -s "$DEVICE_ID" logcat -d -v brief -s Unity > "$LOG" 2>/dev/null || true

if grep -q 'p02b_cancel .*accepted=True' "$LOG"; then
  ok "前摇里撤掉，服务端接受"
else
  bad "撤销没被接受"
  grep -m3 'p02b_cancel' "$LOG" | detail
fi

if grep -q 'p02b_cast_start skill=1003' "$LOG" && ! grep -q 'p02b_cast_end skill=1003 ' "$LOG"; then
  ok "撤掉那次没有迟到的结算"
else
  bad "撤掉之后还是收到了结算"
  grep -m3 'p02b_cast_end skill=1003' "$LOG" | detail
fi

"$ADB" -s "$DEVICE_ID" reverse --remove "tcp:${DEVICE_PORT}" >/dev/null 2>&1 || true

echo
echo "P0-2b 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
