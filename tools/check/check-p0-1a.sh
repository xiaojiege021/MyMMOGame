#!/usr/bin/env bash
# P0-1a 核对：技能请求和技能结果，两端同一份形状，字段齐，能往返。
#
# 做四件事：
#   1. 重新生成一遍，和仓库里两份生成代码逐字比对（防止手改生成物）
#   2. 服务端反射打印两个消息的字段清单，再各走一次序列化往返
#   3. 客户端在 Unity 里打同样几行
#   4. 两边逐行比对，并逐项确认第 14 节要求的字段都在
#
# 用法：
#   tools/check/check-p0-1a.sh
#   SKIP_UNITY=1 tools/check/check-p0-1a.sh   只跑服务端（没装 Unity 的机器上）
#
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
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
PROBE_DLL="$ROOT/tools/probe/ProtoProbe/bin/Release/net10.0/ProtoProbe.dll"
FIELDS='^p0_'

echo "== 1. 生成结果和仓库里的一致 =="
if bash "$ROOT/tools/gen/gen-proto.sh" --into "$TMP/gen" >"$TMP/gen.log" 2>&1; then
  ok "重新生成成功（$(grep -m1 '^protoc:' "$TMP/gen.log" | cut -d' ' -f2-)）"
else
  bad "生成失败，见 $TMP/gen.log"
  cat "$TMP/gen.log"
fi

if diff -r -q -x "*.meta" -x "*Grpc.cs" "$TMP/gen" "$SERVER_OUT" >/dev/null 2>&1; then
  ok "服务端生成物与仓库一致"
else
  bad "服务端生成物和 proto 不一致，重新跑 tools/gen/gen-proto.sh 并提交"
  diff -r -x "*.meta" "$TMP/gen" "$SERVER_OUT" | head -10
fi

if diff -r -q -x "*.meta" -x "*Grpc.cs" "$SERVER_OUT" "$CLIENT_OUT" >/dev/null 2>&1; then
  ok "客户端与服务端两份生成物逐字相同"
else
  bad "两端生成物不同"
  diff -r -x "*.meta" "$SERVER_OUT" "$CLIENT_OUT" | head -10
fi

echo "== 2. 服务端把请求和结果各走一次 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet，跳过服务端核对"
else
  if "$DOTNET" build "$ROOT/tools/probe/ProtoProbe/ProtoProbe.csproj" -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
    if "$DOTNET" "$PROBE_DLL" --p0-1a >"$TMP/server.txt" 2>"$TMP/server.err"; then
      ok "服务端往返通过"
    else
      bad "服务端往返失败"
      tail -5 "$TMP/server.err"
    fi
  else
    bad "ProtoProbe 编译失败"
    grep -E "error " "$TMP/build.log" | head -5
  fi
  grep -E "$FIELDS" "$TMP/server.txt" >"$TMP/server.proto" 2>/dev/null || true
  sed -n 's/^/  /p' "$TMP/server.proto"
fi

if [ "${SKIP_UNITY:-0}" = "1" ]; then
  echo "== 3. 客户端跳过（SKIP_UNITY=1）=="
  CLIENT_DONE=0
else
  echo "== 3. 客户端在 Unity 里把同样两条走一次 =="
  if [ ! -x "$UNITY" ]; then
    bad "找不到 Unity：$UNITY"
    CLIENT_DONE=0
  else
    "$UNITY" -batchmode -quit -projectPath "$CLIENT_PROJ" \
      -executeMethod P0Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1
    UNITY_EXIT=$?
    grep -E "$FIELDS" "$TMP/unity.log" >"$TMP/client.proto" 2>/dev/null || true
    if [ "$UNITY_EXIT" -eq 0 ] && [ -s "$TMP/client.proto" ]; then
      ok "客户端往返通过"
      CLIENT_DONE=1
    else
      bad "客户端往返失败（Unity 退出码 $UNITY_EXIT）"
      grep -E "error CS|will not be loaded|p0_fail=" "$TMP/unity.log" | head -5
      CLIENT_DONE=0
    fi
    sed -n 's/^/  /p' "$TMP/client.proto"
  fi
fi

echo "== 4. 两边逐行比对 =="
if [ "${CLIENT_DONE:-0}" = "1" ]; then
  if diff -u "$TMP/server.proto" "$TMP/client.proto" >"$TMP/diff.txt"; then
    ok "客户端与服务端是同一组消息和字段"
  else
    bad "两端字段不一致"
    cat "$TMP/diff.txt"
  fi
else
  echo "  跳过：客户端没有跑"
fi

echo "== 5. 第 14 节要求的字段 =="
REQUEST_LINE="$(grep '^p0_request_fields=' "$TMP/server.proto" 2>/dev/null | head -1)"
RESULT_LINE="$(grep '^p0_result_fields=' "$TMP/server.proto" 2>/dev/null | head -1)"

check_field() {
  local name="$1" line="$2" where="$3"
  case "$line" in
    *",$name,"*|*",$name"|*"$name,"*) ok "$where 有 $name" ;;
    *) bad "$where 缺 $name" ;;
  esac
}

for f in skill_id target_id cast_time_ms; do
  check_field "$f" "$REQUEST_LINE" "请求"
done
for f in hit damage healing stagger_ms cooldown_ms; do
  check_field "$f" "$RESULT_LINE" "结果"
done

if grep -q '^p0_roundtrip=ok$' "$TMP/server.proto" 2>/dev/null; then
  ok "服务端两个消息都能原样往返"
else
  bad "服务端往返结果不是 ok"
fi

echo
echo "P0-1a 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
