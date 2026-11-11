#!/usr/bin/env bash
#
# 长生隙 · 推 Gitea 后同步 GitHub
# ============================================================
# 用途：把「推 Gitea (origin)」与「同步 GitHub 公开仓库」合并成一步。
#       先推内网 Gitea；推送成功后再跑 tools/sync-github.sh --push，
#       把清洗（改邮箱/IP、排除 PDF、去死链、补 LICENSE）后的内容
#       强推到 GitHub。
#
# 用法：
#   ./tools/push-and-sync.sh            # 推 main 到 Gitea，再同步 GitHub
#   ./tools/push-and-sync.sh <branch>   # 推指定分支
#
# 说明：
#   * git 没有「推送后」钩子，故用本包装脚本替代 `git push origin main`。
#   * 同步会重建临时镜像并重写历史（force push），全过程几分钟；
#     会改写 GitHub 上的提交哈希（fork/star 者需 rebase）。
#   * 同步前隐私闸门未通过会中止，绝不会把私人信息推到 GitHub。
#   * 推 Gitea 失败则不会继续同步（set -e 保证）。
#
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"

BRANCH="${1:-main}"

echo ">> [1/2] 推送 $BRANCH 到 Gitea (origin)…"
git push origin "$BRANCH"

echo ">> [2/2] 推 Gitea 成功，开始同步 GitHub（清洗 + 隐私校验 + force push）…"
exec "$ROOT/tools/sync-github.sh" --push
