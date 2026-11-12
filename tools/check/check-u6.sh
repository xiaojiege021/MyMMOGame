#!/usr/bin/env bash
# tools/check/check-u6.sh —— U6 经济与商城四屏（开发架构第 14 节第 76 行）。
#
# 完成标准：拍卖只展示标价与回包里的税 / 净额，不本地算税；清单外商品被拒时元宝不变
# 且不发货；绑定态由回包决定。
#
#   bash tools/check/check-u6.sh
#       静态断言 + Unity 编辑器四屏自检（Mac 上跑）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
TITLE="U6（client）"
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
root = os.environ['ROOT']
assets = root + '/client/MMOGame/Assets'
ui = assets + '/HotUpdate/Ui'
net = assets + '/Net'

names = ['UiCommerceFlow.cs', 'UiCommerceCodex.cs', 'UiCommerceSelfCheck.cs',
         'UiShopScreen.cs', 'UiAuctionScreen.cs', 'UiCurrencyScreen.cs', 'UiPassScreen.cs']
for n in names:
    assert os.path.exists(ui + '/' + n), 'missing ' + n
for n in ('CharacterShop.cs', 'CharacterCurrency.cs', 'CharacterRecharge.cs'):
    assert os.path.exists(net + '/' + n), 'missing ' + n
assert os.path.exists(assets + '/Editor/U6Check.cs'), 'missing U6Check.cs'

for n in names:
    if not n.endswith('Screen.cs'):
        continue
    text = open(ui + '/' + n, encoding='utf-8').read()
    assert 'config/' not in text, 'config read in ' + n
    assert '"balances"' not in text, 'json parse in ' + n
    assert 'tax_rate' not in text and '0.05' not in text and '0.08' not in text, 'tax math in ' + n
    assert 'ReadyLog = "u6_' in text, 'no ready log in ' + n

flow = open(ui + '/UiCommerceFlow.cs', encoding='utf-8').read()
for token in ('CharacterShop.Parse', 'CharacterCurrency.Parse',
              'CharacterAuction.Parse', 'CharacterRecharge.ParseOrder',
              'CharacterRecharge.ParseSettle'):
    assert token in flow, 'missing ' + token

shop = open(ui + '/UiShopScreen.cs', encoding='utf-8').read()
auction = open(ui + '/UiAuctionScreen.cs', encoding='utf-8').read()
currency = open(ui + '/UiCurrencyScreen.cs', encoding='utf-8').read()
assert '.ChargedYuanbao' in shop and '.Delivered' in shop and '.Bound' in shop
assert '.TaxGold' in auction and '.NetGold' in auction
assert '.Balance' in currency and '.Duplicate' in currency

proto = open(root + '/proto/mmo/character.proto', encoding='utf-8').read().lower()
assert 'u6' not in proto

allcards = open(root + '/tools/check/check-all.sh', encoding='utf-8').read()
assert 'u6' in allcards and 'u6)' in allcards, 'check-all 没有 u6'

print('u6_static=ok files=%d' % len(names))
PY
then ok "四屏齐全、不读配置、不算税、总控已挂 U6"
else bad "U6 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器四屏自检 =="
if [ ! -x "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U6Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U6Check.Run 退出码 0"
else
  bad "Unity U6Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files screens no_config no_tax_math shop_fields auction_fields currency_fields flow_net no_proto self; do
  if grep -q "u6_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u6_${key}=ok"
  else
    bad "编辑器自检 u6_${key} 没过"
    grep "u6_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

for key in currency shop_ok shop_reject auction ingot recharge duplicate four skin_layout skin_copy; do
  if grep -q "u6_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "四屏自检 u6_self_${key}=ok"
  else
    bad "四屏自检 u6_self_${key} 没过"
    grep "u6_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

finish "$TITLE"
