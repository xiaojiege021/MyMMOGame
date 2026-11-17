#!/usr/bin/env bash
# tools/check/check-n14.sh —— N14 地表列表与 N1 解锁一致（开发架构第 14 节第 98 行）。
#
# 完成标准：带角色时列表口按 RegionGate 的结论回开放（第 11 场 + 立仙/立魔 分路），
# 不带角色时退回静态 open（老卡不炸）；U4 的「已开 / 未开」以回包为准；不改地名与解锁规则。
#
#   bash tools/check/check-n14.sh
#       编译 + 静态断言 + 世界服列表口无库段 + 客户端拆装探针 + Unity U4 自检
#   MMO_N14_GATEWAY=http://127.0.0.1:61000 MMO_N14_TOKEN=<令牌> \
#     MMO_N14_CHARACTER=<角色号> bash tools/check/check-n14.sh
#       再加真网关段：读一次带角色号的地点表
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
WORLD_DLL="$ROOT/src/World/bin/Release/net10.0/World.dll"
PROBE_DLL="$ROOT/tools/probe/WorldEntryProbe/bin/Release/net10.0/WorldEntryProbe.dll"
GATEWAY="${MMO_N14_GATEWAY:-}"
TOKEN="${MMO_N14_TOKEN:-}"
CHARACTER="${MMO_N14_CHARACTER:-0}"
TITLE="N14（world + client）"
TMP="$(mktemp -d)"; TMP="${TMP//\\//}"
cleanup() { rm -rf "$TMP" 2>/dev/null || true; }
trap cleanup EXIT

echo "== 1. 编译 =="
for proj in src/World/World.csproj src/Gateway/Gateway.csproj \
            tools/probe/WorldEntryProbe/WorldEntryProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 ${n}"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ -f "$WORLD_DLL" ] || { bad "没有 $WORLD_DLL"; finish "$TITLE"; }

echo "== 2. 静态断言 =="
PYBIN=""
for c in python3 python; do
  if command -v "$c" >/dev/null 2>&1; then PYBIN="$c"; break; fi
done
if [ -z "$PYBIN" ]; then
  bad "找不到 python3 / python"
else
  if ROOT="$ROOT" "$PYBIN" "$ROOT/tools/check/check-n14-static.py" >"$TMP/static.out" 2>&1; then
    ok "列表口过 RegionGate、网关只转发、客户端只照回包写、总控已挂 N14"
  else
    bad "N14 静态断言失败"; cat "$TMP/static.out" | detail
  fi
fi

echo "== 3. 世界服列表口（无库，纯计算） =="
list_out() {
  "$DOTNET" "$WORLD_DLL" --region-list --main-stage "$1" ${2:+--alignment "$2"} 2>&1
}
row_of() { printf '%s' "$2" | grep "^region_list=id=$1 " || true; }

L11="$(list_out 11 立仙)"
A="$(row_of 旧天城区 "$L11")"
B="$(row_of 焦原 "$L11")"
if printf '%s' "$A" | grep -q 'open=true' && printf '%s' "$B" | grep -q 'open=false reason=region_route_mismatch'; then
  ok "第 11 场 + 立仙：旧天城区开、焦原 region_route_mismatch"
else
  bad "第 11 场 + 立仙 的列表不对"; printf '%s\n%s\n' "$A" "$B" | detail
fi
if printf '%s' "$L11" | grep -q '^region_list_summary=regions=8 open=7'; then
  ok "立仙这条路八块里开七块"
else
  bad "立仙的开放块数不对"; printf '%s' "$L11" | grep summary | detail
fi

L11M="$(list_out 11 立魔)"
A2="$(row_of 旧天城区 "$L11M")"
B2="$(row_of 焦原 "$L11M")"
if printf '%s' "$A2" | grep -q 'open=false reason=region_route_mismatch' \
   && printf '%s' "$B2" | grep -q 'open=true'; then
  ok "第 11 场 + 立魔：焦原开、旧天城区 region_route_mismatch"
else
  bad "第 11 场 + 立魔 的列表不对"; printf '%s\n%s\n' "$A2" "$B2" | detail
fi

L10="$(list_out 10 立仙)"
A3="$(row_of 旧天城区 "$L10")"
B3="$(row_of 焦原 "$L10")"
if printf '%s' "$A3" | grep -q 'open=false reason=region_locked unlock_stage=11' \
   && printf '%s' "$B3" | grep -q 'open=false reason=region_locked unlock_stage=11' \
   && printf '%s' "$L10" | grep -q '^region_list_summary=regions=8 open=6'; then
  ok "第 10 场：两块都 region_locked、门槛 11 照配置、开六块"
else
  bad "第 10 场 的列表不对"; printf '%s\n%s\n' "$A3" "$B3" | detail
fi

