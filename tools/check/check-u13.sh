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

echo "== 3. 屏上自检（U13 那四条 + A9 加点三条）=="
for key in realm_pending realm_phase promote_ok promote_reject promote_body \
           a9_attr_points a9_allocate_ok a9_allocate_reject; do
  if grep -q "u5_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "境界自检 u5_self_${key}=ok"
  else
    bad "境界自检 u5_self_${key} 没过"
    grep "u5_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 3b. A9 属性加点屏（U18）=="
ATTR_NET="$ROOT/client/MMOGame/Assets/Net/CharacterAttr.cs"
ATTR_SCREEN="$ROOT/client/MMOGame/Assets/HotUpdate/Ui/UiAttrScreen.cs"
ATTR_CODEX="$ROOT/client/MMOGame/Assets/HotUpdate/Ui/UiUpgradeCodex.cs"
for f in "$ATTR_NET" "$ATTR_SCREEN" "$ROOT/config/ui_text/UiAttrScreen.json"; do
  if [ -f "$f" ]; then
    ok "有 $(basename "$f")"
  else
    bad "缺 $(basename "$f")"
  fi
done

# 请求体只带三项：事务号由网关生成，剩余点数 / 上限 / 洗点开关都在角色服判。
if grep -q 'character_id' "$ATTR_NET" && grep -q 'attribute' "$ATTR_NET" && grep -q 'points' "$ATTR_NET" \
   && ! grep -qE 'txn_id|per_attr_cap|free_respec' "$ATTR_NET"; then
  ok "加点请求体只有角色号 / 属性键 / 点数（不带事务号、上限、洗点开关）"
else
  bad "加点请求体里混进了客户端不该带的东西"
  grep -nE 'txn_id|per_attr_cap|free_respec' "$ATTR_NET" | head -3 | detail
fi

if grep -q '"/character/attr-points"' "$ATTR_NET" \
   && grep -q '"/character/attr-points/allocate"' "$ATTR_NET"; then
  ok "两条 HTTP 路径与网关一致"
else
  bad "属性点路径与网关不一致"
fi

# 屏上不本地扣点、不出洗点口；点数与四格已加点都只抄回包。
if ! grep -qE 'attr_points *-|-= *points|points -=|\breadonly .*cap' "$ATTR_SCREEN"; then
  ok "屏上不本地扣点（剩余点数只抄回包）"
else
  bad "屏上出现了本地扣点"
fi
if ! grep -qi 'respec' "$ATTR_SCREEN" "$ATTR_NET"; then
  ok "没有洗点口（A9 不做免费洗点）"
else
  bad "出现了洗点口"
fi

if python3 - "$ATTR_CODEX" <<'PY'
import re
import sys
s = open(sys.argv[1], encoding='utf-8').read()
m = re.search(r'Attrs\s*=\s*\{([^}]*)\}', s)
if not m:
    sys.exit(1)
keys = re.findall(r'"([a-z]+)"', m.group(1))
sys.exit(0 if keys == ['strength', 'agility', 'spirit', 'vitality'] else 1)
PY
then
  ok "四属性键恰好四个，顺序与回包字段一致"
else
  bad "四属性键与回包字段对不上"
fi

echo "== 3c. U20 制作屏与背包掷点（B3 界面欠项）=="
CRAFT_NET="$ROOT/client/MMOGame/Assets/Net/CharacterCraft.cs"
CRAFT_FLOW="$ROOT/client/MMOGame/Assets/HotUpdate/Ui/Flow/CraftFlow.cs"
CRAFT_SCREEN="$ROOT/client/MMOGame/Assets/HotUpdate/Ui/UiCraftScreen.cs"
BAG_SCREEN="$ROOT/client/MMOGame/Assets/HotUpdate/Ui/UiBagScreen.cs"
for f in "$CRAFT_NET" "$CRAFT_FLOW" "$CRAFT_SCREEN" "$BAG_SCREEN" \
         "$ROOT/config/ui_text/UiCraftScreen.json" "$ROOT/config/ui_text/CraftFlow.json"; do
  if [ -f "$f" ]; then
    ok "有 $(basename "$f")"
  else
    bad "缺 $(basename "$f")"
  fi
done

# 四条口与网关一致；开始只发角色号与配方编号（事务号网关生成）。
if grep -q '"/craft/recipes"' "$CRAFT_NET" && grep -q '"/craft/state"' "$CRAFT_NET" \
   && grep -q '"/craft/start"' "$CRAFT_NET" && grep -q '"/craft/finish"' "$CRAFT_NET" \
   && grep -q 'MapGet("/craft/recipes"' "$ROOT/src/Gateway/Api/Character/Craft.cs" \
   && grep -q 'MapGet("/craft/state"' "$ROOT/src/Gateway/Api/Character/Craft.cs" \
   && grep -q 'MapPost("/craft/start"' "$ROOT/src/Gateway/Api/Character/Craft.cs" \
   && grep -q 'MapPost("/craft/finish"' "$ROOT/src/Gateway/Api/Character/Craft.cs"; then
  ok "四条口与网关路径一致（recipes / state / start / finish）"
