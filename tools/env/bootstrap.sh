#!/usr/bin/env bash
# 换机器开工前跑一遍：把仓库带不走的东西摆出来，能自动做的做掉，做不了的当场说清楚。
#
# 用法：
#   tools/env/bootstrap.sh            体检 + 生成 proto + 跑 E8
#   tools/env/bootstrap.sh --quiet    只体检，不跑核对
#
# 不进仓库的东西：Library、.tools、IL2CPP 补丁、*.env。前三样本脚本能重建，
# 后一样得自己从服务器取。Windows 上用 Git Bash 跑。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJ="$ROOT/client/MMOGame"
UNITY_VERSION="$(sed -n 's/^m_EditorVersion: //p' "$PROJ/ProjectSettings/ProjectVersion.txt" 2>/dev/null)"
QUIET=0
[ "${1:-}" = "--quiet" ] && QUIET=1

FAIL=0
WARN=0
ok()   { printf '  ok    %s\n' "$1"; }
warn() { WARN=$((WARN + 1)); printf '  注意  %s\n' "$1"; }
bad()  { FAIL=$((FAIL + 1)); printf '  失败  %s\n' "$1"; }

echo "== 0. 这台机器 =="
case "$(uname -s)" in
  Darwin) OS="mac";       MACHINE="$(uname -m)" ;;
  Linux)  OS="linux";     MACHINE="$(uname -m)" ;;
  MINGW*|MSYS*|CYGWIN*) OS="windows"; MACHINE="$(uname -m)" ;;
  *)      OS="unknown";   MACHINE="$(uname -m)" ;;
esac
printf '  %s / %s\n' "$OS" "$MACHINE"
[ "$OS" = "unknown" ] && warn "没识别出系统，后面几步可能要手动"
echo "  仓库 $ROOT"
echo "  Unity 版本（仓库里写的）：${UNITY_VERSION:-读不到}"

echo "== 1. 命令 =="
need_hard="git curl"
for c in $need_hard; do
  if command -v "$c" >/dev/null 2>&1; then ok "$c"; else bad "缺 $c"; fi
done
if command -v python3 >/dev/null 2>&1; then
  ok "python3"
elif command -v python >/dev/null 2>&1; then
  warn "只有 python，没有 python3。gen-proto 与核对脚本写的是 python3，建个别名或装一个"
else
  bad "缺 python3（gen-proto 解包和几个核对脚本要用）"
fi
if command -v unzip >/dev/null 2>&1; then ok "unzip"; else warn "没有 unzip，gen-proto 会改用 python 解包"; fi
DOTNET=""
for c in "${DOTNET:-}" "$HOME/.dotnet/dotnet" "$(command -v dotnet 2>/dev/null || true)"; do
  [ -n "$c" ] && [ -x "$c" ] && { DOTNET="$c"; break; }
done
if [ -n "$DOTNET" ]; then
  ok "dotnet $("$DOTNET" --version 2>/dev/null | head -1)（${DOTNET}）"
else
  warn "没有 dotnet。只在客户端干活可以先不管；跑服务端和 E8 核对要装 10.0.403"
fi

echo "== 2. 仓库状态 =="
BRANCH="$(git -C "$ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null)"
echo "  分支 ${BRANCH}，HEAD $(git -C "$ROOT" log --oneline -1 2>/dev/null)"
DIRTY="$(git -C "$ROOT" status --porcelain | wc -l | tr -d ' ')"
if [ "$DIRTY" = "0" ]; then ok "工作区干净"; else warn "有 $DIRTY 个未提交改动，先看清是不是自己刚改的"; fi
if git -C "$ROOT" rev-parse --abbrev-ref "@{u}" >/dev/null 2>&1; then
  read -r BEHIND AHEAD <<<"$(git -C "$ROOT" rev-list --left-right --count "@{u}...HEAD" 2>/dev/null)"
  if [ "${BEHIND:-0}" = "0" ]; then ok "与远端同步"; else warn "落后远端 $BEHIND 个提交，开工前先 git pull --rebase"; fi
  [ "${AHEAD:-0}" = "0" ] || warn "本地有 $AHEAD 个提交没推"
else
  warn "没有上游分支，检查 remote"
fi

# Unity 先在前面探好：E8 核对脚本要用同一条路径，第 5 步只负责报出来。
# 编辑器版本 2022.3.62f3c1，Hub 目录常写成 2022.3.62f3，两种都找。
DIR_VERSION="$(printf '%s' "$UNITY_VERSION" | sed -E 's/c[0-9]+$//')"
FOUND=""
if [ -n "${UNITY:-}" ] && [ -x "${UNITY:-}" ]; then
  FOUND="$UNITY"
else
  for v in "$UNITY_VERSION" "$DIR_VERSION"; do
    [ -n "$v" ] || continue
    for p in \
      "/Applications/Unity/Hub/Editor/$v/Unity.app/Contents/MacOS/Unity" \
      "$HOME/Applications/Unity/Hub/Editor/$v/Unity.app/Contents/MacOS/Unity" \
      "/c/Program Files/Unity/Hub/Editor/$v/Editor/Unity.exe" \
      "/c/Program Files/Unity/$v/Editor/Unity.exe" \
      "/d/Program Files/Unity/Hub/Editor/$v/Editor/Unity.exe" \
      "/d/Program Files/Unity/$v/Editor/Unity.exe"; do
      [ -f "$p" ] && { FOUND="$p"; break 2; }
    done
  done
