#!/usr/bin/env bash
# tools/check/check-u5.sh —— U5 角色养成七屏（开发架构第 14 节第 75 行）。
#
# 完成标准：强化 / 转移 / 结晶结果只读回包，不本地算概率、费用、绑定；
# 失败时未生效数值显示 0。
#
#   bash tools/check/check-u5.sh
#       静态断言 + Unity 编辑器七屏自检（Mac 上跑）
#   MMO_U5_GATEWAY=http://127.0.0.1:61000 MMO_U5_TOKEN=<令牌> \
#     MMO_U5_CHARACTER=<角色号> bash tools/check/check-u5.sh
#       再加真网关段：只读 /inventory 能不能读到
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
GATEWAY="${MMO_U5_GATEWAY:-}"
TOKEN="${MMO_U5_TOKEN:-}"
CHARACTER="${MMO_U5_CHARACTER:-0}"
TITLE="U5（client）"
TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

if [ ! -d "$ROOT/client/MMOGame" ]; then
  echo "  skip 这台机器没有客户端工程（U 卡在 Mac 上跑）"
  finish "$TITLE"
fi

echo "== 1. 静态断言 =="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import os, re
root = os.environ['ROOT']
assets = root + '/client/MMOGame/Assets'
ui = assets + '/HotUpdate/Ui'

names = ['UiUpgradeFlow.cs', 'UiUpgradeCodex.cs', 'UiUpgradeViews.cs', 'UiUpgradeSelfCheck.cs',
         'UiCharacterScreen.cs', 'UiBagScreen.cs', 'UiEnhanceScreen.cs', 'UiTransferScreen.cs',
         'UiCrystalScreen.cs', 'UiSkillScreen.cs', 'UiRealmScreen.cs']
for n in names:
    assert os.path.exists(ui + '/' + n), 'missing ' + n
assert os.path.exists(assets + '/Net/CharacterBag.cs'), 'missing CharacterBag.cs'
assert os.path.exists(assets + '/Editor/U5Check.cs'), 'missing U5Check.cs'

# 界面不读配置、不自己拆 JSON：只吃 Mmo.Net 拆好的回包。
for n in names:
    if not n.endswith('Screen.cs'):
        continue
    text = open(ui + '/' + n, encoding='utf-8').read()
    assert 'config/' not in text, 'config read in ' + n
    assert '"slots"' not in text, 'json parse in ' + n

# 流程复用 Net 的拆装，不在界面层重写。
flow = open(ui + '/UiUpgradeFlow.cs', encoding='utf-8').read()
for token in ('CharacterBag.Parse', 'CharacterEnhance.Parse',
              'CharacterTransfer.Parse', 'CharacterCrystal.Parse'):
    assert token in flow, 'missing ' + token

# 三张结果屏读回包字段，不本地算。
enhance = open(ui + '/UiEnhanceScreen.cs', encoding='utf-8').read()
transfer = open(ui + '/UiTransferScreen.cs', encoding='utf-8').read()
crystal = open(ui + '/UiCrystalScreen.cs', encoding='utf-8').read()
assert '.LevelBefore' in enhance and '.GoldCost' in enhance and '.StoneCost' in enhance
assert '.FromLevel' in transfer and '.GoldCost' in transfer
assert '.Filled' in crystal and '.EnhanceLevel' in crystal and '.Bound' in crystal

# 概率 / 费用不在客户端算。
for n in names:
    if not n.endswith('Screen.cs'):
        continue
    text = open(ui + '/' + n, encoding='utf-8').read()
    assert 'success_rate' not in text, 'rate in ' + n
    assert 'gold_cost' not in text, 'cost in ' + n

# 七屏各有一条 ReadyLog。
for n in names:
    if not n.endswith('Screen.cs'):
        continue
    text = open(ui + '/' + n, encoding='utf-8').read()
    assert 'ReadyLog = "u5_' in text, 'no ready log in ' + n

# 这张卡不动 proto。
proto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read().lower()
assert 'unbind' in proto and 'u5' not in proto

