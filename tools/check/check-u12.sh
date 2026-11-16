#!/usr/bin/env bash
# tools/check/check-u12.sh —— U12 竞技场与战场进场屏（开发架构第 14 节第 95 行）。
#
# 完成标准：F03 接 POST /versus/arena/enter 与 POST /versus/battlefield/enter；
# 展示回包里的进场路径与模式事实，不本地算战力、不造匹配算法。
#
#   bash tools/check/check-u12.sh
#       静态断言 + Unity 编辑器自检（进场 / 请求体 / 人数 / ok=false / 非 200 / 战场 / 切皮肤）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
TITLE="U12（client）"
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
if ROOT="$ROOT" "$PYBIN" "$ROOT/tools/check/check-u12-static.py" >"$TMP/static.out" 2>&1; then
  ok "两条进场口接线、缺口文案已去、总控已挂 U12"
else
  bad "U12 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器自检 =="
if [ ! -x "$UNITY" ] && [ ! -f "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U12Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U12Check.Run 退出码 0"
else
  bad "Unity U12Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files net_parse flow_versus screen_wired codex_wired no_power_math no_config no_proto self; do
  if grep -q "u12_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u12_${key}=ok"
  else
    bad "编辑器自检 u12_${key} 没过"
    grep "u12_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 3. 屏上自检 =="
for key in pending arena body_only no_local_multiply reason reject battlefield skin_layout skin_copy; do
  if grep -q "u12_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "进场自检 u12_self_${key}=ok"
  else
    bad "进场自检 u12_self_${key} 没过"
    grep "u12_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 4. 屏就绪 =="
if grep -q "u7_arena_screen=ready" "$TMP/unity.log" 2>/dev/null; then
  ok "屏就绪 u7_arena_screen=ready"
else
  bad "竞技场屏没念就绪行"
fi
if grep -q "u12_versus_wired=ready" "$TMP/unity.log" 2>/dev/null; then
  ok "接线行 u12_versus_wired=ready"
else
  bad "没有接线就绪行"
fi

echo "== 5. 不做项 =="
if git -C "$ROOT" rev-parse --git-dir >/dev/null 2>&1; then
  if git -C "$ROOT" diff --quiet -- config proto 2>/dev/null; then
    ok "config/ 与 proto/ 一行未改"
  else
    bad "这张卡改了 config/ 或 proto/"
    git -C "$ROOT" diff --name-only -- config proto | detail
  fi
else
  echo "  skip 不是 git 仓库，跳过改动比对"
fi

finish "$TITLE"
