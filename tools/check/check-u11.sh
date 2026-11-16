#!/usr/bin/env bash
# tools/check/check-u11.sh —— U11 英灵图鉴屏（开发架构第 14 节第 94 行）。
#
# 完成标准：F05 读 GET /hero-codex；九格名字与收集度、被动 bps 只读回包；
# 去掉图鉴缺口文案；不本地算、不编条目。
#
#   bash tools/check/check-u11.sh
#       静态断言 + Unity 编辑器自检（九格 / 收集度 / 被动 / 空条目 / 拒绝 / 切皮肤）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
TITLE="U11（client）"
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
if ROOT="$ROOT" "$PYBIN" "$ROOT/tools/check/check-u11-static.py" >"$TMP/static.out" 2>&1; then
  ok "图鉴接线、缺口文案已去、总控已挂 U11"
else
  bad "U11 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器自检 =="
if [ ! -x "$UNITY" ] && [ ! -f "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U11Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U11Check.Run 退出码 0"
else
  bad "Unity U11Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files net_parse flow_codex screen_wired codex_wired no_names no_config no_proto self; do
  if grep -q "u11_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u11_${key}=ok"
  else
    bad "编辑器自检 u11_${key} 没过"
    grep "u11_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 3. 屏上自检 =="
for key in pending entries collected passive advance no_entries reject skin_layout skin_copy; do
  if grep -q "u11_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "图鉴自检 u11_self_${key}=ok"
  else
    bad "图鉴自检 u11_self_${key} 没过"
    grep "u11_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 4. 不做项 =="
if git -C "$ROOT" diff --quiet -- config proto 2>/dev/null; then
  ok "config/ 与 proto/ 一行未改"
else
  if git -C "$ROOT" rev-parse --git-dir >/dev/null 2>&1; then
    bad "这张卡改了 config/ 或 proto/"
    git -C "$ROOT" diff --name-only -- config proto | detail
  else
    echo "  skip 不是 git 仓库，跳过改动比对"
  fi
fi

finish "$TITLE"
