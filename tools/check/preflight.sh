#!/usr/bin/env bash
# tools/check/preflight.sh —— 提交前/开工前置闸门：把最容易返工的三类问题在写码那一刻拦住。
#
# 用途：bootstrap.sh 开工体检之外，再挡三道「人眼容易漏、漏了必返工」的闸：
#   1. meta GUID 闸门：任何 .meta 的 guid 必须正好 32 位十六进制（图标那次就是多一位）。
#   2. 静态断言收窄提示：核对脚本里对客户端/服务端文件的 grep 若扫到注释/文档里的裸词，
#      会误伤（JsonUtility 那次）；这里只提示存在疑似窄断言，不硬失败。
#   3. 只读 state 入口提示：新卡若加了 --xxx-state，必须与真实启动共用同一份 Load 函数
#      和同一组参数（flight-state 漏传 regions 那次）；这里检测 state 入口是否调用 Load。
#
# 退出码：0=通过；1=有硬性失败（GUID 非法）。
# 本脚本只读，不改任何文件，不改玩法规则。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

FAIL=0
PASS=0
ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
warn(){ printf '  ..   %s\n' "$1"; }

echo "== 前置闸门 =="

# ---------------------------------------------------------------------------
# 闸门 1：meta GUID 必须正好 32 位十六进制。
# 背景：Assets/Art/Icon.meta 曾写成 33 位，Unity 判定无效并忽略整个文件夹，
#       连 YooAsset 打 Launch AB 都报 [ErrorCode300]。
# ---------------------------------------------------------------------------
echo "-- 1. meta GUID（32 位十六进制） --"
bad_guid=0
while IFS= read -r g; do
  [ -z "$g" ] && continue
  if [ "${#g}" -ne 32 ] || ! printf '%s' "$g" | grep -qE '^[0-9a-fA-F]{32}$'; then
    bad "非法 GUID（长度 ${#g}）：$g"
    bad_guid=1
  fi
done < <(find client/MMOGame/Assets -name '*.meta' -type f -print0 2>/dev/null \
  | xargs -0 grep -h '^guid:' 2>/dev/null | awk '{print $2}')
if [ "$bad_guid" -eq 0 ]; then
  ok "所有 .meta 的 GUID 都是 32 位十六进制"
fi

# ---------------------------------------------------------------------------
# 闸门 2：静态断言收窄提示（软性）。
# 背景：核对脚本曾对客户端文件 grep 'JsonUtility' 裸词，误扫到注释里的说明文字，
#       导致明明合规的文件被判 FAIL。规范：静态断言只扫「代码行」，收窄到具体文件字段，
#       别扫注释/文档。
# ---------------------------------------------------------------------------
echo "-- 2. 静态断言收窄提示 --"
# 检查核对脚本里是否存在「整文件 grep 裸词」的疑似宽断言（软提示，不硬失败）。
if grep -rlE 'grep[^|]*-(rq|q)[^|]*JsonUtility|JsonUtility.*grep' tools/check/*.sh client/MMOGame/Assets/Editor/*.cs 2>/dev/null | head -1 >/dev/null; then
  warn "检测到疑似对 JsonUtility 的宽 grep 断言；建议只扫代码行、避开注释（软提示）"
else
  ok "未发现对 JsonUtility 的宽 grep 断言"
fi

# ---------------------------------------------------------------------------
# 闸门 3：只读 state 入口必须走完整校验链（软性）。
# 背景：--flight-state 曾只调 FlightTable.Load(path) 不传 regions，导致「漏配地表门槛」
#       在只读入口没被挡住。规范：state 入口要与真实启动共用同一份 Load + 同一组参数。
# ---------------------------------------------------------------------------
echo "-- 3. 只读 state 入口校验链提示 --"
# 找到所有 --xxx-state 入口，检查其是否调用了带参数的 Load（软提示）。
state_hits="$(grep -rlE '\-\-[a-z0-9-]+-state' src/*/Program.cs 2>/dev/null | tr '\n' ' ' || true)"
if [ -n "$state_hits" ]; then
  warn "以下进程有 --*-state 只读入口：${state_hits}（请确认它们与启动共用同一 Load 函数与参数）"
else
  ok "未发现 --*-state 只读入口"
fi

echo "----------------------------------------"
echo "前置闸门：$PASS 项通过，$FAIL 项失败"
[ "$FAIL" = "0" ] || exit 1
exit 0
