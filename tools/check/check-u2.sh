#!/usr/bin/env bash
# U2 核对：入口与账号 4 屏（开发架构第 14 节第 72 行）。
#
# 完成标准（第 72 行）：
#   错误口令 / 无网络 / 503 在屏上分得开，界面不本地判登录；
#   创角只列五职业与四种出身，不列十条分支；载入进度不决定能否进游戏。
#
# 这里跑三段：
#   1. 静态断言：四屏文件齐、出身与职业对得上 config、四句失败文案照抄原型、
#      没有本地判口令、建角分支为空、载入进度有硬上限、无同步资源加载、meta 32 位。
#   2. Unity 编辑器自检：四屏真搭起来跑一遍，回包靠替换发送实现喂进去。
#   3. 真网关段（可选）：给了 MMO_U2_GATEWAY 就用 curl 打真网关，
#      把真状态码与真回包喂给同一个 AccountEntry 拆装。
#
# 真机 P0-3c 的 59 项属于依赖闭包，按 17.4 用
#   bash tools/check/check-all.sh --card u2
# 跑（客户端代码变了要先重新出包）。
#
# 用法：
#   bash tools/check/check-u2.sh
#   MMO_U2_GATEWAY=http://127.0.0.1:61000 bash tools/check/check-u2.sh
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE="$ROOT/tools/probe/EntryProbe"
TMP="$(mktemp -d)"
TITLE="U2（client）"

cleanup() {
  rm -rf "$TMP"
}
trap cleanup EXIT

# 第 16 节：笔记本只同步服务端，没有 client/MMOGame。缺工程时跳过前两段，
# 但真网关段不依赖客户端工程，给了 MMO_U2_GATEWAY 就照样跑。
HAVE_CLIENT=1
if [ ! -d "$ROOT/client/MMOGame" ]; then
  HAVE_CLIENT=0
  echo "  skip 这台机器没有客户端工程（静态段与 Unity 段在 Mac 上跑）"
fi

if [ "$HAVE_CLIENT" = "1" ]; then

echo "== 1. 四屏静态断言 =="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import glob, json, os, re
root = os.environ['ROOT']
assets = root + '/client/MMOGame/Assets'
ui = assets + '/HotUpdate/Ui'

def read(p):
    return open(p, encoding='utf-8').read()

names = ['UiEntryCodex.cs', 'UiTextField.cs', 'UiOption.cs', 'UiEntryFlow.cs',
         'UiLoginScreen.cs', 'UiCreateScreen.cs', 'UiRosterScreen.cs', 'UiLoadingScreen.cs',
         'UiEntrySelfCheck.cs']
for n in names:
    assert os.path.exists(ui + '/' + n), 'missing ' + n
assert os.path.exists(assets + '/Net/AccountEntry.cs'), 'missing AccountEntry.cs'
for n in ('U2Check.cs', 'U2Preview.cs'):
    assert os.path.exists(assets + '/Editor/' + n), 'missing ' + n

codex = read(ui + '/UiEntryCodex.cs')
flow = read(ui + '/UiEntryFlow.cs')
entry = read(assets + '/Net/AccountEntry.cs')
create = read(ui + '/UiCreateScreen.cs')
loading = read(ui + '/UiLoadingScreen.cs')

# 出身与职业必须与 config 正本对得上：编号、名字、出身对白、外观四样都在。
races = json.loads(read(root + '/config/races.json'))['races']
classes = json.loads(read(root + '/config/classes.json'))['classes']
assert len(races) == 4, 'config races=%d' % len(races)
assert len(classes) == 5, 'config classes=%d' % len(classes)
for r in races:
    assert 'Id = "%s"' % r['id'] in codex, 'race id ' + r['id']
    assert r['name'] in codex, 'race name ' + r['name']
    assert r['origin'] in codex, 'race origin ' + r['id']
    body = r['appearance']['body']
    assert '\\"body\\":\\"%s\\"' % body in codex, 'appearance ' + r['id']
for c in classes:
    assert 'Id = "%s"' % c['id'] in codex, 'class id ' + c['id']
    assert c['name'] in codex, 'class name ' + c['name']

