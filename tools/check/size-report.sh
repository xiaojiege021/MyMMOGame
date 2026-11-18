#!/usr/bin/env bash
# tools/check/size-report.sh —— 体量报告：只告警，不判红。
#
# 为什么有它：14.8 的拆分清单是 2026-11-16 的快照，之后每张卡都在往同一批
# 文件里加代码（CharacterGrpcService 3754→4450、Battle/Server 1808→2252），
# 之前没有任何地方能看见「债在涨」。这个脚本只打印事实：
#   - 非生成 .cs 的前 10 名，以及超过 LIMIT_CS 的清单；
#   - tools/check/*.sh 的前 5 名，以及超过 LIMIT_SH 的条数（纪律是单脚本 ≤350）。
# 它**永远退出 0**，不参与 CI 失败判定：阈值不是闸门，拆不拆看 14.8 的排期。
#
#   bash tools/check/size-report.sh
#
# 口径：
#   - 排除 src/Shared/Generated、client/.../Assets/Proto（proto 生成物，不手写）；
#   - 排除 obj / bin；
#   - 客户端目录不存在（多机 rsync 副本）就跳过那一段，不报假红。

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 0

LIMIT_CS=800
LIMIT_SH=350

echo "体量报告（只告警，不影响 CI 结果）"

# ---------------------------------------------------------------- 非生成 .cs
cs_list=""
for dir in src "client/MMOGame/Assets"; do
  [ -d "$dir" ] || continue
  cs_list="$cs_list$(find "$dir" -name '*.cs' \
      -not -path '*/obj/*' -not -path '*/bin/*' \
      -not -path '*/Generated/*' -not -path '*/Assets/Proto/*' 2>/dev/null)
"
done

cs_rows="$(printf '%s' "$cs_list" | grep -v '^$' | xargs wc -l 2>/dev/null \
  | awk '$2 != "total" { printf "%d\t%s\n", $1, $2 }' | sort -rn)"
cs_total="$(printf '%s\n' "$cs_rows" | grep -c . || true)"

echo
echo "1. 非生成 .cs：共 ${cs_total} 个；超过 ${LIMIT_CS} 行的："
over_cs="$(printf '%s\n' "$cs_rows" | awk -F'\t' -v L="$LIMIT_CS" '$1 > L' || true)"
if [ -n "$over_cs" ]; then
  printf '%s\n' "$over_cs" | while IFS=$'\t' read -r n f; do
    printf '   %6s  %s\n' "$n" "$f"
  done
else
  echo "   （无）"
fi

echo
echo "   前 10 名："
printf '%s\n' "$cs_rows" | head -10 | while IFS=$'\t' read -r n f; do
  printf '   %6s  %s\n' "$n" "$f"
done

# ------------------------------------------------------------- check 脚本
echo
if [ -d tools/check ]; then
  sh_rows="$(wc -l tools/check/*.sh 2>/dev/null \
    | awk '$2 != "total" { printf "%d\t%s\n", $1, $2 }' | sort -rn)"
  sh_total="$(printf '%s\n' "$sh_rows" | grep -c . || true)"
  sh_over="$(printf '%s\n' "$sh_rows" | awk -F'\t' -v L="$LIMIT_SH" '$1 > L' | grep -c . || true)"
  echo "2. tools/check/*.sh：共 ${sh_total} 个，超过 ${LIMIT_SH} 行的 ${sh_over} 个；前 5 名："
  printf '%s\n' "$sh_rows" | head -5 | while IFS=$'\t' read -r n f; do
    printf '   %6s  %s\n' "$n" "$f"
  done
else
  echo "2. tools/check/ 不存在，跳过。"
fi

echo
echo "口径见 docs/开发架构.md 14.8；这里只报事实，恒退出 0。"
exit 0
