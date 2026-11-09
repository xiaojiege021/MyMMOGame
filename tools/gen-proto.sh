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

PROTOC_NAME="protoc"
case "$(uname -s)" in
  Darwin) PLATFORM="macosx_universal" ;;
  Linux)  PLATFORM="linux_$( [ "$(uname -m)" = "aarch64" ] && echo arm64 || echo x64 )" ;;
  MINGW*|MSYS*|CYGWIN*) PLATFORM="windows_x64"; PROTOC_NAME="protoc.exe" ;;
  *) echo "unsupported os: $(uname -s)" >&2; exit 3 ;;
esac

CACHE="$ROOT/.tools/grpc.tools-$GRPC_TOOLS_VERSION"
PROTOC="$CACHE/tools/$PLATFORM/$PROTOC_NAME"

unzip_pkg() {
  if command -v unzip >/dev/null 2>&1; then
    unzip -qo "$1" -d "$2"
    return
  fi
  for PY in python3 python; do
    if command -v "$PY" >/dev/null 2>&1; then
      "$PY" -c 'import sys,zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$1" "$2"
      return
    fi
  done
  echo "既没有 unzip 也没有 python，解不开 $1" >&2
  exit 4
}

if [ ! -f "$PROTOC" ]; then
  echo "取 protoc $GRPC_TOOLS_VERSION ($PLATFORM)"
  mkdir -p "$CACHE"
  PKG="$ROOT/.tools/grpc.tools-$GRPC_TOOLS_VERSION.nupkg"
  if [ ! -f "$PKG" ]; then
    curl -fsSL -o "$PKG" \
      "https://api.nuget.org/v3-flatcontainer/grpc.tools/$GRPC_TOOLS_VERSION/grpc.tools.$GRPC_TOOLS_VERSION.nupkg"
  fi
  unzip_pkg "$PKG" "$CACHE"
  # protoc 与 grpc 插件都要可执行，解压出来默认没有执行位。
  chmod +x "$CACHE"/tools/*/protoc* "$CACHE"/tools/*/grpc_csharp_plugin* 2>/dev/null || true
fi

PROTOC_VERSION="$("$PROTOC" --version)"
echo "protoc: $PROTOC_VERSION"

# gRPC 服务端代码用同一个包里的插件生成。客户端不生成：
# Unity 里没有 Grpc 运行库，客户端也不直连角色服（走网关的 HTTP 入口）。
PLUGIN="$CACHE/tools/$PLATFORM/grpc_csharp_plugin"
[ "$PROTOC_NAME" = "protoc.exe" ] && PLUGIN="$CACHE/tools/$PLATFORM/grpc_csharp_plugin.exe"

proto_files() {
  (cd "$PROTO_DIR" && find . -name '*.proto' | sed 's|^\./||' | sort)
}

# $1=输出目录 $2=1 时连 gRPC 服务代码一起生成
run() {
  local out="$1" with_grpc="${2:-0}"
  mkdir -p "$out"
  # 只清生成物，留着 .cs.meta：Unity 的 GUID 进仓库，删了会让两台机器的 GUID 对不上
  find "$out" -maxdepth 1 -type f -name '*.cs' -delete
  # shellcheck disable=SC2046
  "$PROTOC" -I "$PROTO_DIR" --csharp_out="$out" $(proto_files)
  if [ "$with_grpc" = "1" ]; then
    # shellcheck disable=SC2046
    "$PROTOC" -I "$PROTO_DIR" \
      --plugin=protoc-gen-grpc="$PLUGIN" \
      --grpc_out="$out" $(proto_files)
  fi
  for m in "$out"/*.cs.meta; do
    [ -e "$m" ] || continue
    [ -f "${m%.meta}" ] || rm -f "$m"
  done
  echo "输出: ${out#"$ROOT"/} gRPC=$with_grpc"
}

if [ -n "$INTO" ]; then
  run "$INTO" 1
else
  run "$SERVER_OUT" 1
  run "$CLIENT_OUT" 0
fi
