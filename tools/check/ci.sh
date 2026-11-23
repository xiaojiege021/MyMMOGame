#!/usr/bin/env bash
# tools/check/ci.sh —— D4 最小 CI：编译 + 无库段核对，每次提交 / PR 自动跑。
#
# 完成标准（开发架构 14.6 的 D4 行）：build + 静态断言 + 无库段 check 每次提交自动跑，失败即红。
#
# 这里只跑「不需要角色库 / 网关 / Unity / 平板」的卡：它们自带静态断言，
# 且在没有 gamedb 的机器上也必须全绿。需要真库或真机的卡（p1-4b、p1-5a 等）
# 不在这里跑，留给笔记本与 Mac 的收尾回归。
#
#   bash tools/check/ci.sh            # CI 与本地都跑这一条（全量，约 13 分钟）
#   bash tools/check/ci.sh --changed  # 日常：只跑改动涉及到的卡（约 1/5 时间）
#   bash tools/check/ci.sh --list     # 只列出本 CI 覆盖的卡
#
# 卡是串行的：47 张卡里有十几张要起进程，端口是固定的（61000 / 62000 / 60001），
# 并行会互相抢端口，所以省时间只能靠「少跑几张」，不能靠「同时跑」。
# 改 proto / 迁移 / 网关入口 / lib-common.sh 时一律用全量，不要用 --changed。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

# 无库段可跑的卡（新增卡只有在「无 gamedb 也能全绿」时才往这里加）。
# contract = D16 契约闸，脚本按 14.11.3 命名为 check-contract.sh。
CHECKS="b1 b9 b10 c2-realm c4 c5 c6 c7 contract d1a d1b d5 d8 d15 invariants k1 k3 k4 k5 k6 k7 n6 n7 n13 n14 p1-5c1 p1-5c2 p1-5c6 p1-5c7 p1-5c8 p1-5d p2-1b p2-1c p2-2a q1 q2 q3 q4 q5 q6 q7 q8 q9 q10 q11 q12 s1"

MODE="full"
case "${1:-}" in
  "") ;;
  --list) printf '%s\n' $CHECKS; exit 0 ;;
  --changed) MODE="changed" ;;
  *) echo "unknown argument: $1" >&2; exit 2 ;;
esac

# ── --changed：按改动文件挑卡 ──────────────────────────────────────
# 判据只有两条，宁可退回全量也不猜：
#   1. 改动的是 check-<卡>.sh —— 那一张卡必跑（改了自己的核对脚本）
#   2. 卡脚本里实际读取过的文件名（read("src/.../X.cs") 这种）命中改动文件
# 不做「同目录传播」：src/Character/ 一条就命中 37/47，等于全量，白算。
# 一张都没命中时退回全量，避免「改了没人读的文件」被静默跳过。
# 这两张是全局闸：改了任何业务代码都该复检。
ALWAYS="contract invariants"

affected_cards() {
  local files c f base srcs hit out forced others
  if [ -n "${CI_CHANGED_FILES:-}" ]; then
    # 测试钩子：CI 里也可以直接传文件列表，配合 --changed 用
    files="$(printf '%s\n' $CI_CHANGED_FILES | sed '/^$/d' | sort -u)"
  else
    files="$(
      {
        git diff --name-only HEAD 2>/dev/null
        git diff --name-only --cached 2>/dev/null
        git ls-files --others --exclude-standard 2>/dev/null
      } | sort -u
    )"
  fi
  # 没有可比改动：跑全量最稳
  [ -n "$files" ] || { printf '%s\n' $CHECKS; return; }
  # 这几类一改，所有卡的语义都可能变：直接全量
  if printf '%s\n' "$files" | grep -qE \
    '^(proto/|db/|config/|\.github/|\.gitea/|tools/probe/|global\.json)'; then
    printf '%s\n' $CHECKS
    return
  fi
  # tools/check 下除了「某张卡自己的脚本」之外的东西（ci.sh / lib-common.sh / 公共 py 等）：全量
  if printf '%s\n' "$files" | grep -qE '^tools/check/'; then
    others="$(printf '%s\n' "$files" | grep -E '^tools/check/' \
      | grep -vE '^tools/check/check-[a-z0-9.-]+\.sh$')"
    if [ -n "$others" ]; then
      printf '%s\n' $CHECKS
      return
    fi
  fi
  # 纯文档改动（docs/、根目录 *.md、.workbuddy/）：没有卡会因此变，跑 0 张
  if ! printf '%s\n' "$files" | grep -qvE '^(docs/|\.workbuddy/|\.cursor/|[A-Za-z0-9_.-]+\.md$)'; then
    return
  fi

  # 改了哪几张卡自己的脚本
  forced="$(printf '%s\n' "$files" | sed -n 's#^tools/check/check-\(.*\)\.sh$#\1#p' | sort -u)"

  out=""
  for c in $CHECKS; do
    hit=0
    case " $ALWAYS " in *" $c "*) hit=1 ;; esac
    case " $(printf '%s' "$forced" | tr '\n' ' ') " in *" $c "*) hit=1 ;; esac
    srcs="tools/check/check-$c.sh"
    [ -f "tools/check/check-$c-static.py" ] && srcs="$srcs tools/check/check-$c-static.py"
    if [ "$hit" = "0" ]; then
      while IFS= read -r f; do
        [ -n "$f" ] || continue
        base="${f##*/}"
        if grep -qF -- "$base" $srcs 2>/dev/null; then hit=1; break; fi
      done <<EOF
$files
EOF
    fi
    [ "$hit" = "1" ] && out="${out}${c}
"
  done
  # 一张都没命中：说明这批改动没有任何卡读过，退回全量而不是跑 0 张
  [ -n "$out" ] || { printf '%s\n' $CHECKS; return; }
  printf '%s' "$out"
}

if [ "$MODE" = "changed" ]; then
  CHECKS="$(affected_cards)"
fi
set -- $CHECKS
NCARDS=$#

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

if [ "$MODE" = "changed" ]; then
  echo "       模式 --changed：改动命中 ${NCARDS} 张卡（判不准会退回全量）"
fi
step "2. 无库段核对（${NCARDS} 张卡）"
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

# 体量报告：只打印，不参与失败判定（脚本内部恒退出 0）。拆分时机看 14.8 的排期，
# 这里只让「债在涨」每次都看得见。
step "3. 体量报告（只告警，不判红）"
bash tools/check/size-report.sh | sed 's/^/  /' || true

# 结构预算闸（开发架构 10.1 / 10.3）：比基线差就红。基线是「现状快照」不是目标值，
# 并域或瘦文档之后手工把 tools/check/structure-baseline.txt 调低，成果才落账。
step "4. 结构预算闸（比基线差即红）"
if bash tools/check/check-structure-budget.sh >"$TMP/budget.log" 2>&1; then
  grep -E '核对：' "$TMP/budget.log" | sed 's/^/  /'
else
  echo "  FAIL 结构预算超基线"
  grep -E '^  (FAIL|warn)' "$TMP/budget.log" | head -12 | sed 's/^/       /'
  gh_error "CI 结构预算超基线：$(grep -E '^  FAIL' "$TMP/budget.log" | head -3 | tr '\n' ' ')"
  FAIL=$((FAIL + 1))
fi

printf '\nCI 结果：%s 张卡 + 1 项结构预算，%s 项未过\n' "$NCARDS" "$FAIL"
if [ "$FAIL" -ne 0 ]; then
  echo "红：见上面 FAIL 行；需要真库 / 真机的段不在这里跑。"
  exit 1
fi
echo "全绿。"
