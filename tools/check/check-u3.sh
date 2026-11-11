#!/usr/bin/env bash
# U3 核对：战斗与副本 4 屏（开发架构第 14 节第 73 行）。
#
# 完成标准（第 73 行）：
#   血条蓝条只读回包；被拒绝的技能在画面上停住且血量不变；
#   Boss 阶段与疲劳 / 次数显示和只读入口一致。
#
# 这里跑三段：
#   1. 静态断言：四屏文件齐、展示表与 config/dungeons.json 与 exp.json 逐条对得上、
#      请求体只有角色号/副本编号/人数、没有本地算血算蓝与本地猜阶段、
#      HUD 的三个按钮矩形一个数没动、新加的三块不进 HitTest、meta 32 位。
#   2. Unity 编辑器自检：三屏真搭起来跑一遍，回包靠替换发送实现喂进去。
#   3. 真网关段（可选）：给了 MMO_U3_GATEWAY 就用 curl 打真网关，
#      把真状态码与真回包喂给同一个 DungeonEntry 拆装；给了角色服 DLL 再与
#      --fatigue-state / --share-state 两个只读入口对账。
#
# 真机 P0-3c 的 59 项属于依赖闭包，按 17.4 用
#   bash tools/check/check-all.sh --card u3
# 跑（客户端代码变了要先重新出包）。
#
# 用法：
#   bash tools/check/check-u3.sh
#   MMO_U3_GATEWAY=http://127.0.0.1:61000 MMO_U3_TOKEN=<令牌> \
#     MMO_U3_CHARACTER=<角色号> bash tools/check/check-u3.sh
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE="$ROOT/tools/probe/DungeonProbe"
# 与只读入口对账要在能连库的机器上做：没给 DLL 就没法查 --fatigue-state，直接跳过，
# 不拿「没查过」冒充「查过一致」。
CHARACTER_DLL="${MMO_U3_CHARACTER_DLL:-}"
TMP="$(mktemp -d)"
TITLE="U3（client）"

cleanup() {
  rm -rf "$TMP"
}
trap cleanup EXIT

# 公共函数库只有 ok / bad / detail：跳过项单独印一行，别混进通过数。
skip() { printf '  skip %s\n' "$1"; }


# 第 16 节：笔记本只同步服务端，没有 client/MMOGame。缺工程时跳过前两段，
# 但真网关段不依赖客户端工程，给了 MMO_U3_GATEWAY 就照样跑。
HAVE_CLIENT=1
if [ ! -d "$ROOT/client/MMOGame" ]; then
  HAVE_CLIENT=0
  echo "  skip 这台机器没有客户端工程（静态段与 Unity 段在 Mac 上跑）"
fi

if [ "$HAVE_CLIENT" = "1" ]; then

echo "== 1. 四屏静态断言 =="
if ROOT="$ROOT" python3 - >"$TMP/static.out" 2>&1 <<'PY'
import json, os, re

root = os.environ['ROOT']
assets = root + '/client/MMOGame/Assets'
ui = assets + '/HotUpdate/Ui'

def read(p):
    return open(p, encoding='utf-8').read()

names = ['UiBattleCodex.cs', 'UiDungeonFlow.cs', 'UiDungeonScreen.cs',
         'UiSettleScreen.cs', 'UiPartyScreen.cs', 'UiBattleSelfCheck.cs']
for n in names:
    assert os.path.exists(ui + '/' + n), 'missing ' + n
assert os.path.exists(assets + '/Net/DungeonEntry.cs'), 'missing DungeonEntry.cs'
for n in ('U3Check.cs', 'U3Preview.cs'):
    assert os.path.exists(assets + '/Editor/' + n), 'missing ' + n

codex = read(ui + '/UiBattleCodex.cs')
flow = read(ui + '/UiDungeonFlow.cs')
entry = read(assets + '/Net/DungeonEntry.cs')
hud = read(assets + '/HotUpdate/BattleHud.cs')
settle = read(ui + '/UiSettleScreen.cs')
party = read(ui + '/UiPartyScreen.cs')

# ---- 展示表与配置正本逐条对得上（编号、名字、人数上限、扣不扣疲劳、占不占份额）
dungeon_cfg = json.loads(read(root + '/config/dungeons.json'))
exp_cfg = json.loads(read(root + '/config/exp.json'))
rows = dungeon_cfg['dungeons']
assert len(rows) == 3, 'config dungeons=%d' % len(rows)
for row in rows:
    assert 'Id = "%s"' % row['id'] in codex, 'dungeon id ' + row['id']
    assert row['name'] in codex, 'dungeon name ' + row['name']
    assert 'MaxPlayers = %d' % row['max_players'] in codex, 'max_players ' + row['id']
    flag = 'true' if row['charges_fatigue'] else 'false'
    assert 'ChargesFatigue = %s' % flag in codex, 'charges_fatigue ' + row['id']
    flag = 'true' if row['takes_weekly_share'] else 'false'
    assert 'TakesWeeklyShare = %s' % flag in codex, 'takes_weekly_share ' + row['id']

