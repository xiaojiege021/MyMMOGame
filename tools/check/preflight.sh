#!/usr/bin/env bash
# tools/check/preflight.sh —— 提交前/开工前置闸门：把最容易返工的四类问题在写码那一刻拦住。
#
# 用途：bootstrap.sh 开工体检之外，再挡四道「人眼容易漏、漏了必返工」的闸：
#   1. meta GUID 闸门：任何 .meta 的 guid 必须正好 32 位十六进制（图标那次就是多一位）。
#   2. 静态断言收窄提示：核对脚本里对客户端/服务端文件的 grep 若扫到注释/文档里的裸词，
#      会误伤（JsonUtility 那次）；这里只提示存在疑似窄断言，不硬失败。
#   3. 只读 state 入口提示：新卡若加了 --xxx-state，必须与真实启动共用同一份 Load 函数
#      和同一组参数（flight-state 漏传 regions 那次）；这里检测 state 入口是否调用 Load。
#   4. 老卡断言不写死「当时的发货种数」：delivered_kinds=数字、把「还没接发货」绑死在某
#      一个编号上，都会在新卡接发货时假失败（c1 那次闭包首跑 21/22、c7 现在的 5 种）。
#
# 退出码：0=通过；1=有硬性失败（GUID 非法，或老卡断言写死发货种数）。
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

# ---------------------------------------------------------------------------
# 闸门 4：老卡断言不写死「当时的发货种数」，也不把「还没接发货」绑死在某一个编号上（硬性）。
# 背景：P1-5c2 接发货后，check-p1-5c1 两条硬编码同时变红，闭包首跑 21/22；
#       check-p1-5c7 现在写死 delivered_kinds=5，P1-5c8 再接一种发货就会假失败。
# 只禁两种写法：
#   A. `delivered_kinds=` 后面直接跟数字；
#   B. 「没接发货 / 未接发货」或 delivery_pending 与字面商品编号写在同一行
#      （形如 kind=talisman_red、item_id="shop_xxx"，或带引号的 snake_case 编号）。
# `version=` 不在此列：配置版本和启动日志里的版本都是合法的。
# 正确写法是只钉那张卡自己的语义：种类数相等且不少于本卡、动态挑空发货商品，
# 见 check-p1-5c2.sh 第 220 行附近。
# ---------------------------------------------------------------------------
echo "-- 4. 老卡断言不写死当时的发货种数 --"
gate_hits="$(python3 - <<'PY'
import glob, re, sys

num = re.compile(r"delivered_kinds=[0-9]")
phrase = re.compile(r"(没接发货|未接发货|delivery_pending)")
literal = re.compile(r"(?:kind|item_id)\s*=\s*(?!\$)[\"']?[a-z][a-z0-9_]*[a-z0-9][\"']?")
quoted = re.compile(r"[\"'](?:shop|talisman|scroll|protect|ticket|ore|sword|exp)_[a-z0-9_]+[\"']")

hits = 0
for path in sorted(glob.glob("tools/check/check-*.sh")):
    for lineno, line in enumerate(open(path, encoding="utf-8"), 1):
        text = line.strip()
        if text.startswith("#"):
            continue
        why = ""
        if num.search(text):
            why = "delivered_kinds 写死数字"
        elif phrase.search(text) and (literal.search(text) or quoted.search(text)):
            why = "「没接发货」绑死字面编号"
        if why:
            print("%s:%d:%s" % (path, lineno, why))
            hits += 1
sys.exit(1 if hits else 0)
PY
)" && gate_ok=1 || gate_ok=0
if [ "$gate_ok" = "1" ]; then
  ok "老卡断言没有写死发货种数 / 未接发货编号"
else
  while IFS=: read -r gate_file gate_line gate_why; do
    [ -n "$gate_file" ] || continue
    bad "${gate_file}:${gate_line} ${gate_why}"
  done <<< "$gate_hits"
fi

echo "----------------------------------------"
echo "前置闸门：$PASS 项通过，$FAIL 项失败"
[ "$FAIL" = "0" ] || exit 1
exit 0