fi

if [ "$QUIET" = "0" ]; then
  echo "== 3. 生成 proto =="
  if bash "$ROOT/tools/gen/gen-proto.sh"; then
    # 用内容比较，不用 status --porcelain：protoc 重写文件会改 mtime，
    # Windows 上 git 会把它当成已修改（stat 缓存没刷新），内容其实一模一样。
    if git -C "$ROOT" diff --quiet -- src/Shared/Generated client/MMOGame/Assets/Proto/Generated; then
      ok "生成物与仓库一致，没有手改过的生成代码"
    else
      bad "生成物和仓库不一致：要么 .proto 改了没提交，要么有人手改了生成代码"
    fi
  else
    bad "gen-proto 跑失败"
  fi

  echo "== 4. E8 两端一致 =="
  if [ -x "$ROOT/tools/check/check-e8.sh" ]; then
    # check-e8.sh 里的 Unity 默认路径是 macOS 的，把这里探到的传给它
    [ -n "$FOUND" ] && export UNITY="$FOUND"
    if bash "$ROOT/tools/check/check-e8.sh" >/tmp/mmo-bootstrap-e8.log 2>&1; then
      ok "$(grep -c '^  ok' /tmp/mmo-bootstrap-e8.log | tr -d ' ') 项通过，日志 /tmp/mmo-bootstrap-e8.log"
    else
      bad "E8 没过，看 /tmp/mmo-bootstrap-e8.log"
    fi
  else
    bad "缺 tools/check/check-e8.sh"
  fi
fi

echo "== 5. Unity =="
if [ -n "$FOUND" ]; then
  ok "找到 $FOUND"
  if [ "$UNITY_VERSION" != "$DIR_VERSION" ]; then
    echo "        目录名用 ${DIR_VERSION}，编辑器版本 ${UNITY_VERSION}，两边一致就行"
  fi
else
  warn "没找到 Unity ${UNITY_VERSION}。版本必须逐字相同，差一个补丁号资源会重转"
  echo '        装在别处就指过去：UNITY="/d/某某/Unity.exe" tools/env/bootstrap.sh'
fi

echo "== 6. IL2CPP 补丁 =="
PATCH_DIR="$(ls -d "$PROJ"/Library/HybridCLR/LocalIl2CppData-* 2>/dev/null | head -1)"
if [ -n "$PATCH_DIR" ]; then
  ok "已装：${PATCH_DIR#"$PROJ"/}"
else
  warn "这台机器还没装补丁（约 106MB，进不了仓库，也不通用）"
  cat <<'STEPS'
        装法：
          ./tools/env/fetch-il2cpp-patch.sh
          然后用 Unity 打开 client/MMOGame，菜单走 E7 的「装 IL2CPP 补丁」，或
          设 MMO_IL2CPP_SRC 指向 .tools/il2cpp-patch 后调 Assets/Editor/E7Install.cs
        补丁按操作系统分开装，Mac 装的和 Windows 装的不通用
STEPS
fi

echo "== 7. 还得自己做的 =="
cat <<'STEPS'
  - Unity 里装 Android 构建模块和 SDK/NDK（首次打开工程时按提示装）
  - 服务端密码从 Ubuntu 笔记本取 /srv/mmo/gamedb.env，不要提交
  - Windows 上 E7 的核对脚本按 macOS 路径写死，走编辑器菜单跑 E7Check
STEPS

echo "== 8. 前置闸门（防返工） =="
# GUID / 静态断言 / state 入口 三道闸；软性提示，不阻断主体检（有些机器没 Unity）。
if [ -f "$ROOT/tools/check/preflight.sh" ]; then
  if bash "$ROOT/tools/check/preflight.sh"; then
    :
  else
    warn "前置闸门有硬性失败（GUID 非法），改完再提交：bash tools/check/preflight.sh"
  fi
else
  warn "缺 tools/check/preflight.sh"
fi

echo "== 9. 提交作者身份 =="
# 换机器开工时 git 会用系统默认名加主机名邮箱（例如 yewenkeji@*.local），
# Gitea 按邮箱认账号，认不出就只在作者列显示一个陌生名字。只在仓库级没配时补，
# 不覆盖别人机器上已经选定的身份。
CUR_MAIL="$(git -C "$ROOT" config --local user.email 2>/dev/null || true)"
if [ -z "$CUR_MAIL" ]; then
  git -C "$ROOT" config --local user.name "xiaojiege"
  git -C "$ROOT" config --local user.email "xiaojiege@users.noreply.github.com"
  ok "已补上仓库级提交身份：xiaojiege <xiaojiege@users.noreply.github.com>"
elif [ "$CUR_MAIL" = "xiaojiege@users.noreply.github.com" ]; then
  ok "提交身份与 Gitea 账号一致"
else
  warn "仓库级提交邮箱是 $CUR_MAIL，与 Gitea 账号不一致；要改就跑 git config --local user.email xiaojiege@users.noreply.github.com"
fi

echo
if [ "$FAIL" = "0" ]; then
  echo "体检：$WARN 项要注意，0 项失败"
else
  echo "体检：$WARN 项要注意，$FAIL 项失败"
  exit 1
fi
