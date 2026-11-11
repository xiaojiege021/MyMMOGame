#!/usr/bin/env bash
# tools/check/check-all.sh —— 核对总控：按依赖闭包回归，或全量回归。
#
# 用途：把散着跑的 30 张 check-<卡号>.sh 串起来，只把「没过的卡 + 失败行」报出来，
#       全过则静默。人工从「盯 26 份日志」降到「只看红字」。
#
# 依赖来源：docs/开发架构.md 第 14 节排期表的「依赖」列。做一张卡时只回归
#           「它的直接依赖 + 本卡」（依赖闭包），全量回归挪到里程碑/每天收工。
#
# 用法：
#   bash tools/check/check-all.sh --card P1-4b     # 回归 P1-4b 的依赖闭包 + 本卡
#   bash tools/check/check-all.sh --card p1-4b     # 大小写不敏感
#   bash tools/check/check-all.sh --full           # 全量（所有已存在的卡）
#   bash tools/check/check-all.sh --list           # 列出已知卡及其依赖
#
# 说明：
#   - 客户端段（Unity 自检）需要 Unity，服务端段需要 gamedb；本总控只负责「调用」
#     和「汇总」，不替单卡决定跑哪段。每张卡仍按它自己的 MMO_*_MODE 约定跑服务端段，
#     客户端段另用 MMO_*_MODE=client 单独触发（见 --card 输出里的提示）。
#   - 不改任何既有核对脚本，不改玩法规则，不碰未派卡。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

# ---------------------------------------------------------------------------
# 卡号 → 直接依赖（来自 docs/开发架构.md 第 14 节「依赖」列）。
# 约定：值用空格分隔；空表示无依赖（环境/根基卡）。
# ---------------------------------------------------------------------------
# 依赖查表：用 case 而非关联数组，兼容 macOS /bin/bash 3.2 与 Windows Git Bash。
deps_of() {
  case "$1" in
    # 环境与根基（无依赖）
    e3|e7|e8|p0-1a) echo "" ;;
    # P0
    p0-1b) echo "p0-1a" ;;
    p0-1c) echo "p0-1b" ;;
    p0-1d) echo "p0-1c" ;;
    p0-2a) echo "p0-1d" ;;
    p0-2b) echo "p0-2a" ;;
    p0-3a) echo "p0-2b" ;;
    p0-3b) echo "p0-3a" ;;
    p0-3c) echo "p0-3b" ;;
    # P1
    p1-1a1) echo "p0-3a" ;;
    p1-1a2) echo "p1-1a1" ;;
    p1-1b1) echo "p1-1a2" ;;
    p1-1b2) echo "p1-1b1" ;;
    p1-1b3) echo "p1-1b2" ;;
    p1-1b4) echo "p1-1b3" ;;
    p1-1b5) echo "p1-1b4" ;;
    p1-2a) echo "p1-1b5" ;;
    p1-2b1) echo "p1-2a" ;;
    p1-2b2) echo "p1-2b1" ;;
    p1-2c) echo "p1-2b2" ;;
    p1-2d) echo "p1-2a" ;;
    p1-3a1) echo "p1-1b5" ;;
    p1-3a2) echo "p1-3a1" ;;
    p1-3a3) echo "p1-3a2" ;;
    p1-3b) echo "p1-3a1 p1-1b3" ;;
    p1-3c) echo "p1-3a2" ;;
    p1-4a) echo "p1-1b5 e4" ;;
    p1-4b) echo "p1-4a" ;;
    p1-4c) echo "p1-4a" ;;
    p1-4d) echo "p1-1b3" ;;
    p1-5a) echo "p1-1b5" ;;
    p1-5b) echo "p1-5a" ;;
    p1-5c1) echo "p1-1b4" ;;
    p1-5c2) echo "p1-5c1 p1-4b" ;;
    p1-5c3) echo "p1-5c1 p0-3a" ;;
    p1-5c4) echo "p1-5c1" ;;
    p1-5c5) echo "p1-5c1 p0-3a" ;;
    p1-5c6) echo "p1-5c1 p0-3a" ;;
    p1-5c7) echo "p1-5c1 p1-2d" ;;
    p1-5c8) echo "p1-5c1 p1-1b3" ;;
    p1-5d) echo "p1-1b4" ;;
    # P2
    p2-1a1) echo "p1-1b1 e2" ;;
    p2-1a2) echo "p2-1a1" ;;
    p2-1a3) echo "p2-1a2" ;;
    p2-1a4) echo "p2-1a1" ;;
    p2-1b) echo "p2-1a1 p1-2a" ;;
    p2-1c) echo "p2-1a1 p1-2d" ;;
    p2-2a) echo "p1-2c" ;;
    p2-3a) echo "p0-3a p1-1b5" ;;
    p2-3b) echo "p2-3a" ;;
    p2-4a) echo "p1-1b1 p1-1b2 p0-3a" ;;
    # P3
    p3-1a1) echo "p1-1b2 p1-2c e4" ;;
    p3-1a2) echo "p3-1a1 p1-1b4" ;;
    p3-2a) echo "p1-3b p1-5a" ;;
    p3-3a1) echo "p2-1b p2-3b" ;;
    p3-3a2) echo "p3-3a1" ;;
    p3-3a3) echo "p3-3a1 p2-1b" ;;
    p3-3a4) echo "p3-3a1 p3-2a" ;;
    p3-4a1) echo "p3-1a1" ;;
    p3-4a2) echo "p3-4a1 p1-4b p1-5c2" ;;
    p3-4b) echo "p1-5c3 p1-5c4" ;;
    p3-5a) echo "p1-3a1 p2-1a1 p0-3a" ;;
    # U（界面线，第 14 节表尾 71–77 行；依赖列见 14.1，不与第 70 行构成先后）
    u1) echo "e6 e7 p0-3c" ;;
    u2) echo "u1 p1-1a1 p1-1b1" ;;
    u3) echo "u1 p0-3c p1-2c p1-2a" ;;
    *) echo "__UNKNOWN__" ;;
  esac
}

