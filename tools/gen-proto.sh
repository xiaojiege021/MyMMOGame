#!/usr/bin/env bash
# 从 proto/ 生成服务端和客户端的 C# 消息代码。
# 只用一份 proto，一个 protoc 版本，生成到两处，不改生成结果。
#
# 用法：
#   tools/gen-proto.sh            生成到仓库内两处目录
#   tools/gen-proto.sh --into DIR 生成到指定目录（核对脚本用）
#
# protoc 从 NuGet 上的 Grpc.Tools 包取，版本写在下面，缓存在 .tools/。
# 想换版本就改 GRPC_TOOLS_VERSION，不要混用两个版本生成。
set -euo pipefail

GRPC_TOOLS_VERSION="2.85.0"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROTO_DIR="$ROOT/proto"
SERVER_OUT="$ROOT/src/Shared/Generated"
CLIENT_OUT="$ROOT/client/MMOGame/Assets/Proto/Generated"

INTO=""
while [ $# -gt 0 ]; do
  case "$1" in
    --into) INTO="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

case "$(uname -s)" in
  Darwin) PLATFORM="macosx_universal" ;;
  Linux)  PLATFORM="linux_$( [ "$(uname -m)" = "aarch64" ] && echo arm64 || echo x64 )" ;;
  *) echo "unsupported os: $(uname -s)" >&2; exit 3 ;;
esac

CACHE="$ROOT/.tools/grpc.tools-$GRPC_TOOLS_VERSION"
PROTOC="$CACHE/tools/$PLATFORM/protoc"

if [ ! -x "$PROTOC" ]; then
  echo "取 protoc $GRPC_TOOLS_VERSION ($PLATFORM)"
  mkdir -p "$CACHE"
  PKG="$ROOT/.tools/grpc.tools-$GRPC_TOOLS_VERSION.nupkg"
  if [ ! -f "$PKG" ]; then
    curl -fsSL -o "$PKG" \
      "https://api.nuget.org/v3-flatcontainer/grpc.tools/$GRPC_TOOLS_VERSION/grpc.tools.$GRPC_TOOLS_VERSION.nupkg"
  fi
  unzip -qo "$PKG" -d "$CACHE"
  chmod +x "$PROTOC"
fi

PROTOC_VERSION="$("$PROTOC" --version)"
echo "protoc: $PROTOC_VERSION"

run() {
  local out="$1"
  rm -rf "$out"
  mkdir -p "$out"
  # shellcheck disable=SC2046
  "$PROTOC" -I "$PROTO_DIR" --csharp_out="$out" $(cd "$PROTO_DIR" && find . -name '*.proto' | sed 's|^\./||' | sort)
  echo "输出: ${out#"$ROOT"/}"
}

if [ -n "$INTO" ]; then
  run "$INTO"
else
  run "$SERVER_OUT"
  run "$CLIENT_OUT"
fi
