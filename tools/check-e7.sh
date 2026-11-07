#!/usr/bin/env bash
# E7 核对：热更程序集能加载，且热更包里没有伤害结算。
#
# 编辑器侧跑 Assets/Editor/E7Check.cs：
#   登记热更程序集 → 扫一遍热更目录 → 按 Android 目标编译 dll → 当字节流装进运行时
# 真机 IL2CPP 那一步不在这里，见 docs/开发日志.md 的 E7 卡。
#
# 用法：tools/check-e7.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
LOG="$(mktemp)"

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }

if [ ! -x "$UNITY" ]; then
  echo "找不到 Unity：$UNITY" >&2
  exit 1
fi

echo "== 1. 编译热更 dll 并装进运行时 =="
"$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
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
  bad "热更程序集没加载起来（Unity 退出码 $UNITY_EXIT）"
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

echo
echo "E7 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
