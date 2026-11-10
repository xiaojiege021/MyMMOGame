#!/usr/bin/env bash
# tools/check/nightly-full.sh —— 每天定时跑的全量回归。
#
# 用途：兜底。白天做卡只跑依赖闭包（check-all.sh --card），
#       晚上定点把「整目录同步到笔记本 + 全量 --full」跑一遍，只留一份汇总日志。
#
# 为什么在笔记本跑：服务端连库连服核对需要 gamedb，gamedb 只在笔记本（ubu）。
#       本脚本从这台 Mac 把最新代码 rsync 过去，再 ssh 过去跑 --full，
#       这样永远不会因为「忘了同步」而跑旧代码。
#
# 用法（手动）：
#   bash tools/check/nightly-full.sh
# 定时（crontab，每天 19:30）：
#   30 19 * * *  cd /Users/yewenkeji/MyMMOGame && bash tools/check/nightly-full.sh >> /tmp/mmogame-nightly.log 2>&1
#
# 结果：笔记本上留 /tmp/mmogame-full-<日期>.log；本机留 /tmp/mmogame-nightly.log 摘要。
# 有卡失败时脚本 exit 1（cron 能看到非零退出）。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

REMOTE="ubu"
REMOTE_DIR="p11a1"
STAMP="$(date +%Y%m%d-%H%M%S)"
REMOTE_LOG="/tmp/mmogame-full-${STAMP}.log"

echo "=== nightly 全量回归 ${STAMP} ==="

# 1. 整目录同步（先拉最新，再推到笔记本）
echo "[1/3] 同步代码到笔记本 ..."
if ! git pull --rebase --quiet; then
  echo "FAIL git pull --rebase 失败，中止"
  exit 1
fi
if ! rsync -az --exclude 'obj/' --exclude 'bin/' src db config tools proto docs "${REMOTE}:~/${REMOTE_DIR}/"; then
  echo "FAIL rsync 到笔记本失败，中止"
  exit 1
fi
echo "ok  同步完成"

# 2. 笔记本上跑全量
echo "[2/3] 笔记本跑 check-all.sh --full ..."
ssh -o BatchMode=yes "$REMOTE" \
  "cd ~/${REMOTE_DIR} && bash tools/check/check-all.sh --full > ${REMOTE_LOG} 2>&1; echo EXIT=\$?" \
  > "$ROOT/.nightly-remote-exit.tmp" 2>&1
REMOTE_CODE="$(grep -o 'EXIT=[0-9]*' "$ROOT/.nightly-remote-exit.tmp" | tail -1 | cut -d= -f2)"
rm -f "$ROOT/.nightly-remote-exit.tmp"
echo "ok  笔记本全量完成（exit=${REMOTE_CODE:-未知}）"

# 3. 拉回结果，只贴红字
echo "[3/3] 汇总 ..."
ssh -o BatchMode=yes "$REMOTE" "tail -60 ${REMOTE_LOG}" 2>/dev/null \
  | grep -E '^==|^  ok|^  FAIL|全部通过|有卡未通过|全量：' || true
echo "远程完整日志：${REMOTE}:${REMOTE_LOG}"

if [ "${REMOTE_CODE:-1}" = "0" ]; then
  echo "nightly 全量：全部通过"
  exit 0
else
  echo "nightly 全量：有卡未通过（见上，或远程日志）"
  exit 1
fi
