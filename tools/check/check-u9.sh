#!/usr/bin/env bash
# tools/check/check-u9.sh —— U9 洞府摆放屏接 C3（开发架构第 14 节第 79 行）。
#
# 完成标准：F04 读 /homestead 展示六格；摆 / 撤走 POST；种植与家园币由 U10 另测；
# 不读 config、不动 proto。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
TITLE="U9（client）"
TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

if [ ! -d "$ROOT/client/MMOGame" ]; then
  echo "  skip 这台机器没有客户端工程"
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
if ROOT="$ROOT" "$PYBIN" "$ROOT/tools/check/check-u9-static.py" >"$TMP/static.out" 2>&1; then
  ok "洞府屏接线、摆放事实、总控已挂 U9"
else
  bad "U9 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器自检 =="
if [ ! -x "$UNITY" ] && [ ! -f "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U9Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U9Check.Run 退出码 0"
else
  bad "Unity U9Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files net_parse flow_homestead screen_wired codex_slots no_config no_proto self; do
  if grep -q "u9_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u9_${key}=ok"
  else
    bad "编辑器自检 u9_${key} 没过"
    grep "u9_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

for key in pending empty place remove unavailable skin_layout skin_copy; do
  if grep -q "u9_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "洞府自检 u9_self_${key}=ok"
  else
    bad "洞府自检 u9_self_${key} 没过"
    grep "u9_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

finish "$TITLE"
