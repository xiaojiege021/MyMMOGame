#!/usr/bin/env bash
# tools/check/check-u13.sh —— U13 段内四阶屏（开发架构第 14 节第 97 行）。
#
# 完成标准：U5 境界入口只读子阶名与 passive_pct_bps；升阶按钮只 POST 网关事务号；
# 不本地算门槛；客户端没有第二份阶名表；不动 proto / config / 服务端。
#
#   bash tools/check/check-u13.sh
#       静态断言 + Unity 编辑器 U13Check.Run + 屏自检（尚未请求 / 只读 / 升成 / 被拒 / 请求体）
#   MMO_U13_GATEWAY=http://127.0.0.1:61000 MMO_U13_TOKEN=<令牌> \
#     MMO_U13_CHARACTER=<角色号> bash tools/check/check-u13.sh
#       再加真网关段：读一次 GET /realm/phase，看回包的 phase_name 与 passive_pct_bps
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
GATEWAY="${MMO_U13_GATEWAY:-}"
TOKEN="${MMO_U13_TOKEN:-}"
CHARACTER="${MMO_U13_CHARACTER:-0}"
TITLE="U13（client）"
TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 静态断言 =="
PYBIN=""
for c in python3 python; do
  if command -v "$c" >/dev/null 2>&1; then PYBIN="$c"; break; fi
done
if [ -z "$PYBIN" ]; then
  bad "找不到 python3 / python"
  finish "$TITLE"
fi
if ROOT="$ROOT" "$PYBIN" "$ROOT/tools/check/check-u13-static.py" >"$TMP/static.out" 2>&1; then
  ok "两条口挂上、只读回包、无阶名表、无本地门槛、总控已挂 U13"
else
  bad "U13 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器自检 =="
if [ ! -d "$ROOT/client/MMOGame" ]; then
  echo "  skip 这台机器没有客户端工程（U 卡在 Mac 上跑）"
  finish "$TITLE"
fi
if [ ! -x "$UNITY" ] && [ ! -f "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U13Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U13Check.Run 退出码 0"
else
  bad "Unity U13Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files net_parse net_paths net_body_no_txn flow_actions screen_wired \
           no_static_ladder no_advance_button no_config no_phase_table no_proto self; do
  if grep -q "u13_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u13_${key}=ok"
  else
    bad "编辑器自检 u13_${key} 没过"
    grep "u13_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

# 自检一条都没打出来时，上面的循环会把 11 条全记成 FAIL，这里再兜一次
# 「一行 ok 都没有」的编译失败情形（别把编不过报成断言失败）。
if ! grep -q "u13_all=" "$TMP/unity.log" 2>/dev/null; then
  bad "Unity 日志里没有 u13_all=，多半是编译失败"
  grep -E "error CS|Compilation failed" "$TMP/unity.log" 2>/dev/null | head -5 | detail
fi

echo "== 3. 屏上自检（U13 那四条）=="
for key in realm_pending realm_phase promote_ok promote_reject promote_body; do
  if grep -q "u5_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "境界自检 u5_self_${key}=ok"
  else
    bad "境界自检 u5_self_${key} 没过"
    grep "u5_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 4. 不做项 =="
# 这一条守的是「境界子阶这条链不许改配置与玩法」：只看境界 / 职业那两份配置；
# 别的卡会改物品表、商店表这些跟境界无关的文件，不按整个 config/ 目录比对。
# proto 与 src 同理（N14 就动了 world.proto），改成断言本卡依赖的两条 rpc 与网关路径仍在。
if git -C "$ROOT" rev-parse --git-dir >/dev/null 2>&1; then
  if git -C "$ROOT" diff --quiet -- config/realms.json config/classes.json 2>/dev/null; then
    ok "境界 / 职业配置一行未改"
  else
    bad "这张卡改了境界或职业配置"
    git -C "$ROOT" diff --name-only -- config/realms.json config/classes.json | detail
  fi
else
  echo "  skip 不是 git 仓库，跳过改动比对"
fi
if grep -q "rpc PromoteRealmPhase" "$ROOT/proto/mmo/character.proto" \
   && grep -q "rpc DescribeRealmPassive" "$ROOT/proto/mmo/character.proto" \
   && grep -q 'MapGet("/realm/phase"' "$ROOT/src/Gateway/RealmPhaseApi.cs" \
   && grep -q 'MapPost("/realm/phase/promote"' "$ROOT/src/Gateway/RealmPhaseApi.cs"; then
  ok "境界子阶两条 rpc 与网关两条路径都在（proto / 服务端没被这条链改掉）"
else
  bad "境界子阶的 rpc 或网关路径不见了"
fi

echo "== 5. 真网关段 =="
if [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '  (跳过：没给 MMO_U13_GATEWAY；这一段要有库的机器上跑，例如\n'
  printf '   MMO_U13_GATEWAY=http://127.0.0.1:61000 MMO_U13_TOKEN=<令牌> MMO_U13_CHARACTER=<角色号> \\\n'
  printf '     bash tools/check/check-u13.sh)\n'
else
  resp="$(curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
    "$GATEWAY/realm/phase?character_id=$CHARACTER" 2>&1)"
  if printf '%s' "$resp" | grep -q '"phase_name"'; then
    ok "真网关读得到段内阶：$(printf '%s' "$resp" | head -c 100)…"
    if printf '%s' "$resp" | grep -q '"passive_pct_bps"'; then
      ok "回包带 passive_pct_bps"
    else
      bad "回包没有 passive_pct_bps：$resp"
    fi
  else
    bad "真网关没读到段内阶：$resp"
  fi
fi

finish "$TITLE"
