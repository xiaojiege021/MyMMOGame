#!/usr/bin/env bash
#
# 长生隙 · GitHub 公开镜像同步脚本
#
# 作用：把本机主仓库（内网 Gitea，可能含私人邮箱 / 私人 IP / 未公开 PDF 原稿）
#       清洗后推到 GitHub 公开仓库。
#
# 安全约定：
#   * 本脚本【绝不修改】主仓库，只在 /tmp 下的临时克隆上操作。
#   * 所有私人信息（邮箱、IP）都从主仓库“实时提取”，脚本本身不含任何明文私人串。
#   * 推送前会做隐私校验闸门；任何私人串没清干净都拒绝推送。
#   * 默认只构建并校验镜像，不推送；加 --push 才真正推到 GitHub。
#
# 用法：
#   ./sync-github.sh            # 构建清洗镜像 + 隐私校验（不推送，安全预览）
#   ./sync-github.sh --push     # 校验通过后强制推送到 GitHub
#
# 环境变量（均可覆盖，一般不需要改）：
#   MAIN_REPO   主仓库路径（默认：脚本所在目录的上一级）
#   MIRROR_DIR  临时镜像目录（默认：/tmp/mmo-github-mirror）
#   GH_REMOTE   GitHub 仓库 SSH 地址

set -euo pipefail

# ---------- 基本路径 ----------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MAIN_REPO="${MAIN_REPO:-$(cd "$SCRIPT_DIR/.." && pwd)}"
MIRROR_DIR="${MIRROR_DIR:-/tmp/mmo-github-mirror}"
GH_REMOTE="${GH_REMOTE:-git@github.com:xiaojiege021/MyMMOGame.git}"
GH_USER_NAME="xiaojiege"
GH_USER_EMAIL="xiaojiege@users.noreply.github.com"

PUSH=0
for a in "$@"; do
  [ "$a" = "--push" ] && PUSH=1
done

echo ">> 主仓库： $MAIN_REPO"
echo ">> 镜像目录： $MIRROR_DIR"
echo ">> GitHub： $GH_REMOTE"
echo

# ---------- 前置检查 ----------
if ! command -v git-filter-repo >/dev/null 2>&1; then
  echo ">> 未找到 git-filter-repo，尝试安装…"
  if command -v brew >/dev/null 2>&1; then
    brew install git-filter-repo
  elif command -v pip3 >/dev/null 2>&1; then
    pip3 install git-filter-repo
  else
    echo "!! 请先安装 git-filter-repo（brew install git-filter-repo 或 pip3 install git-filter-repo）" >&2
    exit 1
  fi
fi

echo ">> 检查 GitHub SSH 连通…"
# 注意：GitHub SSH 即使认证成功也返回退出码 1（"does not provide shell access"），
# 故用 || true 接住 ssh，再对捕获到的输出做判断，避免 pipefail 把成功判成失败。
SSH_OUT="$(ssh -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o ConnectTimeout=10 -T git@github.com 2>&1 || true)"
if ! echo "$SSH_OUT" | grep -q 'Hi '; then
  echo "!! 无法以 SSH 连接到 GitHub（未认证或公钥未添加）。请先把本机公钥加到 GitHub → Settings → SSH and GPG keys。" >&2
  echo "   ssh 输出：$SSH_OUT" >&2
  exit 1
fi
echo "   SSH 连通正常（$(echo "$SSH_OUT" | grep 'Hi ' | head -1)）。"

if [ ! -d "$MAIN_REPO/.git" ]; then
  echo "!! 主仓库路径无效： $MAIN_REPO" >&2
  exit 1
fi

# ---------- 从主仓库实时提取需要抹掉的私人信息（脚本本身不含明文） ----------
# 注：macOS 自带 bash 为 3.2，无 mapfile，故用 while read 兼容写法。
PRIV_EMAILS=()
while IFS= read -r e; do
  [ -z "$e" ] && continue
  [ "$e" = "$GH_USER_EMAIL" ] && continue
  PRIV_EMAILS+=("$e")
done < <(git -C "$MAIN_REPO" log --all --pretty='%ae%n%ce' 2>/dev/null | sort -u)

PRIV_IP="$(git -C "$MAIN_REPO" remote get-url origin 2>/dev/null | grep -oE '[0-9]{1,3}(\.[0-9]{1,3}){3}' | head -1 || true)"