assert 'FatigueDailyCap = %d' % dungeon_cfg['fatigue_daily_cap'] in codex, 'daily cap'
assert 'HotSpringRecovery = %d' % dungeon_cfg['hot_spring_recovery'] in codex, 'hot spring'
assert 'WeeklyShareCap = %d' % dungeon_cfg['weekly_share_cap'] in codex, 'share cap'
assert 'PartyBonusPct = %d' % exp_cfg['party_bonus_pct'] in codex, 'party bonus'
assert 'PartyMinSize = %d' % exp_cfg['party_min_size'] in codex, 'party min size'

# 配置里没有的副本不许出现在表里（列了就是编数据）。
for absent in ('dragon', 'world_boss', 'sect'):
    assert 'Id = "%s"' % absent not in codex, 'invented dungeon ' + absent
# 只列三条，不多不少。
assert codex.count('Id = "') == 3, 'dungeon rows=%d' % codex.count('Id = "')

# ---- 请求体只有三样：角色号、副本编号、人数；不带任何能左右结果的数值
plain = entry.replace('\\"', '"')
assert '"branch":"' not in plain, 'branch in body'
for field in ('"character_id":', '"dungeon_id":', '"party_size":'):
    assert field in plain, 'body missing ' + field
for field in ('"fatigue":', '"weekly_share":', '"exp":', '"damage":'):
    assert field not in plain, 'body carries ' + field

# ---- 被拒这一档来自 allowed 字段，不是从状态码猜
assert 'ReadBool(body, "allowed")' in entry, 'allowed flag'

# ---- 客户端没有本地判定：不掷点、不乘系数、不扣数值
for text, name in ((flow, 'flow'), (settle, 'settle'), (party, 'party')):
    assert 'Random' not in text, 'random in ' + name
for bad in ('FatigueDailyCap -', 'WeeklyShareCap -', 'PartyBonusPct *', 'ExpGain *', 'ExpGain /'):
    assert bad not in flow + settle, 'local math ' + bad

# ---- HUD：既有按钮矩形一个数没动，既有日志还在，新块不进 HitTest
for keep in ('const int SkillButtonSize = 200;', 'const int SkillButtonRight = 60;',
             'const int SkillButtonBottom = 60;', 'const int CancelButtonHeight = 90;',
             'new Rect(40f, Screen.height - 170f, 260f, 84f)',
             'p02b_hud=ready', 'p03c_hud=ready', 'u3_hud=ready'):
    assert keep in hud, 'hud changed: ' + keep

start = hud.index('public static bool HitTest')
end = hud.index('static Rect SkillButtonRect')
hit = hud[start:end]
assert hit.count('.Contains(') == 3, 'hit test rects=%d' % hit.count('.Contains(')
for rect in ('ClassButtonRect()', 'SkillButtonRect(i)', 'CancelButtonRect()'):
    assert rect in hit, 'hit test lost ' + rect
for banned in ('Party', '_phaseText', '_logText'):
    assert banned not in hit, 'hit test touched ' + banned

# 血与蓝只从回包读，HUD 里没有赋值回写。
for write in ('BossHp =', 'CasterHp =', 'Mana ='):
    assert write not in hud, 'hud writes ' + write
for read_field in ('_client.BossHp', '_client.CasterHp', '_client.Mana', '_client.Party'):
    assert read_field in hud, 'hud does not read ' + read_field

# 阶段只认广播，不按血线猜。
assert 'UiBattleCodex.PhaseLine(_client.PhaseIndex, _client.PhaseName)' in hud, 'phase not from reply'
for guess in ('BossHp <', 'BossHp >', 'BossHp <=', 'BossHp >='):
    assert guess not in hud, 'phase guessed by hp ' + guess

# 结算屏不自己造掉落表。
assert 'SettleDropNote' in settle, 'drop note missing'
assert '铁矿' not in settle and '强化石 ×' not in settle, 'invented drop table'

# ---- 客户端不许同步加载资源：字面量拼出来，免得这条断言自己撞上自己
needle = 'Resources' + '.Load'
for name in os.listdir(ui):
    if name.endswith('.cs'):
        assert needle not in read(ui + '/' + name), 'resources.load in ' + name

