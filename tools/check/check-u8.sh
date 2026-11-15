#!/usr/bin/env bash
# tools/check/check-u8.sh —— U8 界面入口（开发架构第 14 节第 78 行）。
#
# 完成标准：HUD 七格能打开已有 U 屏（不再 Toast 占位）；U5/U6/U7/U3 有二级入口；
# 载入确认后进表世界；结算「返回表世界」走 Hub；不读 config、不动 proto。
#
#   bash tools/check/check-u8.sh
#       静态断言 + Unity 编辑器自检
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
TITLE="U8（client）"
TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

if [ ! -d "$ROOT/client/MMOGame" ]; then
  echo "  skip 这台机器没有客户端工程（U 卡在 Mac 上跑）"
  finish "$TITLE"
fi

echo "== 1. 静态断言 =="
PYBIN=""
for c in python3 python; do
  if command -v "$c" >/dev/null 2>&1; then PYBIN="$c"; break; fi
done
if [ -z "$PYBIN" ]; then
  bad "找不到 python3 / python"
  finish "$TITLE"
fi
if ROOT="$ROOT" "$PYBIN" "$ROOT/tools/check/check-u8-static.py" >"$TMP/static.out" 2>&1; then
  ok "入口枢纽齐全、HUD 已接线、总控已挂 U8"
else
  bad "U8 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器入口自检 =="
if [ ! -x "$UNITY" ]; then
  # Windows 常见路径：调用方显式给 UNITY=...
  if [ -n "${UNITY:-}" ] && [ -f "$UNITY" ]; then
    :
  else
    bad "找不到 Unity：$UNITY"
    finish "$TITLE"
  fi
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U8Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U8Check.Run 退出码 0"
else
  bad "Unity U8Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files hud_hub hub_ready entry_gate settle_back enter_done no_toast_placeholder \
           no_config no_proto self; do
  if grep -q "u8_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u8_${key}=ok"
  else
    bad "编辑器自检 u8_${key} 没过"
    grep "u8_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

for key in hud seven u5_enhance u5_realm u6_auction u6_pass u7_home u7_codex \
           u3_party back_world settle_back skin_layout skin_copy; do
  if grep -q "u8_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "入口自检 u8_self_${key}=ok"
  else
    bad "入口自检 u8_self_${key} 没过"
    grep "u8_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

finish "$TITLE"