echo ">> 提取到的私人邮箱（将改写为 noreply）："
if [ "${#PRIV_EMAILS[@]}" -eq 0 ]; then
  echo "     无"
else
  printf '     %s\n' "${PRIV_EMAILS[@]}"
fi
echo ">> 提取到的私人 IP（将改写为 YOUR_SERVER_IP）： ${PRIV_IP:-无}"

# ---------- 准备清洗规则文件（写入 /tmp，不进仓库） ----------
MAP="$(mktemp)"
REP="$(mktemp)"
: > "$MAP"; : > "$REP"
for e in "${PRIV_EMAILS[@]:-}"; do
  [ -z "$e" ] && continue
  echo "xiaojiege <$GH_USER_EMAIL> <$e>" >> "$MAP"
  echo "$e==>$GH_USER_EMAIL" >> "$REP"
done
if [ -n "$PRIV_IP" ]; then
  echo "$PRIV_IP==>YOUR_SERVER_IP" >> "$REP"
fi

# ---------- 步骤 1：克隆主仓库到镜像（独立副本，不碰主仓库） ----------
echo
echo ">> [1/7] 克隆主仓库到临时镜像…"
rm -rf "$MIRROR_DIR"
git clone --no-hardlinks "$MAIN_REPO" "$MIRROR_DIR" >/dev/null 2>&1
cd "$MIRROR_DIR"
git remote rename origin gitea >/dev/null 2>&1 || true
git config user.name "$GH_USER_NAME"
git config user.email "$GH_USER_EMAIL"
# 排除含个人信息的两个 PDF 原稿（永远不进公开仓库）
EXCLUDE_PDF=("docs/游戏背景.pdf" "docs/游戏设定.pdf")

# ---------- 步骤 2：历史清洗（邮箱→noreply，私人 IP→占位，PDF 从历史抹掉） ----------
echo ">> [2/7] git filter-repo 清洗历史…"
git filter-repo --force \
  --mailmap "$MAP" \
  --replace-text "$REP" \
  --invert-paths \
  --path "${EXCLUDE_PDF[0]}" \
  --path "${EXCLUDE_PDF[1]}"

# ---------- 步骤 3：公开版 bootstrap.sh 不再写死私人邮箱 ----------
echo ">> [3/7] 中性化 bootstrap.sh 提交身份逻辑…"
python3 - <<'PY'
import re
p = 'tools/env/bootstrap.sh'
s = open(p, encoding='utf-8').read()
pat = re.compile(r'echo "== 9\. 提交作者身份 ==".*?\nfi\n', re.DOTALL)
assert pat.search(s), "bootstrap 第 9 步块未找到，可能文件结构已变，请人工核对"
new_block = (
    'echo "== 9. 提交作者身份 ==",\n'
).replace('"', '"') if False else (
    'echo "== 9. 提交作者身份 ==\\n'
    '# 开源协作：不替你写死邮箱。若本地还没配 git 身份，提示自行设置，\\n'
    '# 或读取可选环境变量 MMO_GIT_EMAIL 作为仓库级邮箱；不覆盖已选定的身份。\\n'
    'CUR_MAIL="$(git -C "$ROOT" config --local user.email 2>/dev/null || true)"\\n'
    'if [ -z "$CUR_MAIL" ]; then\\n'
    '  if [ -n "$MMO_GIT_EMAIL" ]; then\\n'
    '    git -C "$ROOT" config --local user.email "$MMO_GIT_EMAIL"\\n'
    '    ok "已用环境变量 MMO_GIT_EMAIL 补上仓库级提交邮箱"\\n'
    '  else\\n'
    '    warn "尚未配置 git 提交邮箱；本仓库是开源协作，请用你自己的身份："\\n'
    '    warn "  git config --local user.name "你的名字""\\n'
    '    warn "  git config --local user.email "you@example.com""\\n'
    '  fi\\n'
    'else\\n'
    '  ok "提交身份已配置：$CUR_MAIL"\\n'
    'fi\\n'
)
s = pat.sub(new_block, s, count=1)
open(p, 'w', encoding='utf-8').write(s)
print("   bootstrap.sh 已中性化")
PY