# 全部已知卡（用于 --list）。
ALL_CARDS="e3 e7 e8 p0-1a p0-1b p0-1c p0-1d p0-2a p0-2b p0-3a p0-3b p0-3c \
p1-1a1 p1-1a2 p1-1b1 p1-1b2 p1-1b3 p1-1b4 p1-1b5 \
p1-2a p1-2b1 p1-2b2 p1-2c p1-2d \
p1-3a1 p1-3a2 p1-3a3 p1-3b p1-3c \
p1-4a p1-4b p1-4c p1-4d \
p1-5a p1-5b p1-5c1 p1-5c2 p1-5c3 p1-5c4 p1-5c5 p1-5c6 p1-5c7 p1-5c8 p1-5d \
p2-1a1 p2-1a2 p2-1a3 p2-1a4 p2-1b p2-1c p2-2a p2-3a p2-3b p2-4a \
p3-1a1 p3-1a2 p3-2a p3-3a1 p3-3a2 p3-3a3 p3-3a4 p3-4a1 p3-4a2 p3-4b p3-5a \
u1 u2 u3"

# 已存在的核对脚本（只回归真正存在的卡）。
exists_card() {
  local c="$1"
  [ -f "$ROOT/tools/check/check-$c.sh" ]
}

# 递归算依赖闭包（去重、保序、不含本卡）。
closure() {
  # 依赖闭包（去重、保序、不含本卡），只输出已存在的卡。
  local seen="" stack="" c n dup
  for c in $(deps_of "$1"); do [ "$c" != "__UNKNOWN__" ] && stack="$stack $c"; done
  stack="${stack# }"
  while [ -n "$stack" ]; do
    n="${stack%% *}"
    stack="${stack#* }"
    [ "$n" = "$stack" ] && stack=""
    # 去重
    dup=0
    for s in $seen; do [ "$s" = "$n" ] && dup=1; done
    [ "$dup" = "1" ] && continue
    seen="$seen $n"
    seen="${seen# }"
    for c in $(deps_of "$n"); do [ "$c" != "__UNKNOWN__" ] && stack="$stack $c"; done
  done
  for s in $seen; do
    exists_card "$s" && echo "$s"
  done
}

# 这些卡默认就要开 Unity / 连平板（第 14 节 E、P0 段），笔记本按第 16 节
# 只同步服务端源码，没有 client/MMOGame。缺工程时明确跳过，别把「环境不具备」
# 报成回归失败，也别假装跑过；它们在本机 Mac 上单独跑。
client_card_needs_unity() {
  case "$1" in
    e7|e8|p0-1a|p0-1b|p0-1c|p0-1d|p0-2a|p0-2b|p0-3c|u1|u2|u3) return 0 ;;
    *) return 1 ;;
  esac
}

