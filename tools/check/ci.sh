#!/usr/bin/env bash
# tools/check/ci.sh —— D4 最小 CI：编译 + 无库段核对，每次提交 / PR 自动跑。
#
# 完成标准（开发架构 14.6 的 D4 行）：build + 静态断言 + 无库段 check 每次提交自动跑，失败即红。
#
# 这里只跑「不需要角色库 / 网关 / Unity / 平板」的卡：它们自带静态断言，
# 且在没有 gamedb 的机器上也必须全绿。需要真库或真机的卡（p1-4b、p1-5a 等）
# 不在这里跑，留给笔记本与 Mac 的收尾回归。
#
#   bash tools/check/ci.sh            # CI 与本地都跑这一条
#   bash tools/check/ci.sh --list     # 只列出本 CI 覆盖的卡
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

# 无库段可跑的卡（新增卡只有在「无 gamedb 也能全绿」时才往这里加）。
# contract = D16 契约闸，脚本按 14.11.3 命名为 check-contract.sh。
CHECKS="c2-realm c5 c6 c7 contract d1a d1b d5 d8 d15 invariants k1 k3 k4 k5 k6 k7 n6 n7 n13 n14 p1-5c1 p1-5c2 p1-5c6 p1-5c7 p1-5c8 p1-5d p2-1b p2-1c p2-2a q1 q2 q3 q4 s1"

if [ "${1:-}" = "--list" ]; then
  printf '%s\n' $CHECKS
  exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

FAIL=0
step() { printf '\n== %s ==\n' "$1"; }
# GitHub Actions 里把失败卡与第一行断言写成 annotation：日志下载要仓库权限，
# annotation 是公开的，红的时候能直接看到是哪张卡、哪条断言。
gh_error() {
  [ -n "${GITHUB_ACTIONS:-}" ] || return 0
  printf '::error::%s\n' "$(printf '%s' "$1" | tr '\n' ' ' | cut -c1-600)"
}

step "1. 编译（服务端 sln，Release）"
# SDK 版本要钉住：GitHub runner 预装了更新的 SDK 预览版（11.0.100），`dotnet build`
# 默认挑最新那个，于是 MSBuild 的 task host 起不来，报 MSB4216（不是代码错）。
# 仓库根的 global.json 与两个 workflow 都钉 10.0.403，这里再挡一道并说清原因。
SDK="$("$DOTNET" --version 2>/dev/null)"
echo "       sdk ${SDK}"
if [ "${SDK#10.0.}" = "$SDK" ]; then
  echo "  FAIL SDK 不是 10.0.x（现在是 ${SDK:-空}）：global.json 与 ci.yml 都钉了 10.0.403"
  gh_error "CI SDK mismatch: got ${SDK:-empty}, want 10.0.403"
  exit 1
fi
if "$DOTNET" build src/MMOGame.sln -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  echo "  ok   编译通过"
else
  echo "  FAIL 编译失败"
  # 带错误码的那一行（error CS0103 / error NU1102）比最后那句 "N Error(s)" 有用得多：
  # annotation 只留前几条，日志里留前 30 条。
  ERRS="$(grep -E '(error|错误) [A-Z]{2,4}[0-9]{3,4}' "$TMP/build.log" | head -30)"
  [ -n "$ERRS" ] || ERRS="$(tail -30 "$TMP/build.log")"
  printf '%s\n' "$ERRS" | sed 's/^/       /'
  gh_error "CI build failed: $(printf '%s' "$ERRS" | head -4 | tr '\n' ' | ' | cut -c1-900)"
  exit 1
fi

step "2. 无库段核对（$(printf '%s\n' $CHECKS | wc -l | tr -d ' ') 张卡）"
for c in $CHECKS; do
  log="$TMP/$c.log"
  if bash tools/check/check-$c.sh >"$log" 2>&1; then
    line="$(grep -E '核对：' "$log" | tail -1)"
    echo "  ok   ${line:-$c}"
  else
    echo "  FAIL $c"
    grep -E '^  FAIL' "$log" | head -6 | sed 's/^/       /'
    gh_error "CI $c failed: $(grep -E '^  FAIL' "$log" | head -3 | tr '\n' ' ')"
    FAIL=$((FAIL + 1))
  fi
done

printf '\nCI 结果：%s 张卡，%s 张未过\n' "$(printf '%s\n' $CHECKS | wc -l | tr -d ' ')" "$FAIL"
if [ "$FAIL" -ne 0 ]; then
  echo "红：见上面 FAIL 行；需要真库 / 真机的段不在这里跑。"
  exit 1
fi
echo "全绿。"