# ---------- 步骤 4：公开 README 去掉指向已删 PDF 的死链 ----------
echo ">> [4/7] 清理 README 中的 PDF 死链…"
python3 - <<'PY'
import re
p = 'README.md'
s = open(p, encoding='utf-8').read()
before = s
s = re.sub(r'.*游戏背景\.pdf.*\n', '', s)
s = re.sub(r'.*游戏设定\.pdf.*\n', '', s)
open(p, 'w', encoding='utf-8').write(s)
print("   已清理" if s != before else "   无死链可清（已干净）")
PY

# ---------- 步骤 5：补 MIT LICENSE（代码开源 / 文学内容保留著作权） ----------
echo ">> [5/7] 写入 MIT LICENSE…"
cat > LICENSE <<'EOF'
MIT License

Copyright (c) 2026 xiaojiege

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

---
内容说明：本仓库的修仙世界观设定、原创题字「一见隙开道成空」及旧谣文本为作者原创文学内容，
保留著作权；代码按上述 MIT 许可开源。如用于演绎或转载文字内容，请注明出处。
EOF

# ---------- 步骤 6：提交公开版清洗改动 ----------
echo ">> [6/7] 提交清洗改动到镜像…"
git add -A
if git diff --cached --quiet; then
  echo "   无新增改动（镜像已是最新清洗态）"
else
  git commit -q -m "chore: 公开版清洗——中性化 bootstrap / 去 PDF 死链 / MIT LICENSE"
  echo "   已提交清洗提交"
fi

# ---------- 步骤 7：隐私校验闸门 ----------
echo ">> [7/7] 隐私校验闸门…"
FAIL=0
ALL_EMAILS_NOW="$(git log --all --pretty='%ae' | sort -u)"
# 正确口径：若所有作者邮箱“都包含”noreply 即通过；用 grep -q + ! 规避 pipefail 把成功判失败
if ! echo "$ALL_EMAILS_NOW" | grep -q "$GH_USER_EMAIL"; then
  echo "   !! 不存在任何 noreply 作者邮箱（异常）："; echo "$ALL_EMAILS_NOW"
  FAIL=1
elif echo "$ALL_EMAILS_NOW" | grep -qv "$GH_USER_EMAIL"; then
  echo "   !! 仍存在非 noreply 的作者邮箱："; echo "$ALL_EMAILS_NOW"
  FAIL=1
fi
PRIV_HITS="$(git grep -n 'YOUR_SERVER_IP\|xiaojiege@users.noreply.github.com\|local-host' $(git rev-list --all) 2>/dev/null || true)"
if [ -n "$PRIV_HITS" ]; then
  echo "   !! 历史中仍含私人串："; echo "$PRIV_HITS" | head
  FAIL=1
fi
PDF_HIST="$(git log --all --oneline -- "${EXCLUDE_PDF[0]}" "${EXCLUDE_PDF[1]}" 2>/dev/null || true)"
if [ -n "$PDF_HIST" ]; then
  echo "   !! 历史中仍含 PDF 原稿："; echo "$PDF_HIST" | head
  FAIL=1
fi
PDF_TREE="$(git ls-files | grep -E '游戏背景\.pdf|游戏设定\.pdf' || true)"
if [ -n "$PDF_TREE" ]; then
  echo "   !! 当前文件树仍含 PDF 原稿： $PDF_TREE"
  FAIL=1
fi

echo
echo "   提交总数： $(git rev-list --count main)"
echo "   作者邮箱： $(git log --all --pretty='%ae' | sort -u | tr '\n' ' ')"
if [ "$FAIL" = "1" ]; then
  echo "!! 隐私校验未通过，已中止（未推送）。请人工核对上面标出的问题。"
  exit 1
fi
echo "   ✅ 隐私校验通过：无私人邮箱 / 无私人 IP / 无 PDF 原稿"

# ---------- 推送 ----------
if [ "$PUSH" = "1" ]; then
  echo
  echo ">> 推送到 GitHub（force，因历史已改写）…"
  git remote add github "$GH_REMOTE" 2>/dev/null || git remote set-url github "$GH_REMOTE"
  git push --force github main 2>&1 | tail -8
  echo
  echo "✅ 已推送到 $GH_REMOTE"
else
  echo
  echo "（预览模式：未推送）确认无误后，运行："
  echo "   $0 --push"
fi
