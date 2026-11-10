#!/usr/bin/env bash
# E7 核对：热更程序集能加载，热更包里没有伤害结算，IL2CPP 补丁已装并能出包。
#
# 默认只跑编辑器侧（快）：
#   0. 补丁版本：包版本与本地 il2cpp 里写的版本一致，且 hybridclr 源码在
#   1. 登记热更程序集 → 扫一遍热更目录 → 按 Android 目标编译 dll → 当字节流装进运行时
#   2. 逐项看加载结果
#   3. 热更目录里没有伤害结算
#
# 加 --build 再跑一遍 Android IL2CPP 打包（慢，十几分钟）：
#   4. 用补丁过的 il2cpp 出 apk，里面要有 libil2cpp.so 和 global-metadata.dat
#
# 用法：tools/check/check-e7.sh [--build]
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
PROJ="$ROOT/client/MMOGame"
PKG="$PROJ/Packages/com.code-philosophy.hybridclr"
LOCAL_IL2CPP="$PROJ/Library/HybridCLR/LocalIl2CppData-OSXEditor/il2cpp"
APK="/tmp/mmo-e7-apk/MMOGame.apk"
LOG="$(mktemp)"
BUILD=0
[ "${1:-}" = "--build" ] && BUILD=1

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }

if [ ! -x "$UNITY" ]; then
  echo "找不到 Unity：$UNITY" >&2
  exit 1
fi

echo "== 0. IL2CPP 补丁 =="
PKG_VERSION="$(python3 -c "import json,sys;print(json.load(open('$PKG/package.json',encoding='utf-8'))['version'])" 2>/dev/null || echo "")"
VERSION_FILE="$LOCAL_IL2CPP/libil2cpp/hybridclr/generated/libil2cpp-version.txt"
INSTALLED="$(python3 -c "import sys;print(open(sys.argv[1],encoding='utf-8-sig').read().strip())" "$VERSION_FILE" 2>/dev/null || echo "")"
if [ -d "$LOCAL_IL2CPP/libil2cpp/hybridclr" ]; then
  ok "本地 il2cpp 里有 hybridclr 源码"
else
  bad "本地 il2cpp 里没有 hybridclr，补丁没装（跑 E7Install.Run，来源目录由 MMO_IL2CPP_SRC 指定）"
fi
if [ -n "$PKG_VERSION" ] && [ "$INSTALLED" = "$PKG_VERSION" ]; then
  ok "补丁版本与包一致：$INSTALLED"
else
  bad "补丁版本 ${INSTALLED:-空} 与包版本 ${PKG_VERSION:-空} 不一致"
fi

echo "== 1. 编译热更 dll 并装进运行时 =="
"$UNITY" -batchmode -quit -projectPath "$PROJ" \
  -executeMethod E7Check.Run -logFile "$LOG" >/dev/null 2>&1
UNITY_EXIT=$?

LINE="$(grep -m1 -E '^hotupdate=' "$LOG" || true)"
LOADED=0
case "$LINE" in
  hotupdate=ok\ *) LOADED=1 ;;
esac

if [ "$UNITY_EXIT" -eq 0 ] && [ "$LOADED" = "1" ]; then
  ok "热更程序集能加载"
  printf '  %s\n' "$LINE"
else
  bad "热更程序集没加载起来（Unity 退出码 ${UNITY_EXIT}）"
  grep -E "error CS|hotupdate=fail|hotupdate_forbidden=" "$LOG" | head -5
fi

echo "== 2. 逐项看加载结果 =="
if [ "$LOADED" = "1" ]; then
  check_field() {
    case "$LINE" in
      *" $1=$2 "*|*" $1=$2") ok "$1=$2" ;;
      *) bad "期望 $1=$2，实际没有" ;;
    esac
  }

  check_field asm HotUpdate
  check_field version 1
  check_field ping hotupdate-ok
  check_field empty_bytes 0
  check_field mode current
else
  echo "  跳过：没有加载成功的输出"
fi

echo "== 3. 热更目录里没有伤害结算 =="
case "$(grep -m1 'hotupdate_forbidden=' "$LOG" || true)" in
  "") ok "Assets/HotUpdate 下没有伤害结算" ;;
  *) bad "热更目录里出现伤害结算关键词"; grep -m3 "hotupdate_forbidden=" "$LOG" ;;
esac

rm -f "$LOG"

if [ "$BUILD" = "1" ]; then
  echo "== 4. 用补丁过的 il2cpp 出 Android 包 =="
  BLOG="$(mktemp)"
  "$UNITY" -batchmode -quit -projectPath "$PROJ" \
    -executeMethod E7Build.Run -logFile "$BLOG" >/dev/null 2>&1
  BUILD_EXIT=$?
  [ "$BUILD_EXIT" -ne 0 ] && cp "$BLOG" /tmp/e7-build-fail.log

  case "$(grep -m1 '^build_result=' "$BLOG" || true)" in
    build_result=Succeeded*) ok "IL2CPP 打包成功" ;;
    *) bad "IL2CPP 打包失败（退出码 ${BUILD_EXIT}）"
       grep -E "BuildFailed|Error building|error CS" "$BLOG" | head -5 ;;
  esac

  case "$(grep -m1 '^build_hotdll=' "$BLOG" || true)" in
    build_hotdll=ok*) ok "Android 热更 dll 编出来了" ;;
    *) bad "Android 热更 dll 没编出来" ;;
  esac

  if [ -f "$APK" ]; then
    SO_SIZE="$(unzip -l "$APK" 2>/dev/null | awk '$4=="lib/arm64-v8a/libil2cpp.so"{print $1}')"
    META_SIZE="$(unzip -l "$APK" 2>/dev/null | awk '$4 ~ /global-metadata.dat$/{print $1}')"
    if [ -n "$SO_SIZE" ]; then ok "apk 里有 libil2cpp.so（$SO_SIZE 字节）"; else bad "apk 里没有 libil2cpp.so"; fi
    if [ -n "$META_SIZE" ]; then ok "apk 里有 global-metadata.dat（$META_SIZE 字节）"; else bad "apk 里没有 global-metadata.dat"; fi
  else
    bad "没有生成 apk：$APK"
  fi
  rm -f "$BLOG"
else
  echo "== 4. Android 打包核对：跳过（加 --build 才跑）=="
fi

echo
echo "E7 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
