#!/usr/bin/env bash
# U4 核对：表世界 5 屏（开发架构第 14 节第 74 行）。
#
# 完成标准（第 74 行）：
#   同屏三档按世界服下发的档位翻译；
#   飞行没权限被拉回地面时画面回地面；
#   采集与跨区只展示回包结果。
#
# 这里跑三段：
#   1. 静态断言：五屏文件齐、展示表与 config/world.json / region/ / dialogue/ /
#      gather.json / flight.json 逐条对得上、没有按距离算档、飞行高度取回包的 y、
#      战斗 HUD 的三个矩形与两条日志一个数没动、meta 32 位。
#   2. Unity 编辑器自检：五屏真搭起来跑一遍，回包靠替换发送实现喂进去。
#   3. 真网关段（可选）：给了 MMO_U4_GATEWAY 就用 curl 打真网关，
#      把真状态码与真回包喂给同一个 WorldEntry 拆装。
#
# 真机 P0-3c 的 59 项属于依赖闭包，按 17.4 用
#   bash tools/check/check-all.sh --card u4
# 跑（客户端代码变了要先重新出包）。
#
# 用法：
#   bash tools/check/check-u4.sh
#   MMO_U4_GATEWAY=http://127.0.0.1:61000 MMO_U4_TOKEN=<令牌> \
#     MMO_U4_CHARACTER=<角色号> bash tools/check/check-u4.sh
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE="$ROOT/tools/probe/WorldEntryProbe"
TMP="$(mktemp -d)"
TITLE="U4（client）"

cleanup() {
  rm -rf "$TMP"
}
trap cleanup EXIT

# 公共函数库只有 ok / bad / detail：跳过项单独印一行，别混进通过数。
skip() { printf '  skip %s\n' "$1"; }

# 第 16 节：笔记本只同步服务端，没有 client/MMOGame。缺工程时跳过前两段，
# 但真网关段不依赖客户端工程，给了 MMO_U4_GATEWAY 就照样跑。
HAVE_CLIENT=1
if [ ! -d "$ROOT/client/MMOGame" ]; then
  HAVE_CLIENT=0
  skip "这台机器没有客户端工程（静态段与 Unity 段在 Mac 上跑）"
fi

if [ "$HAVE_CLIENT" = "1" ]; then

  echo "== 1. 静态断言：展示表对配置、档位不自己算、HUD 不动 =="
  python3 - > "$TMP/static.txt" <<'PY'
import glob, json, os, re

root = os.environ['ROOT']

def client(rel):
    p = os.path.join(root, 'client/MMOGame/Assets', rel)
    return open(p, encoding='utf-8').read() if os.path.exists(p) else ''

def repo(rel):
    p = os.path.join(root, rel)
    return open(p, encoding='utf-8').read() if os.path.exists(p) else ''

def say(key, ok, detail=''):
    print('%s|%s|%s' % (key, 'ok' if ok else 'fail', detail))

files = [
    'Net/WorldEntry.cs',
    'HotUpdate/Ui/UiWorldCodex.cs',
    'HotUpdate/Ui/UiWorldFlow.cs',
    'HotUpdate/Ui/UiWorldHudScreen.cs',
    'HotUpdate/Ui/UiWorldMapScreen.cs',
    'HotUpdate/Ui/UiDialogueScreen.cs',
    'HotUpdate/Ui/UiGatherScreen.cs',
    'HotUpdate/Ui/UiFlightScreen.cs',
    'HotUpdate/Ui/UiWorldSelfCheck.cs',
    'Editor/U4Check.cs',
    'Editor/U4Preview.cs',
]
missing = [f for f in files if not client(f)]
say('files', not missing, ('missing=' + ','.join(missing)) if missing else 'files=%d' % len(files))

bad_meta = []
for f in files:
    text = client(f + '.meta')
    m = re.search(r'guid: ([0-9a-f]+)', text)
    if not m or len(m.group(1)) != 32:
        bad_meta.append(f)
say('meta', not bad_meta, ('bad=' + ','.join(bad_meta)) if bad_meta else 'guid32=%d' % len(files))

codex = client('HotUpdate/Ui/UiWorldCodex.cs')
world = repo('config/world.json')
gather = repo('config/gather.json')
flight = repo('config/flight.json')

