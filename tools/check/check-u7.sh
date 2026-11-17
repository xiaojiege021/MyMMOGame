#!/usr/bin/env bash
# tools/check/check-u7.sh —— U7 社交与组织五屏（开发架构第 14 节第 77 行）。
#
# 完成标准：五屏齐全可建；名册 / 等级 / 上限 / 席位只认 /sect/* 回包，没回包写「尚未请求」；
# 城战同屏只按 SiegeView 翻译（超限只留队色与血条）；归属只读 /world/worldboss 的
# owner_sect_name；竞技场与战场不本地算战力；家园摆放 / 种植 / 图鉴都接了回包；
# 切皮肤只换颜色。
#
#   bash tools/check/check-u7.sh
#       静态断言 + Unity 编辑器五屏自检
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
TITLE="U7（client）"
TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

if [ ! -d "$ROOT/client/MMOGame" ]; then
  echo "  skip 这台机器没有客户端工程（U 卡在 Mac 上跑）"
  finish "$TITLE"
fi

echo "== 1. 静态断言 =="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import os
import re

root = os.environ['ROOT']
assets = root + '/client/MMOGame/Assets'
ui = assets + '/HotUpdate/Ui'
net = assets + '/Net'

screens = ['UiSectScreen.cs', 'UiSiegeScreen.cs', 'UiArenaScreen.cs',
           'UiHomesteadScreen.cs', 'UiHeroCodexScreen.cs']
others = ['UiSocialFlow.cs', 'UiSocialCodex.cs', 'UiSocialSelfCheck.cs']
for n in screens + others:
    assert os.path.exists(ui + '/' + n), 'missing ' + n
for n in ('SectGuild.cs', 'WorldBossEntry.cs'):
    assert os.path.exists(net + '/' + n), 'missing ' + n
assert os.path.exists(assets + '/Editor/U7Check.cs'), 'missing U7Check.cs'

cap_assign = re.compile(r'(MemberCap|SeatCap|MemberCount|SeatCount|FullModelLimit)\s*=\s*\d')
for n in screens:
    text = open(ui + '/' + n, encoding='utf-8').read()
    assert 'ReadyLog = "u7_' in text, 'no ready log in ' + n
    assert 'config/' not in text, 'config read in ' + n
    assert 'sect.json' not in text and 'siege.json' not in text, 'config name in ' + n
    assert 'JsonField.Of' not in text, 'json parse in ' + n
    assert not cap_assign.search(text), 'hardcoded cap in ' + n

arena = open(ui + '/UiArenaScreen.cs', encoding='utf-8').read()
assert '1.3' not in arena and '0.7' not in arena, 'power math in arena screen'
# U12 接了 N5 的两条进场口。这里只认「接线后的入口在不在」，不写「无匹配入口」这种会过期的话。
assert '进竞技场' in arena and 'EnterBattlefield' in arena, 'arena screen 缺进场入口'

codex = open(ui + '/UiSocialCodex.cs', encoding='utf-8').read()
assert 'config/' not in codex, 'config read in codex'
assert not cap_assign.search(codex), 'hardcoded cap in codex'

flow = open(ui + '/UiSocialFlow.cs', encoding='utf-8').read()
for token in ('SectGuild.ParseSect', 'SectGuild.ParsePosition', 'SectGuild.ParseLeadership',
              'SectGuild.ParseUpgrade', 'WorldBossEntry.Parse'):
    assert token in flow, 'missing ' + token
assert 'SiegeViewTiers.For' in flow, '城战档位没走 SiegeViewTiers'

sect = open(ui + '/UiSectScreen.cs', encoding='utf-8').read()
assert '.MemberCap' in sect and '.SeatCap' in sect, '宗门屏没读回包字段'
assert '尚未请求' in sect, '没回包时没写尚未请求'

siege = open(ui + '/UiSiegeScreen.cs', encoding='utf-8').read()
assert '.SiegeFullCount' in siege and '.SiegeFullLimit' in siege and 'Markers()' in siege

home = open(ui + '/UiHomesteadScreen.cs', encoding='utf-8').read()
hero = open(ui + '/UiHeroCodexScreen.cs', encoding='utf-8').read()
social_codex = open(ui + '/UiSocialCodex.cs', encoding='utf-8').read()
# U9 接了摆放、U10 接了种植与家园币、U11 接了图鉴。这里只认「接线后的入口在不在」，
# 不写「已接 / 未接」这种会过期的话。
assert '读取种植' in home and '读取图鉴' in hero
assert 'PlantingGap' not in social_codex and 'CodexGap' not in social_codex
assert 'VersusGap' not in social_codex, '竞技场缺口文案还留着'

# 战斗侧只加一条「收到才处理」的分支；副本链路不发这条消息。
battle_client = open(assets + '/HotUpdate/BattleClient.cs', encoding='utf-8').read()
assert 'BodyOneofCase.SiegeView' in battle_client, '战斗客户端缺 SiegeView 分支'
assert 'UiSocialFlow.Feed' in battle_client, 'SiegeView 没交给社交屏'

for proto in ('social.proto', 'battle.proto'):
    text = open(root + '/proto/mmo/' + proto, encoding='utf-8').read().lower()
    assert 'u7' not in text, '这张卡不该动 proto：' + proto

allcards = open(root + '/tools/check/check-all.sh', encoding='utf-8').read()
assert 'u7)' in allcards, 'check-all 没有 u7'

print('u7_static=ok screens=%d files=%d' % (len(screens), len(screens) + len(others) + 3))
PY
then ok "五屏齐全、不读配置、不写死上限、总控已挂 U7"
else bad "U7 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器五屏自检 =="
if [ ! -x "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U7Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U7Check.Run 退出码 0"
else
  bad "Unity U7Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files screens no_config no_hardcoded_cap no_power_math codex_clean \
           sect_fields siege_fields wired_entries flow_net flow_siege no_proto self; do
  if grep -q "u7_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u7_${key}=ok"
  else
    bad "编辑器自检 u7_${key} 没过"
    grep "u7_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

for key in pending facts_sect facts_seat facts facts_second no_hardcoded \
           siege_tiers siege_markers siege_screen owner owner_empty owner_screen \
           five wired skin_layout skin_copy; do
  if grep -q "u7_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "五屏自检 u7_self_${key}=ok"
  else
    bad "五屏自检 u7_self_${key} 没过"
    grep "u7_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

# 五屏各自念一行，便于真机抓。
for key in sect siege arena homestead codex; do
  if grep -q "u7_${key}_screen=ready" "$TMP/unity.log" 2>/dev/null; then
    ok "屏就绪 u7_${key}_screen=ready"
  else
    bad "屏没念就绪行：u7_${key}_screen"
  fi
done

finish "$TITLE"
