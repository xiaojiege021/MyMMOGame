#!/usr/bin/env bash
# tools/check/check-structure-budget.sh —— 结构预算闸（开发架构 10.1 / 10.3）。
#
# 这张脚本不校验玩法，只回答一个问题：工程产物有没有膨胀到该并域了。
#
# 为什么是「基线比对」而不是绝对阈值：
#   v1.201 现状是 check（sh+py）约 4.9 万行 / 手写业务 60,335 行 ≈ 81%，11 种 helper 在 30 个
#   脚本里重定义。这些都是存量债，历史不许改写（日志 append-only、老脚本不重写），
#   所以绝对阈值一旦判红，CI 就永远红 —— 永远红的闸门等于没有闸门。
#   改成基线锁：存量记在 tools/check/structure-baseline.txt，只惩罚「比基线更差」。
#   并域或瘦文档之后手工把基线调低，成果才会落到账上。
#
# 判据：
#   1. 绝对红线，与基线无关：脚本数 >150 / 单脚本 >1000 行 → 直接红
#   2. 基线比对：任一项比基线差 → 红；变好 → 通过并提示下调基线
#   3. 目标值（10.1）：只 warn，不改退出码
#
# 用法：
#   bash tools/check/check-structure-budget.sh
#   bash tools/check/check-structure-budget.sh --commit <sha>   额外算一张卡的产物比（单卡判红）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

# 10.1 目标值（warn 线）与绝对天花板（bad 线，只防失控，正常不会碰）
TARGET_SCRIPTS=60
HARD_SCRIPTS=150
TARGET_SCRIPT_LINES=350
HARD_SCRIPT_LINES=1000
TARGET_RATIO=40
TARGET_LOG_LINES=50
BASE_FILE="tools/check/structure-baseline.txt"

COMMIT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --commit) COMMIT="${2:-}"; shift 2 ;;
    *) bad "未知参数 $1"; finish "结构预算" ;;
  esac
done

warn() { printf '  warn %s\n' "$1"; }

base_of() {
  [ -f "$BASE_FILE" ] || { printf ''; return; }
  sed -n "s/^$1=//p" "$BASE_FILE" | head -1
}
base_num() { base_of "$1" | tr -d ' '; }

# judge <项名> <当前值> <基线值> <方向> <目标值>
# 方向 min = 越小越好（默认）；max = 越大越好
judge() {
  local name="$1" cur="$2" base="$3" dir="${4:-min}" target="$5"
  if [ -z "$base" ]; then
    bad "${name} 在基线里没有记录（${BASE_FILE}），先补上再跑"
    return
  fi
  local worse=0 better=0
  if [ "$cur" -eq "$base" ]; then
    ok "${name} ${cur}，与基线持平"
  elif [ "$dir" = "min" ]; then
    [ "$cur" -gt "$base" ] && worse=1 || better=1
  else
    [ "$cur" -lt "$base" ] && worse=1 || better=1
  fi
  if [ "$worse" = "1" ]; then
    bad "${name} ${cur}，比基线 ${base} 变差（不许带着恶化往下走）"
  elif [ "$better" = "1" ]; then
    ok "${name} ${cur}，比基线 ${base} 变好"
    warn "${name} 已降到 ${cur}：把 ${BASE_FILE} 里这一项调成 ${cur}，成果要落账"
  fi
  if [ -n "$target" ] && [ "$dir" = "min" ] && [ "$cur" -gt "$target" ]; then
    warn "${name} ${cur}，仍高于目标 ${target}（10.1）"
  fi
}

echo "结构预算核对（开发架构 10.1：目标值 + 基线锁）"

# ── 1. check 脚本数 ────────────────────────────────────────────────
script_count=$(find tools/check -name 'check-*.sh' -type f | wc -l | tr -d ' ')
if [ "$script_count" -gt "$HARD_SCRIPTS" ]; then
  bad "check 脚本 ${script_count} 个，超绝对红线 ${HARD_SCRIPTS}"
else
  ok "check 脚本 ${script_count} 个，未超绝对红线 ${HARD_SCRIPTS}"
fi
judge "check 脚本数" "$script_count" "$(base_num scripts)" min "$TARGET_SCRIPTS"

# ── 2. 单脚本最大行数 ──────────────────────────────────────────────
max_lines=0
max_file=""
hard_over=""
for f in tools/check/check-*.sh; do
  n=$(wc -l < "$f" | tr -d ' ')
  if [ "$n" -gt "$max_lines" ]; then max_lines="$n"; max_file="$f"; fi
  if [ "$n" -gt "$HARD_SCRIPT_LINES" ]; then hard_over="${hard_over}    ${n} 行  ${f}
"; fi
done
if [ -n "$hard_over" ]; then
  bad "有脚本超绝对红线 ${HARD_SCRIPT_LINES} 行"
  printf '%s' "$hard_over" | detail
else
  ok "没有脚本超绝对红线 ${HARD_SCRIPT_LINES} 行（最长 ${max_lines} 行：${max_file}）"