regions = ['枯岚道', '承露门', '听谣镇', '外环隙', '栖云城', '旧天城区', '焦原', '龙巢门口']
ok_regions = all(('"%s"' % r) in world and ('Id = "%s"' % r) in codex for r in regions)
n_region_files = len(glob.glob(os.path.join(root, 'config/region/*.json')))
say('codex_regions', ok_regions and n_region_files == len(regions),
    'codex=%d config=%d' % (len(regions), n_region_files))

speakers = ['乔小满', '苏晚织', '阮青', '阿弦', '闻嫂', '孟七', '澄川', '灰生', '周晚棠']
ok_speakers = all(('"%s"' % s) in world and ('Name = "%s"' % s) in codex for s in speakers)
n_dialogue_files = len(glob.glob(os.path.join(root, 'config/dialogue/*.json')))
say('codex_speakers', ok_speakers and n_dialogue_files == len(speakers),
    'codex=%d config=%d' % (len(speakers), n_dialogue_files))

nodes = ['ore_kulan_1', 'ore_kulan_2', 'herb_tingyao_1', 'spirit_qiyun_1']
ok_nodes = all(n in gather and ('Id = "%s"' % n) in codex for n in nodes)
spirit_block = re.search(r'Id = "spirit_qiyun_1"[\s\S]{0,240}?Forbidden = true', codex)
say('codex_nodes', ok_nodes and bool(spirit_block),
    'nodes=%d spirit_forbidden=%s' % (len(nodes), bool(spirit_block)))

realms = ['炼体', '筑基', '结丹']
ok_realms = ('public const string FlightRealm = "筑基"' in codex) and all(
    ('"%s"' % r) in flight and ('"%s"' % r) in codex for r in realms)
say('codex_realms', ok_realms, 'realms=%d flight_realm=%s' % (len(realms), 'FlightRealm' in codex))

flow = client('HotUpdate/Ui/UiWorldFlow.cs')
hud = client('HotUpdate/Ui/UiWorldHudScreen.cs')
all_ui = flow + hud

say('tier_translate', 'WorldPeerTiers.For' in all_ui and 'AppearanceTier' in all_ui, 'via=WorldPeerTiers')
no_distance = not any(x in all_ui for x in ('Vector3.Distance', 'Mathf.Distance', 'tier_near_distance', 'tier_far_distance'))
no_compare = re.search(r'Tier\s*[<>=]\s*\d', all_ui) is None
say('tier_no_distance', no_distance and no_compare,
    'distance_free=%s compare_free=%s' % (no_distance, no_compare))

screens = ['HotUpdate/Ui/UiWorldFlow.cs', 'HotUpdate/Ui/UiWorldHudScreen.cs', 'HotUpdate/Ui/UiWorldMapScreen.cs',
           'HotUpdate/Ui/UiDialogueScreen.cs', 'HotUpdate/Ui/UiGatherScreen.cs', 'HotUpdate/Ui/UiFlightScreen.cs']
ui_all = ''.join(client(f) for f in screens)
# 屏上会写「config/flight.json 的 flight_realm」这类注脚，那只是文案；
# 要禁的是真去读文件与掷点，所以查读写 API 而不是查 config 这个词。
no_read = not any(x in ui_all for x in ('File.ReadAllText', 'File.Exists', 'Directory.GetFiles', 'Directory.Exists', 'Random.'))
say('no_config_read', no_read, 'chars=%d' % len(ui_all))
say('no_resources_load', 'Resources.Load' not in ui_all, '')
say('no_local_verdict', 'GatherReply.Ok' in ui_all and 'RegionReply.Ok' in ui_all, 'from_reply=1')
say('fly_height_from_reply',
    'public float ScreenY => HasFly ? FlyReply.Y : 0f' in flow and
    'public bool TrailAllowed => HasFly && FlyReply.TrailAllowed' in flow, 'screen_y=1 trail=1')

entry = client('Net/WorldEntry.cs')
# 只看 EnterBody 那一段：回包解析当然要认 line_id、neighbors，请求体里不能有。
at = entry.find('public static string EnterBody')
stop = entry.find('public static WorldEnterOutcome ParseEnter')
body_part = entry[at:stop] if at >= 0 and stop > at else ''
say('enter_body_scope',
    '\\"character_id\\"' in body_part and '\\"region_id\\"' in body_part and
    'display_name' not in body_part and 'line_id' not in body_part and
    'neighbors' not in body_part, 'fields=2')