else
  bad "制作四条口与网关不一致"
fi

if python3 - "$CRAFT_NET" >/dev/null 2>&1 <<'PY'
import re
import sys
s = open(sys.argv[1], encoding='utf-8').read()
m = re.search(r'public static string StartBody\([^)]*\)\s*\{(.*?)\n        \}', s, re.S)
if not m:
    sys.exit(1)
body = m.group(1)
ok = ('character_id' in body and 'recipe_id' in body
      and 'txn_id' not in body and 'craft_attr' not in body and 'craft_extras' not in body)
sys.exit(0 if ok else 1)
PY
then
  ok "开始制作只带角色号与配方编号（不带事务号，也不许客户端带属性值）"
else
  bad "制作请求体里混进了客户端不该带的东西"
  grep -nE 'txn_id' "$CRAFT_NET" | head -3 | detail
fi

# 掷点只在角色服：屏上不许随机、不许读掷点表、不把成功率当成本次预告。
# 别拿 0.5 这类布局系数当证据 —— 面板宽度都是 0.5 对半分。
if ! grep -qE 'Random\.|CraftRoller|craft_roll' "$CRAFT_SCREEN" "$CRAFT_FLOW"; then
  ok "屏上与流程都不掷点（掷点只在角色服 FinishCraft 一处）"
else
  bad "客户端出现了掷点痕迹"
  grep -nE 'Random\.|CraftRoller|craft_roll' "$CRAFT_SCREEN" "$CRAFT_FLOW" | head -3 | detail
fi

if ! grep -q 'config/' "$CRAFT_SCREEN" "$CRAFT_FLOW" "$CRAFT_NET" "$BAG_SCREEN"; then
  ok "制作与背包都不读配置表（不打开 recipes.json / craft_roll.json）"
else
  bad "读了配置表"
  grep -n 'config/' "$CRAFT_SCREEN" "$CRAFT_FLOW" "$CRAFT_NET" "$BAG_SCREEN" | head -3 | detail
fi

if grep -q 'UiCraftScreen.Build' "$ROOT/client/MMOGame/Assets/HotUpdate/Ui/UiEntryHub.cs"; then
  ok "养成二级条挂上制作屏"
else
  bad "导航里没挂制作屏"
fi

# 本卡唯一的协议改动：背包槽加两个只读字段（不新增 rpc、不动既有编号）。
if grep -q 'int32 craft_attr = 6;' "$ROOT/proto/mmo/character.proto" \
   && grep -q 'repeated CraftExtra craft_extras = 7;' "$ROOT/proto/mmo/character.proto"; then
  ok "InventorySlot 带两个只读掷点字段（craft_attr=6 / craft_extras=7）"
else
  bad "InventorySlot 缺掷点字段"
fi
if grep -q 'CraftAttr = roll.Attr;' "$ROOT/src/Character/Grpc/Inventory.cs" \
   && grep -q 'craft_attr = s.CraftAttr' "$ROOT/src/Gateway/Api/Character/Inventory.cs"; then
  ok "角色服填、网关透传（只加字段、不动既有编号）"
else
  bad "掷点没有从角色服送到网关回包"
fi

for key in craft_recipes craft_start craft_state craft_finish craft_not_ready bag_roll; do
  if grep -q "u5_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "制作与掷点自检 u5_self_${key}=ok"
  else
    bad "制作与掷点自检 u5_self_${key} 没过"
    grep "u5_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

echo "== 4. 不做项 =="
# 这一条守的是「境界子阶这条链不许改配置与玩法」。D5 口径：钉内容、不拿 git diff 钉文件——
# U13 屏上依赖的是阶名与被动那两组值，它们没变就说明玩法没改；
# 别的卡往 sub_phases 加自己的键（A8 的 grand_points）不该把这条判红。
if python3 - "$ROOT/config/realms.json" <<'PY'
import json, sys
by = json.load(open(sys.argv[1], encoding="utf-8"))["sub_phases"]["by_realm"]
ok = (by["炼体"]["names"] == ["一阶", "二阶", "三阶", "四阶", "五阶", "六阶", "七阶", "八阶", "九阶"]
      and by["炼体"]["passive_pct_bps"] == [0, 30, 60, 90, 120, 150, 180, 210, 250]
      and by["*"]["names"] == ["初期", "中期", "后期", "大圆满"]
      and by["*"]["passive_pct_bps"] == [0, 80, 160, 250])
sys.exit(0 if ok else 1)
PY
then
  ok "段内阶的阶名与被动一值未改（境界子阶这条链没动玩法）"
else
  bad "境界子阶的阶名或被动被改了"
  detail "看 config/realms.json 的 sub_phases.by_realm"
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
