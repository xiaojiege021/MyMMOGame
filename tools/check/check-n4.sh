#!/usr/bin/env bash
# tools/check/check-n4.sh —— N4 铺开的采集节点进表世界屏（开发架构第 14 节第 83 行）。
#
# 完成标准：U4 列出的节点与世界服回包一致，不再只剩四个基准点。客户端不读 config。
#
#   bash tools/check/check-n4.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
TITLE="N4"
TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
if [ -n "$DOTNET" ]; then
  if (cd "$ROOT" && "$DOTNET" build src/World/World.csproj -v q -o "$TMP/bw") >/dev/null 2>&1 \
    && (cd "$ROOT" && "$DOTNET" build src/Gateway/Gateway.csproj -v q -o "$TMP/bg") >/dev/null 2>&1; then
    ok "World + Gateway 编译"
  else
    bad "World 或 Gateway 编译失败"
    (cd "$ROOT" && "$DOTNET" build src/World/World.csproj -v q) 2>&1 | tail -15 | detail
  fi
else
  skip "找不到 dotnet"
fi

echo "== 2. 静态断言 =="
PYBIN=""
for c in python3 python; do
  command -v "$c" >/dev/null 2>&1 && PYBIN="$c" && break
done
if [ -z "$PYBIN" ]; then
  bad "找不到 python3"
else
  ROOT="$ROOT" "$PYBIN" - >"$TMP/static.out" 2>&1 <<'PY'
import json, os

root = os.environ["ROOT"]

def read(rel):
    _sub = {"CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World"}
    p = os.path.join(root, rel)
    _name = os.path.basename(p)
    if _name in _sub and os.path.exists(p):
        # 14.8 拆分卡之后：主文件 + 分片目录一起读（断言文本不变，只改读法）。
        return open(p, encoding="utf-8").read() + "".join(
            open(f, encoding="utf-8").read()
            for f in sorted(__import__("glob").glob(os.path.join(os.path.dirname(p), _sub[_name], "*.cs"))))
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

proto = read("proto/mmo/world.proto")
gateway = read("src/Gateway/WorldApi.cs")
world = read("src/World/WorldGrpcService.cs")
net = read("client/MMOGame/Assets/Net/WorldGather.cs")
flow = read("client/MMOGame/Assets/HotUpdate/Ui/UiWorldFlow.cs")
screen = read("client/MMOGame/Assets/HotUpdate/Ui/UiGatherScreen.cs")
codex = read("client/MMOGame/Assets/HotUpdate/Ui/UiWorldCodex.cs")
allcards = read("tools/check/check-all.sh")

assert "ListGatherNodes" in proto and "GatherNodeInfo" in proto
assert "/world/gather/nodes" in gateway and "ListGatherNodesAsync" in gateway
assert "ListGatherNodes" in world
assert "ParseList" in net and "GatherNodeRow" in net
assert "LoadGatherNodes" in flow and "/world/gather/nodes" in flow
assert "n4_gather_screen=ready" in screen and "读取节点表" in screen
assert "NoGatherNodesYet" in codex
assert "static readonly GatherNode[] Nodes" not in codex
assert "ore_kulan_1" not in codex or "Id = \"ore_kulan_1\"" not in codex
assert "config/gather.json" not in screen
assert "n4)" in allcards

spread = json.load(open(os.path.join(root, "config/gather_spread.json"), encoding="utf-8"))
base = json.load(open(os.path.join(root, "config/gather.json"), encoding="utf-8"))
# 节点会随内容卡增加，只写下限与 base+spread=total 的结构关系（D5 口径）。
assert len(base["nodes"]) >= 4
assert len(spread["nodes"]) >= 14
assert len(base["nodes"]) + len(spread["nodes"]) >= 18
print("n4_static=ok nodes=%d" % (len(base["nodes"]) + len(spread["nodes"])))
PY
  if grep -q n4_static=ok "$TMP/static.out"; then
    ok "节点表接线、客户端不读配置、总节点不少于 18"
  else
    bad "N4 静态断言失败"
    cat "$TMP/static.out" | detail
  fi
fi

if [ ! -d "$ROOT/client/MMOGame" ]; then
  skip "这台机器没有客户端工程"
  finish "$TITLE"
fi

echo "== 3. Unity 编辑器自检 =="
if [ ! -x "$UNITY" ] && [ ! -f "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod N4Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity N4Check.Run 退出码 0"
else
  bad "Unity N4Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files proto gateway world_svc parse flow screen no_config self; do
  if grep -q "n4_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 n4_${key}=ok"
  else
    bad "编辑器自检 n4_${key} 没过"
    grep "n4_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

for key in pending path count ore_group herb_group spirit_group spirit_forbidden page page_next; do
  if grep -q "n4_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "节点自检 n4_self_${key}=ok"
  else
    bad "节点自检 n4_self_${key} 没过"
  fi
done

if grep -q "n4_gather_screen=ready" "$TMP/unity.log" 2>/dev/null; then
  ok "屏就绪 n4_gather_screen=ready"
else
  bad "屏没念就绪行"
fi

echo "== 4. 回归 u4 + c2（脚本） =="
if bash "$ROOT/tools/check/check-u4.sh" 2>&1 | tail -5 | tee "$TMP/u4.tail" | grep -qE '0 项未通过|全过'; then
  ok "check-u4 回归（见末尾摘要）"
else
  tail -8 "$TMP/u4.tail" | detail
  bad "check-u4 有未通过项"
fi

finish "$TITLE"