battle = client('HotUpdate/BattleHud.cs')
battle_ok = ('static Rect SkillButtonRect(int index)' in battle and
             'static Rect CancelButtonRect()' in battle and
             'static Rect ClassButtonRect()' in battle and
             'p02b_hud=' in battle and 'p03c_hud=' in battle and
             'UiWorld' not in battle)
say('battle_hud_untouched', battle_ok, 'rects=3 logs=2 no_u4=1')
PY

  while IFS='|' read -r key verdict note; do
    [ -z "$key" ] && continue
    if [ "$verdict" = "ok" ]; then
      ok "$key${note:+  ($note)}"
    else
      bad "$key${note:+  ($note)}"
    fi
  done < "$TMP/static.txt"

  echo
  echo "== 2. Unity 编辑器自检：五屏实例断言 =="
  if [ "${MMO_U4_SKIP_UNITY:-0}" = "1" ]; then
    skip "MMO_U4_SKIP_UNITY=1，跳过编辑器段"
  elif [ ! -x "$UNITY" ]; then
    skip "没有 Unity：$UNITY"
  else
    rm -f "$TMP/u4.log"
    "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
      -executeMethod U4Check.Run -logFile "$TMP/u4.log" >/dev/null 2>&1
    U4_EXIT=$?
    if grep -q 'u4_all=ok' "$TMP/u4.log" 2>/dev/null; then
      ok "Unity U4Check.Run 退出码 $U4_EXIT"
    else
      bad "Unity U4Check.Run 退出码 $U4_EXIT"
      grep -E 'u4_[a-z_]+=fail|error CS' "$TMP/u4.log" 2>/dev/null | head -8 | detail
    fi

    # 编辑器段自己的静态断言：任一条 fail 都要单独列出来，别只报一个总判。
    for key in files meta codex_regions codex_speakers codex_nodes codex_realms codex_second_line tier_translate tier_no_distance no_config_read no_resources_load verdict_from_reply fly_height_from_reply fly_trail_from_reply fly_body_scope battle_hud_untouched; do
      if grep -q "u4_${key}=ok" "$TMP/u4.log" 2>/dev/null; then
        ok "编辑器自检 u4_${key}=ok"
      elif grep -q "u4_${key}=fail" "$TMP/u4.log" 2>/dev/null; then
        bad "编辑器自检 u4_${key}=fail"
      fi
    done

    # 五屏实例断言：逐条列，失败的那几条单独摘出来。
    SELF_OK=$(grep -cE 'u4_self_[a-z_]+=ok' "$TMP/u4.log" 2>/dev/null || true)
    SELF_FAIL=$(grep -cE 'u4_self_[a-z_]+=fail' "$TMP/u4.log" 2>/dev/null || true)
    SELF_OK=${SELF_OK:-0}
    SELF_FAIL=${SELF_FAIL:-0}
    if [ "$SELF_FAIL" = "0" ] && [ "$SELF_OK" -gt 0 ]; then
      ok "五屏实例自检 $SELF_OK 项"
    else
      bad "五屏实例自检 $SELF_OK 项通过 / $SELF_FAIL 项未通过"
      grep -E 'u4_self_[a-z_]+=fail' "$TMP/u4.log" 2>/dev/null | head -8 | detail
    fi
  fi
fi

# ------------------------------------------------------------------ 3. 真网关段

echo
echo "== 3. 真网关段 =="
if [ -z "${MMO_U4_GATEWAY:-}" ]; then
  skip "没给 MMO_U4_GATEWAY，跳过真网关段"
  printf '       %s\n' "MMO_U4_GATEWAY=http://127.0.0.1:61000 MMO_U4_TOKEN=<令牌> MMO_U4_CHARACTER=<角色号> bash tools/check/check-u4.sh"
