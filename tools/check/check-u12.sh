#!/usr/bin/env bash
# tools/check/check-u12.sh —— U12 竞技场与战场进场屏（开发架构第 14 节第 95 行）。
#
# 完成标准：F03 接 POST /versus/arena/enter 与 POST /versus/battlefield/enter；
# 展示回包里的进场路径与模式事实，不本地算战力、不造匹配算法。
#
#   bash tools/check/check-u12.sh
#       静态断言 + Unity 编辑器自检（进场 / 请求体 / 人数 / ok=false / 非 200 / 战场 / 切皮肤）
#       3b 段是 U19（B11 界面欠项）：段位屏与排行榜屏三条口的接线、请求体、不算分不排序
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
TITLE="U12（client）"
TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

if [ ! -d "$ROOT/client/MMOGame" ]; then
  echo "  skip 这台机器没有客户端工程（U 卡在 Mac 上跑）"
  finish "$TITLE"
fi

echo "== 1. 静态断言 =="
PYBIN=""
for c in python3 python; do
  if command -v "$c" >/dev/null 2>&1; then PYBIN="$c"; break; fi
done
if [ -z "$PYBIN" ]; then
  bad "找不到 python3 / python"
  finish "$TITLE"
fi
if ROOT="$ROOT" "$PYBIN" "$ROOT/tools/check/check-u12-static.py" >"$TMP/static.out" 2>&1; then
  ok "两条进场口接线、缺口文案已去、总控已挂 U12"
else
  bad "U12 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器自检 =="
