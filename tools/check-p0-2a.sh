#!/usr/bin/env bash
# P0-2a 核对：点选来自热更程序集，Boss 场景来自 Launch AB，目标编号进请求。
#
# 默认只跑编辑器侧与静态断言（快）：
#   1. 编辑器核对 P02aCheck：热更 dll 装载、Select 切换、请求构造、场景、包检查
#   2. 静态断言：加载链在 AOT、点选只在热更程序集、热更目录不加血不算伤害、场景被收集
#
# 加 --device 再跑真机段（要先插好已授权的设备）：
#   3. E7Build 出新 APK（含 Boss 场景与热更 dll 的 Launch AB）
#   4. adb install → 启动 → 等 p02a_scene=ok → input tap 两个目标 → select_target=9001/9002
#
# 用法：tools/check-p0-2a.sh [--device]
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
PROJ="$ROOT/client/MMOGame"
ADB="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
LOG="$(mktemp)"
DEVICE=0
[ "${1:-}" = "--device" ] && DEVICE=1

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
detail() { printf '       %s\n' "$1"; }

if [ ! -x "$UNITY" ]; then
  echo "找不到 Unity：$UNITY" >&2
  exit 1
fi

echo "== 1. 编辑器核对 P02aCheck =="
"$UNITY" -batchmode -quit -projectPath "$PROJ" \
  -executeMethod P02aCheck.Run -logFile "$LOG" >/dev/null 2>&1
UNITY_EXIT=$?

python3 - "$LOG" <<'PY' > /tmp/p02a-lines.txt
import re, sys
t = open(sys.argv[1], encoding='utf-8', errors='ignore').read()
for line in t.splitlines():
    if re.match(r'p02a_', line):
        print(line)
PY

while IFS= read -r line; do
  case "$line" in
    "p02a_select=ok "*)   ok "点选切换：${line#p02a_select=} " ;;
    "p02a_request=ok "*)  ok "目标编号进请求：${line#p02a_request=} " ;;
    "p02a_scene=ok "*)    ok "场景在收集范围：${line#p02a_scene=} " ;;
    "p02a_assets=ok "*)   ok "包内容检查认新资源" ;;
    "p02a_realm_reject=ok"*) ok "后期境界资源仍被拒绝" ;;
    "p02a_entry=ok "*)    ok "热更进门还在：${line#p02a_entry=} " ;;
    "p02a_check=ok"*)     ok "汇总：${line} " ;;
    p02a_*) bad "$line" ;;
  esac
done < /tmp/p02a-lines.txt
[ "$UNITY_EXIT" = "0" ] && ok "Unity 退出码 0" || bad "Unity 退出码 $UNITY_EXIT"

echo "== 2. 静态断言 =="
# 加载链在 AOT：热更 dll 的加载与场景加载都在主包侧
# 静态断言口径：热更目录只许读服务端给的值，不许自己加血算伤害（同 E7 与 P0-1c）
# 注意别写成管道喂 while——子 shell 里 ok/bad 的计数出了管道就丢了
STATIC_OUT="$(python3 - "$PROJ" <<'PY'
import re, sys, pathlib
proj = pathlib.Path(sys.argv[1])
ls = (proj / 'Assets/AOT/LaunchSession.cs').read_text(encoding='utf-8')
hb = (proj / 'Assets/Editor/E6Build.cs').read_text(encoding='utf-8')
scene = (proj / 'Assets/Scenes/BossBattle.unity').read_text(encoding='utf-8')
aot_hits = []
for d in ('Assets/AOT', 'Assets/Net'):
    for f in (proj / d).glob('*.cs'):
        t = f.read_text(encoding='utf-8')
        if 'TargetSelector' in t or 'TargetPicker' in t:
            aot_hits.append(f.name)
hot_texts = ''.join(f.read_text(encoding='utf-8') for f in (proj / 'Assets/HotUpdate').glob('*.cs'))
ARITH = re.compile(r'Hp(Max)?\s*[-+]=|\b\w*[Dd]amage\s*[-+]=')
arith_hits = sorted({m.group(0).strip() for m in ARITH.finditer(hot_texts)})
for h in arith_hits:
    print('detail ' + h)
checks = {
    'AOT 加载链：热更 dll、程序集、场景、顺序': (
        "LoadAssetAsync<TextAsset>" in ls and 'Assembly.Load' in ls
        and 'LoadSceneAsync' in ls and ls.find('LoadAssetAsync<TextAsset>') < ls.find('LoadSceneAsync')),
    '收集器收场景与 dll': 'Assets/Scenes/BossBattle.unity' in hb and 'HotDllDir' in hb,
    '场景含两个目标编号': 'TargetId: 9001' in scene and 'TargetId: 9002' in scene,
    '点选只在热更程序集': not aot_hits,
    '热更目录不加血、不算伤害': not arith_hits,
}
for k, v in checks.items():
    print(('ok   ' if v else 'FAIL ') + k)