# 总控里已经挂上 U5。
allcards = open(root + '/tools/check/check-all.sh', encoding='utf-8').read()
assert 'u5' in allcards and 'u5)' in allcards, 'check-all 没有 u5'

# U21：觉醒 / 坐骑 / 灵兽三屏（B6 / B4 的界面欠项）。挂养成域，不新开文件域。
u21 = ['UiAwakenScreen.cs', 'UiMountScreen.cs', 'UiSpiritBeastScreen.cs']
for n in u21:
    assert os.path.exists(ui + '/' + n), 'missing ' + n
assert os.path.exists(ui + '/Flow/CompanionFlow.cs'), 'missing CompanionFlow.cs'
assert os.path.exists(assets + '/Net/Companion.cs'), 'missing Companion.cs'

for n in u21:
    text = open(ui + '/' + n, encoding='utf-8').read()
    assert 'ReadyLog = "u21_' in text, 'no u21 ready log in ' + n
    assert 'config/' not in text, 'config read in ' + n
    # 万分比与 bps 只拼单位：不折百分比、不乘系数。
    assert '/ 100' not in text and '* 0.01' not in text, 'local math in ' + n

flow21 = open(ui + '/Flow/CompanionFlow.cs', encoding='utf-8').read()
for token in ('Companion.ParseState', 'Companion.ParseAwaken', 'Companion.ParseMounts',
              'Companion.ParseMountAction', 'Companion.ParseSpiritBeasts'):
    assert token in flow21, 'missing ' + token

hub = open(ui + '/UiEntryHub.cs', encoding='utf-8').read()
for token in ('"awaken"', '"mount"', '"beast"', 'UiAwakenScreen.Build',
              'UiMountScreen.Build', 'UiSpiritBeastScreen.Build'):
    assert token in hub, 'nav missing ' + token

print('u5_static=ok files=%d u21=%d' % (len(names), len(u21)))
PY
then ok "七屏齐全、不读配置、不算概率费用、总控已挂 U5；U21 三屏与六条口齐"
else bad "U5 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器七屏自检 =="
if [ ! -x "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U5Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U5Check.Run 退出码 0"
else
  bad "Unity U5Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files screens no_config enhance_fields transfer_fields crystal_fields flow_net no_proto no_local_math u21_screens self; do
  if grep -q "u5_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u5_${key}=ok"
  else
    bad "编辑器自检 u5_${key} 没过"
    grep "u5_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

# 「七屏都能建」由 u5_self_screens=N 与 u5_screens 两条核（U21 起 N=12）；
# 原表里那个 seven 键从来没有代码发出，是存量假红，U21 一并删掉。
for key in bag realm_display enhance_ok enhance_reject transfer_reject crystal_reject \
           q10_move q10_discard q10_sort q10_use \
           b9_bag_worn b9_equip b9_unequip skin_layout skin_copy \
           u21_awaken_state u21_awaken_ok u21_awaken_body u21_awaken_reject \
           u21_mounts u21_mount_ok u21_mount_body u21_dismount u21_dismount_body u21_mount_reject \
           u21_beasts \
           u21_awaken_skin_layout u21_awaken_skin_copy \
           u21_mount_skin_layout u21_mount_skin_copy \
           u21_beast_skin_layout u21_beast_skin_copy; do
  if grep -q "u5_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "七屏自检 u5_self_${key}=ok"
  else
    bad "七屏自检 u5_self_${key} 没过"
    grep "u5_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 3. 真网关段 =="
if [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '  (跳过：没给 MMO_U5_GATEWAY；这一段要有库的机器上跑，例如\n'
  printf '   MMO_U5_GATEWAY=http://127.0.0.1:61000 MMO_U5_TOKEN=<令牌> MMO_U5_CHARACTER=<角色号> bash tools/check/check-u5.sh)\n'
else
  body="$(curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
    "$GATEWAY/inventory?character_id=$CHARACTER" 2>&1)"
  if printf '%s' "$body" | grep -q '"slots"'; then
    ok "真网关读得到背包：$(printf '%s' "$body" | head -c 80)…"
  else
    bad "真网关没读到背包：$body"
  fi
fi

finish "$TITLE"
