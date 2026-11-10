#!/usr/bin/env bash
# 取 HybridCLR 的 IL2CPP 补丁源码，拼成 InstallerController.InstallFromLocal 要的目录。
#
# 官方安装按钮走 gitee 克隆，国内有些网络会拒 git-upload-pack；这里换成 GitHub 镜像的
# 发布包，按包内 Data~/hybridclr_version.json 里当前 Unity 对应的 tag 下载，结果放进
# .tools/il2cpp-patch/libil2cpp，再交给 E7Install.Run。
#
# 用法：tools/env/fetch-il2cpp-patch.sh
# 输出：最后一行是 libil2cpp 的父目录，可直接喂给 MMO_IL2CPP_SRC
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PKG="$ROOT/client/MMOGame/Packages/com.code-philosophy.hybridclr"
CACHE="$ROOT/.tools/il2cpp-patch"
SRC="$CACHE/libil2cpp"

UNITY_VERSION="${UNITY_VERSION:-2022.3}"

read -r HC_TAG IP_TAG < <(python3 - "$PKG/Data~/hybridclr_version.json" "$UNITY_VERSION" <<'PY'
import json, sys
data = json.load(open(sys.argv[1], encoding="utf-8"))
want = sys.argv[2]
match = next((v for v in data["versions"] if v["unity_version"] == want), None)
if match is None:
    sys.exit("没有找到 Unity %s 对应的补丁版本" % want)
print(match["hybridclr"]["tag"], match["il2cpp_plus"]["tag"])
PY
)

echo "hybridclr=$HC_TAG il2cpp_plus=$IP_TAG"

mkdir -p "$CACHE"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fetch() {
  local repo="$1" tag="$2" out="$3"
  if [ -s "$out" ]; then
    echo "  已有 $(basename "$out")，跳过下载"
    return
  fi
  echo "  下载 $repo@$tag"
  curl -sL -m 600 -o "$out" "https://api.github.com/repos/focus-creative-games/$repo/tarball/$tag" \
    -w "  http=%{http_code} size=%{size_download}\n"
  tar tzf "$out" >/dev/null 2>&1 || { echo "  $repo 的包不完整" >&2; rm -f "$out"; exit 1; }
}

fetch hybridclr "$HC_TAG" "$CACHE/hybridclr.tar.gz"
fetch il2cpp_plus "$IP_TAG" "$CACHE/il2cpp_plus.tar.gz"

rm -rf "$SRC" "$WORK/hc" "$WORK/ip"
mkdir -p "$WORK/hc" "$WORK/ip"
tar xzf "$CACHE/hybridclr.tar.gz" -C "$WORK/hc"
tar xzf "$CACHE/il2cpp_plus.tar.gz" -C "$WORK/ip"

cp -R "$WORK"/ip/*/libil2cpp "$SRC"
cp -R "$WORK"/hc/*/hybridclr "$SRC/hybridclr"

echo "补丁源码就绪：$(du -sh "$SRC" | awk '{print $1}')"
echo "$CACHE"