if [ ! -x "$UNITY" ] && [ ! -f "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U12Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U12Check.Run 退出码 0"
else
  bad "Unity U12Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files net_parse flow_versus screen_wired codex_wired no_power_math no_config no_proto self; do
  if grep -q "u12_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u12_${key}=ok"
  else
    bad "编辑器自检 u12_${key} 没过"
    grep "u12_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 3. 屏上自检 =="
for key in pending arena body_only no_local_multiply reason reject battlefield skin_layout skin_copy; do
  if grep -q "u12_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "进场自检 u12_self_${key}=ok"
  else
    bad "进场自检 u12_self_${key} 没过"
    grep "u12_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 3b. U19 段位与排行榜（B11 界面欠项）=="
NET_RANK="$ROOT/client/MMOGame/Assets/Net/ArenaRank.cs"
FLOW_RANK="$ROOT/client/MMOGame/Assets/HotUpdate/Ui/Flow/ArenaRankFlow.cs"
RANK_SCREEN="$ROOT/client/MMOGame/Assets/HotUpdate/Ui/UiArenaRankScreen.cs"
BOARD_SCREEN="$ROOT/client/MMOGame/Assets/HotUpdate/Ui/UiArenaBoardScreen.cs"
for f in "$NET_RANK" "$FLOW_RANK" "$RANK_SCREEN" "$BOARD_SCREEN" \
         "$ROOT/config/ui_text/UiArenaRankScreen.json" \
         "$ROOT/config/ui_text/UiArenaBoardScreen.json" \
         "$ROOT/config/ui_text/ArenaRankFlow.json"; do
  if [ -f "$f" ]; then
    ok "有 $(basename "$f")"
  else
    bad "缺 $(basename "$f")"
  fi
done

# 三条口与网关一致；领外观的请求体只有角色号与档位键（事务号网关生成）。
if grep -q '"/arena/rank"' "$NET_RANK" && grep -q '"/arena/leaderboard"' "$NET_RANK" \
   && grep -q '"/arena/appearance/claim"' "$NET_RANK" \
   && grep -q 'MapGet("/arena/rank"' "$ROOT/src/Gateway/ArenaRankApi.cs" \
   && grep -q 'MapGet("/arena/leaderboard"' "$ROOT/src/Gateway/ArenaRankApi.cs" \
   && grep -q 'MapPost("/arena/appearance/claim"' "$ROOT/src/Gateway/ArenaRankApi.cs"; then
  ok "三条口与网关路径一致（rank / leaderboard / appearance/claim）"
else
  bad "段位与排行榜的路径与网关不一致"
fi

if grep -q 'character_id' "$NET_RANK" && grep -q 'tier_key' "$NET_RANK" \
   && ! grep -q 'txn_id' "$NET_RANK"; then
  ok "领外观请求体只有角色号与档位键（事务号网关生成，客户端不带）"
else
  bad "领外观请求体里混进了客户端不该带的东西"
  grep -n 'txn_id' "$NET_RANK" | head -3 | detail
fi

# 计算只在服务端：屏上不许拿分数与门槛相减、不许排序、不许自己截人数。
if ! grep -qE 'MinScore *-|Score *-|\.Sort\(|OrderBy|Linq' "$RANK_SCREEN" "$BOARD_SCREEN"; then
  ok "屏上不算分、不排序、不自己截人数（名次与档位都照抄回包）"
else
  bad "屏上出现了本地算分或排序"
  grep -nE 'MinScore *-|Score *-|\.Sort\(|OrderBy|Linq' "$RANK_SCREEN" "$BOARD_SCREEN" | head -3 | detail
fi

if ! grep -q 'config/' "$NET_RANK" "$FLOW_RANK" "$RANK_SCREEN" "$BOARD_SCREEN"; then
  ok "两屏与拆装件都不读配置表（不打开 arena_rank.json）"
else
  bad "这张卡读了配置表"
  grep -n 'config/' "$NET_RANK" "$FLOW_RANK" "$RANK_SCREEN" "$BOARD_SCREEN" | head -3 | detail
fi

if grep -q 'UiArenaRankScreen.Build' "$ROOT/client/MMOGame/Assets/HotUpdate/Ui/UiEntryHub.cs" \
   && grep -q 'UiArenaBoardScreen.Build' "$ROOT/client/MMOGame/Assets/HotUpdate/Ui/UiEntryHub.cs"; then
  ok "社交二级条挂上两屏（段位 / 排行）"
else
  bad "导航里没挂上段位屏或排行榜屏"
fi

if ! grep -qi 'u19' "$ROOT/proto/mmo/character.proto" "$ROOT/proto/mmo/battle.proto"; then
  ok "proto 没被这张卡动过"
else
  bad "proto 里出现了 u19 标记"
fi

for key in rank_ok rank_tiers claim_ok claim_body claim_reject \
           board_ok board_order board_reject board_status rank_skin_layout rank_skin_copy; do
  if grep -q "u12_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "段位与排行自检 u12_self_${key}=ok"
  else
    bad "段位与排行自检 u12_self_${key} 没过"
    grep "u12_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 4. 屏就绪 =="
if grep -q "u7_arena_screen=ready" "$TMP/unity.log" 2>/dev/null; then
  ok "屏就绪 u7_arena_screen=ready"
else
  bad "竞技场屏没念就绪行"
fi
if grep -q "u12_versus_wired=ready" "$TMP/unity.log" 2>/dev/null; then
  ok "接线行 u12_versus_wired=ready"
else
  bad "没有接线就绪行"
fi

echo "== 5. 接口仍在（D5：只钉入口存在，不钉整目录未改） =="
assert_interface src/Gateway/VersusApi.cs 'MapPost\("/versus/arena/enter"' "竞技场进场口在网关"
assert_interface src/Gateway/VersusApi.cs 'MapPost\("/versus/battlefield/enter"' "战场进场口在网关"
assert_interface client/MMOGame/Assets/HotUpdate/Ui/UiArenaScreen.cs \
  'VersusEntry\.SummaryLine' "屏仍按 Net 拆装件摆进场卡"
assert_interface client/MMOGame/Assets/HotUpdate/Ui/UiSocialFlow.cs 'EnterArena' "Flow 仍有竞技场进场入口"
assert_interface client/MMOGame/Assets/Net/VersusEntry.cs 'ArenaPath' "Net 拆装件仍带竞技场路径"

finish "$TITLE"
