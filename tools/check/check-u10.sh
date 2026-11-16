#!/usr/bin/env bash
# tools/check/check-u10.sh —— U10 洞府种植与家园币屏（开发架构第 14 节第 93 行）。
#
# 完成标准：F04 读 /homestead/plants、plant、harvest 与 POST /home-coin/shop/buy；
# 格子与成熟事实只读回包；去掉种植缺口文案；不读 config、不动 proto。
#
#   bash tools/check/check-u10.sh
#       静态断言 + Unity 编辑器自检（种植 / 收取 / 商城 / 拒绝 / 切皮肤）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
TITLE="U10（client）"
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
if ROOT="$ROOT" "$PYBIN" "$ROOT/tools/check/check-u10-static.py" >"$TMP/static.out" 2>&1; then
  ok "种植与家园币接线、缺口文案已去、总控已挂 U10"
else
  bad "U10 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器自检 =="
if [ ! -x "$UNITY" ] && [ ! -f "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U10Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U10Check.Run 退出码 0"
else
  bad "Unity U10Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files net_parse flow_plants screen_wired codex_plants no_config no_proto self; do
  if grep -q "u10_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u10_${key}=ok"
  else
    bad "编辑器自检 u10_${key} 没过"
    grep "u10_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 3. 屏上自检 =="
for key in pending empty plant mature harvest buy buy_pending unavailable skin_layout skin_copy; do
  if grep -q "u10_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "种植自检 u10_self_${key}=ok"
  else
    bad "种植自检 u10_self_${key} 没过"
    grep "u10_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
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
