#!/usr/bin/env bash
# U1 核对：共用界面骨架与双风格（开发架构第 14 节第 71 行）。
#
# 完成标准（第 71 行 + 17.5）：
#   同一套控件切两套皮肤，偏好只存玩家本地；config/ 全无美术字段、
#   客户端无 Resources.Load、文件名即 AB 地址且 .meta GUID 32 位；
#   真机 P0-3c 的 59 项不回退。
#
# 这里跑客户端段：静态断言 + Unity 编辑器自检（切皮肤前后布局与文案不变、
# 颜色必须变、偏好落盘、按钮命中）。真机 59 项属于依赖闭包，按 17.4 用
#   bash tools/check/check-all.sh --card u1
# 跑（P0-3c 会整条链一起回归）。
#
# 用法：bash tools/check/check-u1.sh
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
TMP="$(mktemp -d)"
TITLE="U1（client）"

cleanup() {
  rm -rf "$TMP"
}
trap cleanup EXIT

# 第 16 节：笔记本只同步服务端，没有 client/MMOGame。缺工程时明确跳过，
# 别把「环境不具备」报成回归失败。
if [ ! -d "$ROOT/client/MMOGame" ]; then
  echo "  skip 这台机器没有客户端工程（笔记本只同步服务端，U 卡在 Mac 上跑）"
  finish "$TITLE"
fi

echo "== 1. 骨架静态断言 =="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import glob, os, re
root = os.environ['ROOT']
assets = root + '/client/MMOGame/Assets'
ui = assets + '/HotUpdate/Ui'

names = ['UiPalette.cs', 'UiSkinStore.cs', 'UiFont.cs', 'UiMetrics.cs',
         'UiControls.cs', 'UiShell.cs', 'UiStatusScreen.cs', 'UiReasonCatalog.cs',
         'UiSelfCheck.cs']
for n in names:
    assert os.path.exists(ui + '/' + n), 'missing ' + n

# 客户端不许出现 Resources.Load：资源一律走 AB，文件名即地址。
for p in glob.glob(assets + '/**/*.cs', recursive=True):
    assert 'Resources.Load' not in open(p, encoding='utf-8').read(), 'Resources.Load in ' + p

# 美术资源不进 Resources 目录。
for p in glob.glob(assets + '/**/Resources', recursive=True):
    assert '/Art/' not in p, 'Resources dir under Art: ' + p

# 手写 .meta 的 GUID 必须正好 32 位十六进制。
metas = glob.glob(ui + '/*.meta') + glob.glob(assets + '/Editor/U1*.meta')
assert len(metas) >= 10, 'metas=%d' % len(metas)
for p in metas:
    m = re.search(r'guid:\s*([0-9a-fA-F]+)', open(p, encoding='utf-8').read())
    assert m and len(m.group(1)) == 32, 'bad guid: ' + os.path.basename(p)

# config/ 里不许有美术字段：换资源不动配置，也不动服务端判定。
# items.json 里的 "material" 是物品分类（材料），不是美术材质，不列在这里。
art = ['prefab', 'model', 'effect', 'anim', 'animation', 'icon', 'texture',
       'sprite', 'skin', 'theme', 'shader', 'mesh']
for p in glob.glob(root + '/config/**/*.json', recursive=True):
    text = open(p, encoding='utf-8').read().lower()
    for key in art:
        assert '"%s"' % key not in text, p + ' has ' + key

# 皮肤偏好只在玩家本地：全客户端只有 UiSkinStore 碰 PlayerPrefs。
holders = sorted(os.path.basename(p) for p in glob.glob(assets + '/**/*.cs', recursive=True)
                 if 'PlayerPrefs' in open(p, encoding='utf-8').read())
assert holders == ['UiSkinStore.cs'], 'PlayerPrefs in ' + ','.join(holders)
store = open(ui + '/UiSkinStore.cs', encoding='utf-8').read()
assert 'Key = "mmo.ui.skin"' in store
assert 'LightValue = "light"' in store and 'DarkValue = "dark"' in store

# 风格不进协议。
for p in glob.glob(root + '/proto/**/*.proto', recursive=True):
    text = open(p, encoding='utf-8').read().lower()
    assert 'skin' not in text and 'theme' not in text, p

# reason 词表只展示，不造词：每个都要在服务端代码里真实存在。
catalog = open(ui + '/UiReasonCatalog.cs', encoding='utf-8').read()
tokens = re.findall(r'"([a-z][a-z_]+)"', catalog)
assert len(tokens) == 36, 'tokens=%d' % len(tokens)
server = ''
for p in glob.glob(root + '/src/**/*.cs', recursive=True):
    server += open(p, encoding='utf-8').read()
missing = [t for t in tokens if '"%s"' % t not in server]
assert not missing, 'not in server: ' + ','.join(missing)

# 骨架不许自己发明色值：颜色只有 UiPalette 一份正本。
for p in glob.glob(ui + '/*.cs'):
    if os.path.basename(p) == 'UiPalette.cs':
        continue
    text = open(p, encoding='utf-8').read()
    assert not re.search(r'#[0-9a-fA-F]{6}\b', text), 'hex color in ' + os.path.basename(p)

print('u1_static=ok files=%d metas=%d tokens=%d' % (len(names), len(metas), len(tokens)))
PY
then ok "骨架文件齐、无 Resources.Load、meta 32 位、config 无美术字段、偏好只在本地"
else bad "骨架静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器自检：切皮肤只换颜色 =="
if [ ! -x "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U1Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U1Check.Run 退出码 0"
else
  bad "Unity U1Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files palette local config assets reason self; do
  if grep -q "u1_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u1_${key}=ok"
  else
    bad "编辑器自检 u1_${key} 没过"
    grep "u1_${key}" "$TMP/unity.log" 2>/dev/null | tail -3 | detail
  fi
done

for key in default page layout copy color persist reset hit; do
  if grep -q "u1_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "骨架自检 u1_self_${key}=ok"
  else
    bad "骨架自检 u1_self_${key} 没过"
    grep "u1_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

finish "$TITLE"