# ---- 手写 meta 的 GUID 必须正好 32 位十六进制
checked = 0
for name in names:
    meta = ui + '/' + name + '.meta'
    assert os.path.exists(meta), 'missing meta ' + name
    text = read(meta)
    found = re.search(r'^guid: ([0-9a-f]+)$', text, re.M)
    assert found is not None, 'no guid in ' + name + '.meta'
    assert len(found.group(1)) == 32, 'guid length %d in %s' % (len(found.group(1)), name)
    checked += 1
for name in ('U3Check.cs', 'U3Preview.cs'):
    meta = assets + '/Editor/' + name + '.meta'
    assert os.path.exists(meta), 'missing meta ' + name
    assert len(re.search(r'^guid: ([0-9a-f]+)$', read(meta), re.M).group(1)) == 32, 'guid ' + name
    checked += 1
print('metas=%d' % checked)
PY
then
  ok "四屏文件齐、展示表对得上 config、请求体只有三样、无本地算血算蓝与本地猜阶段、HUD 矩形未动、新块不进 HitTest、meta 32 位"
else
  bad "四屏静态断言没过"
  sed -n '1,12p' "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器自检：三屏真搭起来跑一遍 =="
if [ ! -x "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod U3Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity U3Check.Run 退出码 0"
else
  bad "Unity U3Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files codex_dungeons codex_caps codex_not_yet dungeon_scope dungeon_allowed_flag \
           no_local_verdict hud_readonly phase_by_reply settle_exp_reply \
           hud_rects_unchanged hud_logs hud_hit_unchanged settle_drop settle_no_rule \
           no_resources_load meta_guid self; do
  if grep -q "u3_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 u3_${key}=ok"
  else
    bad "编辑器自检 u3_${key} 没过"
    grep "u3_${key}" "$TMP/unity.log" 2>/dev/null | tail -3 | detail
  fi
done

for key in dungeon_list dungeon_not_yet dungeon_initial dungeon_enter_enabled dungeon_body \
           dungeon_allowed_kind dungeon_allowed_copy dungeon_allowed_hint dungeon_allowed_cost \
           dungeon_refused_kind dungeon_refused_hint dungeon_refused_numbers \
           dungeon_auth dungeon_network dungeon_unavailable \
           party_clamp_max party_clamp_abyss party_unknown_kept \
           settle_initial settle_cleared settle_exp_from_reply settle_txn_shown settle_failed \
           party_empty party_four skin_copy; do
  if grep -q "u3_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "四屏自检 u3_self_${key}=ok"
  else
    bad "四屏自检 u3_self_${key} 没过"
    grep "u3_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

fi

echo "== 3. 真网关段 =="
if [ -z "${MMO_U3_GATEWAY:-}" ]; then
  skip "没给 MMO_U3_GATEWAY；这一段要在有库的机器上跑，例如"
  printf '       %s\n' "MMO_U3_GATEWAY=http://127.0.0.1:61000 MMO_U3_TOKEN=<令牌> MMO_U3_CHARACTER=<角色号> bash tools/check/check-u3.sh"
elif [ ! -x "$DOTNET" ]; then
  bad "找不到 dotnet：$DOTNET"
else
  if ! "$DOTNET" build "$PROBE/DungeonProbe.csproj" -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
    bad "探针编不过"
    tail -20 "$TMP/build.log" | detail
  else
    PROBE_DLL="$PROBE/bin/Release/net10.0/DungeonProbe.dll"
    GW="${MMO_U3_GATEWAY%/}"
    TOKEN="${MMO_U3_TOKEN:-}"
    CHR="${MMO_U3_CHARACTER:-0}"

    # 3.1 没有令牌：网关必须回 401，拆装成 invalid_token，不进本。
    code() { printf '%s' "$1" | sed -n 's/.*"code":\([0-9-]*\).*/\1/p' | head -1; }
    HTTP="$("$DOTNET" "$PROBE_DLL" --enter-body "$CHR" abyss 1 | sed 's/^body=//')"
    RESP="$(curl -s -o "$TMP/r1.json" -w '%{http_code}' -X POST "$GW/dungeon/enter" \
      -H 'Content-Type: application/json' -d "$HTTP" || true)"
    if [ "$RESP" = "401" ] \
        && "$DOTNET" "$PROBE_DLL" --enter "$RESP" "$(cat "$TMP/r1.json")" >"$TMP/p1.out" 2>&1 \
        && grep -q '^kind=invalid_token' "$TMP/p1.out"; then
      ok "无令牌进本：真网关回 401，拆成 invalid_token（不进本）"
    else
      bad "无令牌进本没被拦：http=$RESP"
      head -3 "$TMP/r1.json" 2>/dev/null | detail
    fi

    if [ -n "$TOKEN" ] && [ "$CHR" != "0" ]; then
      # 3.2 人数超上限：回 200 但 allowed=false，原因非空，界面照「进不去」显示。
      HTTP="$(printf '%s' "{\"character_id\":$CHR,\"dungeon_id\":\"main\",\"party_size\":4}")"
      RESP="$(curl -s -o "$TMP/r2.json" -w '%{http_code}' -X POST "$GW/dungeon/enter" \
        -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d "$HTTP" || true)"
      "$DOTNET" "$PROBE_DLL" --enter "$RESP" "$(cat "$TMP/r2.json")" >"$TMP/p2.out" 2>&1 || true
      if [ "$RESP" = "200" ] && grep -q '^allowed=false' "$TMP/p2.out" \
          && ! grep -q '^reason=$' "$TMP/p2.out"; then
        ok "人数超上限：真网关回 200 且 allowed=false，原因非空"
        grep '^reason=' "$TMP/p2.out" | detail
      else
        bad "人数超上限没被拒：http=$RESP"
        head -3 "$TMP/r2.json" 2>/dev/null | detail
      fi

      # 3.3 正常进本：疲劳、日上限、份额都拆得出来，且与只读入口一致。
      HTTP="$(printf '%s' "{\"character_id\":$CHR,\"dungeon_id\":\"abyss\",\"party_size\":1}")"
      RESP="$(curl -s -o "$TMP/r3.json" -w '%{http_code}' -X POST "$GW/dungeon/enter" \
        -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d "$HTTP" || true)"
      "$DOTNET" "$PROBE_DLL" --enter "$RESP" "$(cat "$TMP/r3.json")" >"$TMP/p3.out" 2>&1 || true
      if [ "$RESP" = "200" ] && grep -qE '^allowed=(true|false)' "$TMP/p3.out" \
          && grep -q '^fatigue_daily_cap=6000' "$TMP/p3.out"; then
        ok "正常进本：真网关回 200，疲劳与日上限拆得出来"
        grep -E '^(kind|allowed|fatigue|fatigue_cost|weekly_share_remaining|reward_shares)=' "$TMP/p3.out" | detail
      else
        bad "正常进本没拆出来：http=$RESP"
        head -3 "$TMP/r3.json" 2>/dev/null | detail
      fi

      # 3.4 与只读入口对账：角色服的 --fatigue-state / --share-state 是唯一事实。
      if [ -f "$CHARACTER_DLL" ]; then
        CFG="$TMP/character.json"
        if [ -f "$ROOT/config/character.json" ]; then cp "$ROOT/config/character.json" "$CFG"; fi
        "$DOTNET" "$CHARACTER_DLL" --fatigue-state "$CHR" --config "$CFG" 2>&1 \
          | grep -m1 '^fatigue_state=' >"$TMP/state.out" || true
        GW_FAT="$(sed -n 's/^fatigue=//p' "$TMP/p3.out" | head -1)"
        DB_FAT="$(sed -n 's/.*[[:space:]]fatigue=\([0-9-]*\).*/\1/p' "$TMP/state.out" | head -1)"
        if [ -n "$GW_FAT" ] && [ -n "$DB_FAT" ] && [ "$GW_FAT" = "$DB_FAT" ]; then
          ok "疲劳与只读入口一致：fatigue=$GW_FAT"
        else
          bad "疲劳与只读入口对不上：回包 $GW_FAT / 只读 $DB_FAT"
        fi
      else
        skip "没找到角色服 DLL，跳过与只读入口对账"
        printf '       %s\n' "给 MMO_U3_CHARACTER_DLL=<Character.dll 路径> 就能对上 --fatigue-state"
      fi
    else
      skip "没给 MMO_U3_TOKEN / MMO_U3_CHARACTER，只验了「没令牌不许进本」"
      printf '       %s\n' "这两个值在笔记本上用 --fatigue-state 的那个角色号，令牌走 /login 换"
    fi

    # 3.5 连不上网关：拆成 network，不当成被拒。
    if "$DOTNET" "$PROBE_DLL" --enter 0 '' >"$TMP/p5.out" 2>&1 \
        && grep -q '^kind=network' "$TMP/p5.out"; then
      ok "请求没到网关：拆成 network（不算被拒）"
    else
      bad "连不上网关这一档拆错了"
      head -3 "$TMP/p5.out" 2>/dev/null | detail
    fi
  fi
fi

finish "$TITLE"