L11N="$(list_out 11)"
if printf '%s' "$L11N" | grep -q '^region_list=id=旧天城区 open=false reason=region_locked' \
   && printf '%s' "$L11N" | grep -q '^region_list_summary=regions=8 open=6'; then
  ok "没给路线：两块都 region_locked，不当成开了"
else
  bad "没给路线的列表不对"; printf '%s' "$L11N" | grep -E "旧天城区|summary" | detail
fi

# 静态口径没被改：不参与解锁的地表仍由配置 open 决定
if "$DOTNET" "$WORLD_DLL" --region-unlock-state 2>&1 | grep -q 'locked=旧天城区,焦原 static_open=6'; then
  ok "解锁表与静态 open 一行未改（locked=旧天城区,焦原 static_open=6）"
else
  bad "静态 open / 解锁表口径变了"
fi

echo "== 4. 客户端拆装（同一份 WorldEntry） =="
cat >"$TMP/regions.json" <<'JSON'
{"version":1,"character_id":77,"regions":[
{"id":"栖云城","name":"栖云城","chapters":[5],"neighbors":["外环隙"],"dialogue_ids":[],"open":true,"reason":"","unlock_stage":0,"main_stage":0},
{"id":"旧天城区","name":"旧天城区","chapters":[11],"neighbors":["栖云城"],"dialogue_ids":[],"open":true,"reason":"","unlock_stage":11,"main_stage":11},
{"id":"焦原","name":"焦原","chapters":[11],"neighbors":["栖云城"],"dialogue_ids":[],"open":false,"reason":"region_route_mismatch","unlock_stage":11,"main_stage":11}],
"speakers":[],"factions":[]}
JSON
POUT="$("$DOTNET" "$PROBE_DLL" --regions 200 "@$TMP/regions.json" 2>&1)"
if printf '%s' "$POUT" | grep -q '^regions=3' && printf '%s' "$POUT" | grep -q '^regions_open=2' \
   && printf '%s' "$POUT" | grep -q '^locked=焦原:region_route_mismatch'; then
  ok "客户端拆到 open / reason / unlock_stage（3 条里开 2 块，焦原路线不符）"
else
  bad "客户端没拆出新字段"; printf '%s' "$POUT" | detail
fi

echo "== 5. Unity（U4 地图屏） =="
if [ ! -d "$ROOT/client/MMOGame" ]; then
  echo "  skip 这台机器没有客户端工程"
elif [ ! -x "$UNITY" ] && [ ! -f "$UNITY" ]; then
  echo "  skip 找不到 Unity：$UNITY"
else
  if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod U4Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
    ok "Unity U4Check.Run 退出码 0"
  else
    bad "Unity U4Check.Run 失败"
    tail -30 "$TMP/unity.log" | detail
  fi

  for key in regions_before_reply regions_path regions_from_reply regions_route_mismatch \
             regions_locked_stage regions_screen_from_reply; do
    if grep -q "u4_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
      ok "U4 自检 u4_self_${key}=ok"
    else
      bad "U4 自检 u4_self_${key} 没过"
      grep "u4_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
    fi
  done
fi

echo "== 6. 不做项 =="
if git -C "$ROOT" rev-parse --git-dir >/dev/null 2>&1; then
  # 只钉这张卡承诺不动的四份：八块地表、解锁表与世界顺序表。
  # 不按整个 config/ 目录比对——别的卡会改物品表、商店表这些跟地名无关的文件。
  if git -C "$ROOT" diff --quiet -- config/region config/region_unlock.json config/world.json 2>/dev/null; then
    ok "地名 / 邻接 / 章节 / 解锁表一行未改"
  else
    bad "这张卡改了地表或解锁表"
    git -C "$ROOT" diff --name-only -- config/region config/region_unlock.json config/world.json | detail
  fi
else
  echo "  skip 不是 git 仓库，跳过改动比对"
fi

echo "== 7. 真网关段 =="
if [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '  (跳过：没给 MMO_N14_GATEWAY；这一段要有库的机器上跑，例如\n'
  printf '   MMO_N14_GATEWAY=http://127.0.0.1:61000 MMO_N14_TOKEN=<令牌> MMO_N14_CHARACTER=<角色号> \\\n'
  printf '     bash tools/check/check-n14.sh)\n'
else
  resp="$(curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
    "$GATEWAY/world/regions?character_id=$CHARACTER" 2>&1)"
  if printf '%s' "$resp" | grep -q '"reason"' && printf '%s' "$resp" | grep -q '"unlock_stage"'; then
    ok "真网关带角色读得到开放状态：$(printf '%s' "$resp" | head -c 90)…"
  else
    bad "真网关回包缺开放状态：$resp"
  fi
  static="$(curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
    "$GATEWAY/world/regions" 2>&1)"
  if printf '%s' "$static" | grep -q '"regions"'; then
    ok "不带角色号仍回静态表（老屏与老核对不炸）"
  else
    bad "不带角色号的地点表坏了：$static"
  fi
fi

finish "$TITLE"