run_one() {
  local card="$1" out="$2"
  if ! exists_card "$card"; then
    echo "  (跳过：check-$card.sh 尚未建)"
    return 2
  fi
  # E3 要装 /etc/systemd/system 单元，非 root 跑必然失败；手动 sudo 补跑。
  if [ "$card" = "e3" ] && [ "$(id -u)" -ne 0 ]; then
    echo "  (跳过：e3 要 root 装 systemd 单元；手动 sudo bash tools/check/check-e3.sh)"
    return 2
  fi
  if [ ! -d "$ROOT/client/MMOGame" ] && client_card_needs_unity "$card"; then
    echo "  (跳过：$card 要 Unity 工程 / 平板，本机没有 client/MMOGame；在 Mac 上跑)"
    return 2
  fi
  bash "$ROOT/tools/check/check-$card.sh" >"$out" 2>&1
}

summarize() {
  local card="$1" code="$2" out="$3"
  if [ "$code" -eq 0 ]; then
    echo "  ok   $card"
  else
    echo "  FAIL $card  (exit=$code)"
    grep -E '^  FAIL|核对：' "$out" | sed 's/^/       /' | head -20
  fi
}

# ---------------------------------------------------------------------------
MODE=""
CARD=""

case "${1:-}" in
  --list)
    echo "卡号           依赖"
    for c in $ALL_CARDS; do
      d="$(deps_of "$c")"
      [ "$d" = "__UNKNOWN__" ] && d=""
      printf '%-14s %s\n' "$c" "${d:-(无)}"
    done
    exit 0
    ;;
  --card)
    CARD="$(echo "${2:-}" | tr '[:upper:]' '[:lower:]')"
    MODE="card"
    ;;
  --full)
    MODE="full"
    ;;
  ""|-h|--help)
    echo "用法："
    echo "  bash tools/check/check-all.sh --card <卡号>   回归该卡的依赖闭包 + 本卡"
    echo "  bash tools/check/check-all.sh --full          全量回归（所有已存在的卡）"
    echo "  bash tools/check/check-all.sh --list          列出卡号与依赖"
    exit 0
    ;;
  *)
    echo "未知参数：$*" >&2
    exit 2
    ;;
esac

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "== 核对总控（${MODE}）=="

if [ "$MODE" = "card" ]; then
  if [ "$(deps_of "$CARD")" = "__UNKNOWN__" ]; then
    echo "未知卡号：$CARD（用 --list 看已知卡）" >&2
    exit 2
  fi
  echo "目标卡：$CARD"
  echo "依赖闭包（本卡之前）：$(closure "$CARD" | tr '\n' ' ')"
  # 客户端段提示
  if [ -f "$ROOT/tools/check/check-$CARD.sh" ]; then
    # 这里是顶层脚本，不能用 local（Linux bash 会直接报错）。
    mvar="$(grep -m1 -o 'MMO_[A-Z0-9_]*_MODE' "$ROOT/tools/check/check-$CARD.sh" || true)"
    if [ -n "$mvar" ]; then
      echo "提示：客户端段可单独跑  ${mvar}=client bash tools/check/check-$CARD.sh"
    fi
  fi
  echo "----------------------------------------"
  fail_any=0
  for c in $(closure "$CARD"); do
    out="$TMP/$c.log"
    run_one "$c" "$out"; code=$?
    [ "$code" -eq 2 ] && continue
    summarize "$c" "$code" "$out"
    [ "$code" -ne 0 ] && fail_any=1
  done
  # 本卡。2 表示当前环境跑不了（缺 Unity 工程 / 没 root），
  # 这时没有日志文件，不能 summarize，也不算回归失败。
  out="$TMP/$CARD.log"
  run_one "$CARD" "$out"; code=$?
  if [ "$code" -ne 2 ]; then
    summarize "$CARD" "$code" "$out"
    [ "$code" -ne 0 ] && fail_any=1
  fi
  echo "----------------------------------------"
  if [ "$fail_any" -eq 0 ]; then
    echo "依赖闭包回归：全部通过"
  else
    echo "依赖闭包回归：有卡未通过（红字见上）"
    exit 1
  fi
else
  # 全量：所有已存在的 check-<卡号>.sh
  fail_any=0
  cnt=0
  while IFS= read -r f; do
    card="$(basename "$f" .sh | sed 's/^check-//')"
    out="$TMP/$card.log"
    run_one "$card" "$out"; code=$?
    # 与 --card 一致：2 表示「本机跑不了 / 卡还没建」，不是回归失败。
    [ "$code" -eq 2 ] && continue
    summarize "$card" "$code" "$out"
    [ "$code" -ne 0 ] && fail_any=1
    cnt=$((cnt + 1))
  done < <(ls "$ROOT"/tools/check/check-*.sh | sort)
  echo "----------------------------------------"
  echo "全量：共 $cnt 张卡"
  if [ "$fail_any" -eq 0 ]; then
    echo "全量回归：全部通过"
  else
    echo "全量回归：有卡未通过（红字见上）"
    exit 1
  fi
fi
