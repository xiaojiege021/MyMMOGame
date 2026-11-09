#!/usr/bin/env bash
# E8 核对：一份 proto，两边生成同一份空消息。
#
# 做四件事：
#   1. 重新生成一遍，和提交进仓库的两份代码逐字比对（防止手改生成物）
#   2. 服务端跑一次空消息序列化，打印 proto_type / proto_file / proto_bytes / proto_roundtrip
#   3. 客户端在 Unity 里跑同一段，打印同样四行
#   4. 两边逐行比对
#
# 用法：
#   tools/check-e8.sh                 跑全部
#   SKIP_UNITY=1 tools/check-e8.sh    只跑服务端（没装 Unity 的机器上）
#
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

PASS=0
FAIL=0
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }

SERVER_OUT="$ROOT/src/Shared/Generated"
CLIENT_OUT="$ROOT/client/MMOGame/Assets/Proto/Generated"
CLIENT_PROJ="$ROOT/client/MMOGame"
FIELDS='^proto_(type|file|bytes|roundtrip)='

echo "== 1. 生成结果和仓库里的一致 =="
if bash "$ROOT/tools/gen-proto.sh" --into "$TMP/gen" >"$TMP/gen.log" 2>&1; then
  ok "重新生成成功（$(grep -m1 '^protoc:' "$TMP/gen.log" | cut -d' ' -f2-)）"
else
  bad "生成失败，见 $TMP/gen.log"
  cat "$TMP/gen.log"
fi

if diff -r -q -x "*.meta" -x "*Grpc.cs" "$TMP/gen" "$SERVER_OUT" >/dev/null 2>&1; then
  ok "服务端生成物与仓库一致"
else
  bad "服务端生成物和 proto 不一致，重新跑 tools/gen-proto.sh 并提交"
  diff -r -x "*.meta" -x "*Grpc.cs" "$TMP/gen" "$SERVER_OUT" | head -10
fi

# 消息代码两端必须逐字相同；*Grpc.cs（服务契约）只生成在服务端——
# 客户端不直连角色服，Unity 里也没有 Grpc 运行库。P1-1b1 起有这个区别。
if diff -r -q -x "*.meta" -x "*Grpc.cs" "$SERVER_OUT" "$CLIENT_OUT" >/dev/null 2>&1; then
  ok "两端消息代码逐字相同（gRPC 服务契约只在服务端）"
else
  bad "两端生成物不同"
  diff -r -x "*.meta" -x "*Grpc.cs" "$SERVER_OUT" "$CLIENT_OUT" | head -10
fi

echo "== 2. 服务端把空消息序列化一遍 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet，跳过服务端核对"
else
  if "$DOTNET" run --project "$ROOT/tools/ProtoProbe/ProtoProbe.csproj" -c Release --nologo \
       >"$TMP/server.txt" 2>"$TMP/server.err"; then
    ok "服务端序列化通过"
  else
    bad "服务端序列化失败"
    tail -5 "$TMP/server.err"
  fi
  grep -E "$FIELDS" "$TMP/server.txt" >"$TMP/server.proto" || true
  sed -n 's/^/  /p' "$TMP/server.proto"
fi

if [ "${SKIP_UNITY:-0}" = "1" ]; then
  echo "== 3. 客户端跳过（SKIP_UNITY=1）=="
  CLIENT_DONE=0
else
  echo "== 3. 客户端在 Unity 里把同一条消息序列化一遍 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    CLIENT_DONE=0
  else
    "$UNITY" -batchmode -quit -projectPath "$CLIENT_PROJ" \
      -executeMethod E8Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1
    UNITY_EXIT=$?
    grep -E "$FIELDS" "$TMP/unity.log" >"$TMP/client.proto" || true
    if [ "$UNITY_EXIT" -eq 0 ] && [ -s "$TMP/client.proto" ]; then
      ok "客户端序列化通过"
      CLIENT_DONE=1
    else
      bad "客户端序列化失败（Unity 退出码 $UNITY_EXIT）"
      grep -E "error CS|will not be loaded|proto_fail=" "$TMP/unity.log" | head -5
      CLIENT_DONE=0
    fi
    sed -n 's/^/  /p' "$TMP/client.proto"
  fi
fi

echo "== 4. 两边逐行比对 =="
if [ "${CLIENT_DONE:-0}" = "1" ]; then
  if diff -u "$TMP/server.proto" "$TMP/client.proto" >"$TMP/diff.txt"; then
    ok "客户端与服务端是同一条空消息"
  else
    bad "两端消息不一致"
    cat "$TMP/diff.txt"
  fi

  if grep -q '^proto_bytes=0$' "$TMP/client.proto" && grep -q '^proto_bytes=0$' "$TMP/server.proto"; then
    ok "空消息序列化后是 0 字节"
  else
    bad "空消息序列化后不是 0 字节"
  fi
else
  echo "  跳过：客户端没有跑"
fi

echo
echo "E8 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