else
  GW="${MMO_U4_GATEWAY%/}"
  TOKEN="${MMO_U4_TOKEN:-}"
  CHAR="${MMO_U4_CHARACTER:-0}"

  if [ ! -d "$PROBE" ]; then
    skip "没有探针工程"
  elif [ -z "$DOTNET" ] || [ ! -x "$DOTNET" ]; then
    skip "没有 dotnet，跳过拆装段"
  else
    "$DOTNET" build "$PROBE/WorldEntryProbe.csproj" -c Release -v q --nologo > "$TMP/build.log" 2>&1
    if [ $? -ne 0 ]; then
      bad "探针编不过"
      tail -6 "$TMP/build.log" | detail
    else
      DLL="$PROBE/bin/Release/net10.0/WorldEntryProbe.dll"

      # 3.1 请求体只有角色号与地表编号
      BODY=$("$DOTNET" "$DLL" --enter-body "$CHAR" 枯岚道 | sed -n 's/^body=//p')
      if printf '%s' "$BODY" | grep -q "character_id" && printf '%s' "$BODY" | grep -q "region_id"; then
        ok "进分线请求体只带角色号与地表编号"
      else
        bad "进分线请求体不对：$BODY"
      fi

      # 3.2 无令牌 401 → invalid_token
      resp=$(curl -s -m 8 -w '\n%{http_code}' -X POST "$GW/world/enter" \
        -H 'Content-Type: application/json' -d "$BODY" 2>/dev/null)
      code=$(code_of "$resp")
      body_of "$resp" > "$TMP/r401.json"
      out=$("$DOTNET" "$DLL" --enter "$code" "@$TMP/r401.json")
      if [ "$code" = "401" ] && printf '%s' "$out" | grep -q '^kind=InvalidToken$'; then
        ok "无令牌进分线：401 拆成 invalid_token"
      else
        bad "无令牌进分线：code=$code $(printf '%s' "$out" | grep '^kind=' )"
      fi

      # 3.3 连不上网关 → network。走一个确定没人听的端口，并强制直连：
      # 本机若有 http_proxy，curl 会转给代理回 502，那就不是「连不上」而是另一回事了。
      dead=$(curl --noproxy '*' -s -m 3 -w '\n%{http_code}' -X POST "http://127.0.0.1:59999/world/enter" \
        -H 'Content-Type: application/json' -d "$BODY" 2>/dev/null || true)
      dead_code=$(code_of "$dead")
      if [ -z "$dead_code" ] || [ "$dead_code" = "000" ]; then dead_code=0; fi
      out=$("$DOTNET" "$DLL" --enter "$dead_code" "{}")
      if printf '%s' "$out" | grep -q '^kind=Network$'; then
        ok "连不上网关：拆成 network（不算被拒）"
      else
        bad "连不上网关：code=$dead_code $(printf '%s' "$out" | grep '^kind=')"
      fi

      if [ -n "$TOKEN" ] && [ "$CHAR" != "0" ]; then
        # 3.4 有令牌进分线：能不能进只看回包
        resp=$(curl -s -m 8 -w '\n%{http_code}' -X POST "$GW/world/enter" \
          -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d "$BODY" 2>/dev/null)
        code=$(code_of "$resp")
        body_of "$resp" > "$TMP/enter.json"
        out=$("$DOTNET" "$DLL" --enter "$code" "@$TMP/enter.json")
        kind=$(printf '%s' "$out" | sed -n 's/^kind=//p')
        if [ "$code" = "200" ] && { [ "$kind" = "Entered" ] || [ "$kind" = "Refused" ]; }; then
          ok "进分线回包：$code / $kind $(printf '%s' "$out" | grep '^reason=' | sed 's/^reason=//')"
        else
          bad "进分线回包：code=$code kind=$kind"
          printf '%s' "$out" | detail
        fi

        # 3.5 地点表只读：八块地表、九位开口的人
        resp=$(curl -s -m 8 -w '\n%{http_code}' "$GW/world/regions" \
          -H "Authorization: Bearer $TOKEN" 2>/dev/null)
        code=$(code_of "$resp")
        body_of "$resp" > "$TMP/regions.json"
        out=$("$DOTNET" "$DLL" --regions "$code" "@$TMP/regions.json")
        n=$(printf '%s' "$out" | sed -n 's/^regions=//p')
        sp=$(printf '%s' "$out" | sed -n 's/^speakers=//p')
        if [ "$code" = "200" ] && [ "$n" = "8" ] && [ "$sp" = "9" ]; then
          ok "地点表：$n 块地表 / $sp 位开口的人"
        else
          bad "地点表：code=$code regions=$n speakers=$sp"
        fi
      else
        skip "没给 MMO_U4_TOKEN / MMO_U4_CHARACTER，跳过带令牌的两项"
      fi
    fi
  fi
fi

finish "$TITLE"