# 四句失败文案照抄原型 HINTS：屏上不另写一套说法。
proto = read(root + '/docs/登录界面原型.html')
for hint in ('请求没发出去，重试一次', '账号或口令不对', '暂时连不上，稍后再试', '连不上网关'):
    assert hint in flow, 'hint missing in flow: ' + hint
    assert hint in proto, 'hint missing in proto: ' + hint

# 界面不本地判登录：不拿口令去比对，令牌只有一处赋值、来源是回包，且不落盘。
for p in (ui + '/UiEntryFlow.cs', ui + '/UiLoginScreen.cs', assets + '/Net/AccountEntry.cs'):
    text = read(p)
    for bad in ('password ==', 'Password ==', 'password.Equals', 'Password.Equals'):
        assert bad not in text, bad + ' in ' + os.path.basename(p)
assert flow.count('Token = outcome.Token;') == 1, 'token 赋值处数不对'
assert 'Token = "' not in flow, '有写死的令牌'
for p in (ui + '/UiEntryFlow.cs', ui + '/UiLoginScreen.cs', assets + '/Net/AccountEntry.cs'):
    assert 'PlayerPrefs' not in read(p), '令牌不许落盘：' + os.path.basename(p)

# 创角只列四出身五职业，分支留空。
plain = entry.replace('\\"', '"')
assert '"branch":""' in plain, 'branch 不是空串'
assert 'Races.Length' in create and 'Roles.Length' in create, '出身与职业要按表生成'
assert 'branches_listed=0' in create, '建角日志没写明不列分支'

# 载入进度不决定能否进游戏：自动推进有硬上限，Entered 只有一处能置真。
assert '0.92f' in flow, '进度没有硬上限'
assert flow.count('Entered = true;') == 1, 'Entered 置真处数不对'
assert 'ConfirmServerEntry' in flow and 'SetProgress' in loading, '载入屏没有回包出口'

# 资源与色值：客户端不做同步资源加载，四屏不自己发明色值。
for p in glob.glob(assets + '/**/*.cs', recursive=True):
    assert 'Resources.Load' not in read(p), 'Resources.Load in ' + p
for p in glob.glob(ui + '/*.cs'):
    if os.path.basename(p) == 'UiPalette.cs':
        continue
    text = read(p)
    assert not re.search(r'#[0-9a-fA-F]{6}\b', text), 'hex color in ' + os.path.basename(p)

# 手写 .meta 的 GUID 必须正好 32 位十六进制。
metas = [ui + '/' + n + '.cs.meta' for n in [x[:-3] for x in names]]
metas += [assets + '/Net/AccountEntry.cs.meta',
          assets + '/Editor/U2Check.cs.meta', assets + '/Editor/U2Preview.cs.meta']
for p in metas:
    assert os.path.exists(p), 'missing meta ' + os.path.basename(p)
    m = re.search(r'guid:\s*([0-9a-fA-F]+)', read(p))
    assert m and len(m.group(1)) == 32, 'bad guid: ' + os.path.basename(p)

print('u2_static=ok files=%d metas=%d races=%d classes=%d'
      % (len(names) + 3, len(metas), len(races), len(classes)))
