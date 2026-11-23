#!/usr/bin/env bash
# tools/check/check-u14.sh —— U14–U17 四屏界面欠项（开发架构 14.1 末段，任务队列 #40a）。
#
# 完成标准：组队屏只认 GET /party 回包（没回包写「队伍名单还没到」，不拿自己凑一队）；
# 掷点屏只公布 LootAssigned 的归属（winner=0 写「没人要」），意图只有 need / greed / pass；
# 重连屏只按 deserter_remaining_s 走针；世界 Boss 入口屏只读 /world/worldboss 的五个数；
# 四屏切皮肤只换颜色。
#
#   bash tools/check/check-u14.sh
#       静态断言 + Unity 编辑器四屏自检
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
TITLE="U14–U17（client）"
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
import sys

root = os.environ['ROOT']
sys.path.insert(0, root + '/tools/check')
from lib_read import read_text
assets = root + '/client/MMOGame/Assets'
ui = assets + '/HotUpdate/Ui'
net = assets + '/Net'


def read(path):
    return read_text(path)


screens = ['UiPartyRoomScreen.cs', 'UiLootScreen.cs', 'UiReconnectScreen.cs', 'UiWorldBossScreen.cs']
helpers = ['UiPartyFlow.cs', 'UiPartyCodex.cs', 'UiLootFlow.cs', 'UiLootCodex.cs',
           'UiReconnectFlow.cs', 'UiReconnectCodex.cs', 'UiU14SelfCheck.cs']
for name in screens + helpers:
    assert os.path.exists(os.path.join(ui, name)), '缺屏或流程：' + name
assert os.path.exists(os.path.join(net, 'CharacterParty.cs')), '缺 Net 的队伍拆装'

party_net = read(os.path.join(net, 'CharacterParty.cs'))
for path in ['"/party"', '"/party/invite"', '"/party/respond"', '"/party/ready/start"',
             '"/party/ready"', '"/party/leave"', '"/party/disband"']:
    assert path in party_net, '队伍七条口不全：' + path
assert 'txn' not in party_net.lower(), '队伍请求体里出现了事务号'
assert 'Characters' not in party_net, 'Net 层不该接角色名单'

party_screen = read(os.path.join(ui, 'UiPartyRoomScreen.cs'))
assert 'Members.Add' not in party_screen, '组队屏自己造了队员'
assert 'UiPartyCodex.EmptyList' in party_screen, '组队屏没有「队伍名单还没到」这一档'
assert 'UiPartyCodex.Notice' in party_screen

loot_codex = read(os.path.join(ui, 'UiLootCodex.cs'))
loot_flow = read(os.path.join(ui, 'UiLootFlow.cs'))
loot_screen = read(os.path.join(ui, 'UiLootScreen.cs'))
loot_all = loot_codex + loot_flow + loot_screen
for word in ['need', 'greed', 'pass']:
    assert '"%s"' % word in loot_codex, '三个词缺：' + word
assert 'Nobody' in loot_codex and 'WinnerCharacterId == 0' in loot_codex, '没人要没按 winner=0 判'
assert 'SendLootIntentAsync' in loot_flow, '表态没走 SendLootIntentAsync'
assert 'Random' not in loot_all, '掷点屏出现了随机'
assert not re.search(r'\bRoll\s*\(', loot_all), '掷点屏自己算点'
assert not re.search(r'roll_[a-z]', loot_all, re.I), '掷点屏出现了掷点区间字段'

client = read(assets + '/HotUpdate/BattleClient.cs')
assert 'Ui.UiLootFlow.Feed(message.LootAssigned)' in client, 'LootAssigned 没交给掷点屏'

reconnect_codex = read(os.path.join(ui, 'UiReconnectCodex.cs'))
reconnect_flow = read(os.path.join(ui, 'UiReconnectFlow.cs'))
reconnect_screen = read(os.path.join(ui, 'UiReconnectScreen.cs'))
reconnect_all = reconnect_codex + reconnect_flow + reconnect_screen
assert 'DeserterRemainingS' in reconnect_flow, '重连屏没读 deserter_remaining_s'
assert 'Time.deltaTime' in reconnect_flow, '倒计时没按帧走针'
assert 'DateTime' not in reconnect_all and 'UtcNow' not in reconnect_all, '重连屏拿时钟算了剩余时间'
assert 'ReconnectAsync' in reconnect_flow, '重连没走 BattleLink.ReconnectAsync'

dungeon_net = read(os.path.join(net, 'DungeonEntry.cs'))
assert 'deserter_remaining_s' in dungeon_net, '进本回包没拆 deserter_remaining_s'

boss = read(os.path.join(ui, 'UiWorldBossScreen.cs'))
for field in ['facts.Current', 'facts.Cap', 'facts.Remaining', 'facts.HoldSeconds',
              'facts.RenewSeconds', 'facts.LostSeconds']:
    assert field in boss, '世界 Boss 入口屏少了：' + field
assert 'flow.LoadBoss' in boss and 'WorldBossEntry.OwnerLine' in boss, '入口屏没走只读入口'
assert 'DateTime' not in boss and 'TimeZone' not in boss, '入口屏自己折了时区'

hub = read(os.path.join(ui, 'UiEntryHub.cs'))
for build in ['UiPartyRoomScreen.Build', 'UiLootScreen.Build',
              'UiReconnectScreen.Build', 'UiWorldBossScreen.Build']:
    assert build in hub, '四屏没挂进入口导航：' + build

gateway = read(root + '/src/Gateway/PartyApi.cs')
assert 'loot_mode = reply.LootMode' in gateway and 'roll_max = reply.RollMax' in gateway, \
    '网关 /party 没带分配口径（U14 要照抄这一格）'

proto = read(root + '/proto/mmo/battle.proto')
assert 'message LootAssigned' in proto and 'u14' not in proto, '这张卡不该动 proto'

allcards = read(root + '/tools/check/check-all.sh')
assert 'u14)' in allcards, 'check-all 没有 u14'

print('u14_static=ok screens=%d files=%d' % (len(screens), len(screens) + len(helpers) + 1))
PY
then ok "四屏齐全、只读回包、不掷点不算时间、总控已挂 U14"
else bad "U14–U17 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器四屏自检 =="
if [ ! -x "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U14Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U14Check.Run 退出码 0"
else
  bad "Unity U14Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files wired_entries party_paths party_no_txn party_flow_net party_no_self_member \
           party_gateway_loot_facts loot_three_words loot_no_roll_math loot_send_path loot_feed \
           loot_owner_from_reply reconnect_fact_source reconnect_no_clock reconnect_async \
           boss_read_only boss_five_numbers boss_no_timezone boss_killed_state self; do
  if grep -q "u14_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u14_${key}=ok"
  else
    bad "编辑器自检 u14_${key} 没过"
    grep "u14_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

for key in party_pending party_facts party_loot_mode loot_rows loot_three_words \
           reconnect_fact reconnect_tick reconnect_cleared reconnect_no_fact \
           boss_facts boss_killed skin_layout skin_copy; do
  if grep -q "u14_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "四屏自检 u14_self_${key}=ok"
  else
    bad "四屏自检 u14_self_${key} 没过"
    grep "u14_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

for key in u14_party_room=ready u15_loot=ready u16_reconnect=ready u17_world_boss=ready; do
  if grep -q "$key" "$TMP/unity.log" 2>/dev/null; then
    ok "屏就绪 $key"
  else
    bad "屏没念就绪行：$key"
  fi
done

finish "$TITLE"