fi
judge "最长脚本行数" "$max_lines" "$(base_num max_script_lines)" min "$TARGET_SCRIPT_LINES"

# ── 3. check 行数 ÷ 手写业务代码 ───────────────────────────────────
check_lines=$( { cat tools/check/*.sh; cat tools/check/*.py; } 2>/dev/null | wc -l | tr -d ' ')
# 不走 xargs：Git Bash 下 `getconf ARG_MAX` 为 undefined，xargs 会断言失败并输出 0 行，
# 这台机就会把「数到了 0 行」当成「数不到」，闸门变成假绿 / 假红。
biz_lines=$(find src -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' \
  -not -path '*/Generated/*' -not -path '*.Tests/*' 2>/dev/null \
  | while IFS= read -r f; do cat "$f"; done | wc -l | tr -d ' ')
if [ "${biz_lines:-0}" -gt 0 ]; then
  ratio=$((check_lines * 100 / biz_lines))
  printf '       参考：check %s 行 / 手写业务 %s 行（这两个数只作分母与账目，不单独判红）\n' \
    "$check_lines" "$biz_lines"
  judge "check 占比（%）" "$ratio" "$(base_num ratio)" min "$TARGET_RATIO"
else
  bad "数不到业务代码行数（src/**/*.cs，排除 Generated / Tests）"
fi

# ── 4. 最新一条开发日志的行数（历史条目豁免，新条目按目标判）──────
# 日志是 append-only，存量不改写；但也不能拿「上一张的 15 行」去卡下一张的 30 行
# （卡日志本来就比非卡片记录长）。所以只豁免基线里记下的那一条，其余按目标值判。
log_file="docs/开发日志.md"
if [ -f "$log_file" ]; then
  latest_name=$(grep -m1 '^### ' "$log_file" | sed 's/^### //')
  latest_body=$(awk '/^### /{n++} n==1 && !/^### /' "$log_file" | wc -l | tr -d ' ')
  exempt_card=$(base_of latest_log_card)
  if [ -z "$latest_name" ]; then
    bad "开发日志里找不到「### 卡号 · 版本 · 日期」条目"
  elif [ -n "$exempt_card" ] && [ "$latest_name" = "$exempt_card" ]; then
    ok "最新一条日志「${latest_name}」${latest_body} 行（基线豁免的历史条目，不改写）"
  elif [ "$latest_body" -gt "$TARGET_LOG_LINES" ]; then
    bad "新条目「${latest_name}」${latest_body} 行，超目标 ${TARGET_LOG_LINES}（10.1）：只留改了什么 / 怎么验 / 契约变化"
  else
    ok "最新一条日志 ${latest_body} 行，不超过目标 ${TARGET_LOG_LINES}"
  fi
else
  warn "找不到 ${log_file}，跳过日志行数"
fi

# ── 5. 不许复制粘贴第二套 helper ───────────────────────────────────
dup=0
dup_list=""
for fn in ok bad detail stop_pid body_of code_of json_field expect_json wait_ready wait_line finish; do
  hits=$(grep -l "^${fn}()" tools/check/check-*.sh 2>/dev/null | wc -l | tr -d ' ')
  if [ "$hits" -gt 0 ]; then
    dup=$((dup + hits))
    dup_list="${dup_list}    ${fn}() 在 ${hits} 个卡脚本里重定义，应改用 lib-common.sh
"
  fi
done
judge "重定义的 helper 份数" "$dup" "$(base_num dup_sites)" min ""
if [ "$dup" -gt 0 ]; then
  printf '%s' "$dup_list" | detail
fi

# ── 6. 单卡产物比（可选，对 --commit 判红）────────────────────────
if [ -n "$COMMIT" ]; then
  git rev-parse --verify "${COMMIT}^{commit}" >/dev/null 2>&1 || { bad "提交 ${COMMIT} 不存在"; finish "结构预算"; }
  stat=$(git show --numstat --pretty=format: "$COMMIT")
  fixed=$(printf '%s\n' "$stat" | awk 'NF==3 && $3 ~ /tools\/(check|probe)\// {s+=$1} END{print s+0}')
  biz=$(printf '%s\n' "$stat" | awk 'NF==3 && $3 ~ /^(src|db|proto|config)\// && $3 !~ /Generated/ {s+=$1} END{print s+0}')
  if [ "$biz" -gt 0 ]; then
    r=$((fixed * 100 / biz))
    if [ "$r" -gt 100 ]; then
      bad "提交 ${COMMIT}：固定产物 ${fixed} 行 / 业务 ${biz} 行 = ${r}%，属「业务代码 < 固定产物」（10.3 第 2 问），同类卡必须合"
    else
      ok "提交 ${COMMIT}：固定产物 ${fixed} 行 / 业务 ${biz} 行 = ${r}%"
    fi
  else
    warn "提交 ${COMMIT} 数不到业务代码行数，跳过产物比"
  fi
fi

finish "结构预算"