PY
then ok "四屏文件齐、出身职业对得上 config、四句文案照抄原型、无本地判登录、分支为空、进度有上限、meta 32 位"
else bad "四屏静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器自检：四屏真搭起来跑一遍 =="
if [ ! -x "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U2Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U2Check.Run 退出码 0"
else
  bad "Unity U2Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files codex hints no_local_verdict create_scope loading_gate assets self; do
  if grep -q "u2_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u2_${key}=ok"
  else
    bad "编辑器自检 u2_${key} 没过"
    grep "u2_${key}" "$TMP/unity.log" 2>/dev/null | tail -3 | detail
  fi
done

for key in login_page login_empty_no_request login_401 login_network login_503 login_distinct \
           login_400 login_200 login_no_local_verdict \
           create_options create_four_races create_five_roles create_incomplete_no_request \
           create_body create_ok \
           roster_page roster_rows loading_page loading_progress_only loading_refused loading_entered \
           skin_layout skin_copy skin_color \
           verse_bag_unique verse_bag_refill verse_bag_next_cycle; do
  if grep -q "u2_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "四屏自检 u2_self_${key}=ok"
  else
    bad "四屏自检 u2_self_${key} 没过"
    grep "u2_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

fi

echo "== 3. 真网关段 =="
GATEWAY="${MMO_U2_GATEWAY:-}"
if [ -z "$GATEWAY" ]; then
  echo "  (跳过：没给 MMO_U2_GATEWAY；这一段要在有库的机器上跑，例如"
  echo "   MMO_U2_GATEWAY=http://127.0.0.1:61000 bash tools/check/check-u2.sh)"
else
  if "$DOTNET" build "$PROBE/EntryProbe.csproj" -c Release --nologo -v q >"$TMP/probe-build.log" 2>&1; then
    ok "探针编译（直接编客户端 AccountEntry.cs）"
  else
    bad "探针编译失败"
    grep -E "error " "$TMP/probe-build.log" | head -5 | detail
  fi

  PROBE_DLL="$PROBE/bin/Release/net10.0/EntryProbe.dll"
  probe() {
    "$DOTNET" "$PROBE_DLL" "$@" 2>&1
  }

  # 口令不对：真网关回 401，拆装要认成 invalid_credentials，且拿不到令牌。
  code="$(curl -s -o "$TMP/login401.json" -w '%{http_code}' --max-time 5 \
      -X POST "$GATEWAY/login" -H 'Content-Type: application/json' \
      -d '{"account":"u2_probe_absent","password":"not-a-real-password"}' || true)"
  if [ "$code" = "401" ]; then
    ok "真网关 401（账号不存在也回 401，不区分原因）"
    probe --login 401 "$(cat "$TMP/login401.json")" >"$TMP/p401.out"
    if grep -q '^kind=invalid_credentials$' "$TMP/p401.out" && grep -q '^token_len=0$' "$TMP/p401.out"; then
      ok "401 回包拆成 invalid_credentials，没有令牌"
    else
      bad "401 回包拆得不对"; cat "$TMP/p401.out" | detail
    fi
  else
    bad "真网关没回 401（拿到 $code）"
  fi

  # 连不上：换一个没人听的端口，拆装要认成 network，不能当成口令错。
  probe --login 0 '' >"$TMP/pnet.out"
  if grep -q '^kind=network$' "$TMP/pnet.out"; then
    ok "连接被拒拆成 network（与口令错分开）"
  else
    bad "连接被拒没被认成 network"; cat "$TMP/pnet.out" | detail
  fi

  # 成功登录：真网关发真令牌，拆装要认成 ok 且带上非空令牌。
  ACCOUNT="${MMO_U2_ACCOUNT:-}"
  PASSWORD="${MMO_U2_PASSWORD:-}"
  if [ -n "$ACCOUNT" ] && [ -n "$PASSWORD" ]; then
    code="$(curl -s -o "$TMP/login200.json" -w '%{http_code}' --max-time 5 \
        -X POST "$GATEWAY/login" -H 'Content-Type: application/json' \
        -d "{\"account\":\"$ACCOUNT\",\"password\":\"$PASSWORD\"}" || true)"
    if [ "$code" = "200" ]; then
      ok "真网关 200（口令正确）"
      probe --login 200 "$(cat "$TMP/login200.json")" >"$TMP/p200.out"
      if grep -q '^kind=ok$' "$TMP/p200.out" && ! grep -q '^token_len=0$' "$TMP/p200.out"; then
        ok "200 回包拆出非空令牌（令牌来自服务端，不是本地造的）"
      else
        bad "200 回包没拆出令牌"; cat "$TMP/p200.out" | detail
      fi
    else
      bad "真网关没回 200（拿到 $code）"
      head -3 "$TMP/login200.json" | detail
    fi
  else
    echo "  (跳过：没给 MMO_U2_ACCOUNT / MMO_U2_PASSWORD，成功登录这一段没跑)"
  fi

  # 建角请求体：分支为空、外观来自出身表。
  appearance="$(python3 - <<'PY2'
import json
races = json.load(open('config/races.json', encoding='utf-8'))['races']
print(json.dumps(races[0]['appearance'], separators=(',', ':')))
PY2
)"
  probe --create-body chen warrior 顾长息 "$appearance" >"$TMP/pbody.out"
  if grep -q '"branch":""' "$TMP/pbody.out" && grep -q '\\"body\\"' "$TMP/pbody.out"; then
    ok "建角请求体：分支为空、外观取自身表"
  else
    bad "建角请求体不对"; cat "$TMP/pbody.out" | detail
  fi
fi

finish "$TITLE"