PY
)"
while IFS= read -r line; do
  case "$line" in
    "ok   "*) ok "${line#ok   }" ;;
    "FAIL "*) bad "${line#FAIL }" ;;
    "detail "*) detail "${line#detail }" ;;
  esac
done <<< "$STATIC_OUT"

echo "== 3. 真机段 =="
if [ "$DEVICE" = "1" ]; then
  if [ ! -x "$ADB" ]; then
    bad "没有 adb：$ADB"
  elif ! "$ADB" devices | tail -n +2 | grep -q "device$"; then
    bad "没有已授权的设备（adb devices 里没有 device 状态）"
  else
    SERIAL="$("$ADB" devices | tail -n +2 | grep "device$" | head -1 | cut -f1)"
    ok "设备在线：$SERIAL"

    echo "  （E7Build 出 APK，几分钟）"
    "$UNITY" -batchmode -quit -projectPath "$PROJ" \
      -executeMethod E7Build.Run -logFile "$LOG" >/dev/null 2>&1
    BUILD_EXIT=$?
    python3 - "$LOG" <<'PY'
import re, sys
t = open(sys.argv[1], encoding='utf-8', errors='ignore').read()
for line in t.splitlines():
    if re.match(r'(build_result=|build_apk=|build_hotdll=|build_ab=)', line):
        print(line)
PY
    if [ "$BUILD_EXIT" = "0" ] && [ -f /tmp/mmo-e7-apk/MMOGame.apk ]; then
      ok "APK 打包完成：$(du -h /tmp/mmo-e7-apk/MMOGame.apk | cut -f1)"
    else
      bad "APK 打包失败，退出码 $BUILD_EXIT"
    fi

    "$ADB" -s "$SERIAL" shell input keyevent KEYCODE_WAKEUP
    "$ADB" -s "$SERIAL" shell svc power stayon usb
    "$ADB" -s "$SERIAL" install -r /tmp/mmo-e7-apk/MMOGame.apk >/tmp/p02a-install.txt 2>&1
    if grep -q "Success" /tmp/p02a-install.txt; then
      ok "APK 已装上设备"
    else
      bad "安装失败：$(tail -2 /tmp/p02a-install.txt)"
    fi

    "$ADB" -s "$SERIAL" logcat -c
    "$ADB" -s "$SERIAL" shell monkey -p com.yunjinfanxing.mmogame -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1

    wait_log() {
      local pattern="$1" timeout="${2:-120}" waited=0
      while [ "$waited" -lt "$timeout" ]; do
        "$ADB" -s "$SERIAL" logcat -d -s Unity > /tmp/p02a-logcat.txt 2>/dev/null
        if grep -q "$pattern" /tmp/p02a-logcat.txt; then
          grep -m1 "$pattern" /tmp/p02a-logcat.txt
          return 0
        fi
        sleep 1
        waited=$((waited + 1))
      done
      return 1
    }

    if wait_log "p02a_scene=ok" 120; then
      ok "场景起来：$(grep -m1 'p02a_scene=ok' /tmp/p02a-logcat.txt | sed 's/.*\(p02a_scene=ok[^ ]*\).*/\1/')"
    else
      bad "120 秒内没等到 p02a_scene=ok"
    fi

    # 目标屏幕坐标由场景自己打出来，屏幕方向与分辨率变了也不用改脚本。
    tap_at() {
      local id="$1"
      python3 - "$id" <<'PY' > /tmp/p02a-tap.txt
import re, sys
t = open('/tmp/p02a-logcat.txt', encoding='utf-8', errors='ignore').read()
m = re.search(r'target_screen=' + sys.argv[1] + r' x=(-?\d+) y=(-?\d+)', t)
print(f'{m.group(1)} {m.group(2)}' if m else '')
PY
      read -r tx ty < /tmp/p02a-tap.txt
      if [ -z "${tx:-}" ]; then
        bad "日志里没有目标 $id 的屏幕坐标"
        return 1
      fi
      "$ADB" -s "$SERIAL" shell input tap "$tx" "$ty"
      return 0
    }

    tap_at 9001
    if wait_log "select_target=9001" 10; then
      ok "点中石像：select_target=9001（坐标 $(cat /tmp/p02a-tap.txt)）"
    else
      bad "没点中 9001"
    fi

    tap_at 9002
    if wait_log "select_target=9002" 10; then
      ok "换到木桩：select_target=9002（坐标 $(cat /tmp/p02a-tap.txt)）"
    else
      bad "没点中 9002"
    fi
  fi
else
  detail "（加 --device 跑真机段；本次跳过）"
fi

echo "== 结果 =="
echo "  通过 $PASS / 未通过 $FAIL"
if [ "$FAIL" != "0" ]; then
  exit 1
fi
