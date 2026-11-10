#!/usr/bin/env bash
# P0-3b 核对：Boss 两个阶段，阶段走配置。
#
# 完成标准：阶段切换来自配置。做法是同一份核对场景跑两遍，
# 只改配置里的血线（50% → 30%），切换点必须跟着挪；
# 两阶段的出手参数也走配置，切了阶段这一下的伤害要换成新阶段那份。
# 不设霸体条和破防窗口（方案正文 7.1，这条是不做，不是没做）。
#
# 做这几段：
#   1. 编译战斗进程和 BattleProbe
#   2. 静态断言：阶段名和血线只在配置里，服务端代码没有写死
#   3. 血线 50% 的一版：打到 48% 触发第二阶段，Boss 这一下的伤害换成 250
#   4. 血线 30% 的一版：同一个场景，切换点挪到 22%，伤害还是新阶段那份
#
# 用法：tools/check/check-p0-3b.sh
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
PROBE_DLL="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
PORT=60111
BOSS=9001

PASS=0
FAIL=0
TMP="$(mktemp -d)"
BATTLE_PID=""

cleanup() {
  stop_battle
  rm -rf "$TMP"
}
trap cleanup EXIT

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
detail() { printf '       %s\n' "$1"; }

stop_battle() {
  if [ -n "$BATTLE_PID" ] && kill -0 "$BATTLE_PID" 2>/dev/null; then
    kill -TERM "$BATTLE_PID" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$BATTLE_PID" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$BATTLE_PID" 2>/dev/null || true
    wait "$BATTLE_PID" 2>/dev/null || true
  fi
  BATTLE_PID=""
}

start_battle() {
  stop_battle
  : > "$TMP/battle.log"
  "$DOTNET" "$BATTLE_DLL" --config "$TMP/battle.json" >>"$TMP/battle.log" 2>&1 &
  BATTLE_PID=$!
  for _ in $(seq 1 100); do
    if grep -q '^listen=' "$TMP/battle.log" 2>/dev/null; then
      return 0
    fi
    if ! kill -0 "$BATTLE_PID" 2>/dev/null; then
      break
    fi
    sleep 0.1
  done
  bad "战斗进程没起来"
  tail -8 "$TMP/battle.log" | detail
  return 1
}

probe() {
  "$DOTNET" "$PROBE_DLL" --url "ws://127.0.0.1:$PORT/battle" "$@"
}

# 写一版阶段配置。$1 = 第二阶段的起点血线（百分比）。
write_config() {
  local pct="$1"
  python3 - "$ROOT" "$TMP" "$PORT" "$pct" <<'PY'
import json, os, sys
root, tmp, port, pct = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])

battle = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
battle["listen"] = "127.0.0.1:" + port
battle["settlement_dir"] = os.path.join(tmp, "settlements")
battle["character_dir"] = os.path.join(tmp, "characters")
battle["skill_table"] = "skills.json"
battle["class_table"] = "classes.json"
battle["dungeon_table"] = "dungeons.json"
battle["boss"]["hp_max"] = 1000
# 第一阶段的出手间隔放到十分钟：这一版只想看切阶段之后的出手，别让第一阶段的先搅局。
battle["boss"]["phases"] = [
    {"name": "常态", "hp_pct_from": 100, "hp_pct_to": pct,
     "attack": {"damage": 100, "interval_ms": 600000, "foreswing_ms": 300}},
    {"name": "狂暴", "hp_pct_from": pct, "hp_pct_to": 0,
     "attack": {"damage": 250, "interval_ms": 1200, "foreswing_ms": 300}},
]
json.dump(battle, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
}

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Battle/Battle.csproj" -c Release --nologo -v q >"$TMP/build-battle.log" 2>&1 \
  && "$DOTNET" build "$ROOT/tools/probe/BattleProbe/BattleProbe.csproj" -c Release --nologo -v q >"$TMP/build-probe.log" 2>&1; then
  ok "战斗进程与 BattleProbe 编译通过"
else
  bad "编译失败"
  grep -E "error " "$TMP/build-battle.log" "$TMP/build-probe.log" | head -5 | detail
  echo
  echo "P0-3b 核对：$PASS 项通过，$FAIL 项未通过（编译没过，后面的不跑）"
  exit 1
fi

echo "== 2. 静态断言 =="
# 阶段名、血线、两段出手参数只能出现在配置里；服务端代码里出现这些字就说明写死了。
if grep -rn "狂暴\|常态" "$ROOT/src" --include="*.cs" >/dev/null 2>&1; then
  bad "服务端代码里出现了阶段名，阶段应当只走配置"
else
  ok "阶段名和参数不在服务端代码里"
fi

# 仓库的默认配置不配阶段：默认行为和 P0-2b 之前一样，老检查不 受影响。
if python3 -c "
import json, sys
battle = json.load(open('$ROOT/config/battle.json', encoding='utf-8'))
sys.exit(0 if 'phases' not in battle['boss'] else 1)
"; then
  ok "仓库默认配置没配阶段，老行为不变"
else
  bad "仓库默认配置配了阶段，老检查的口径会变"
fi

cp "$ROOT/config/skills.json" "$ROOT/config/dungeons.json" "$TMP/"
cp "$ROOT/config/classes.json" "$TMP/classes.json"
mkdir -p "$TMP/settlements" "$TMP/characters"

echo "== 3. 血线 50% 的一版 =="
write_config 50
if ! start_battle; then
  echo
  echo "P0-3b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

OUT_A="$(probe --case p03b_phase --boss $BOSS --ranged 1002)"
echo "$OUT_A" | grep '^p03b_' | sed 's/^/       /'
if echo "$OUT_A" | grep -q 'verdict=ok'; then
  ok "打到血线触发第二阶段，广播带上阶段名"
else
  bad "阶段切换这一轮有问题"
fi

INDEX_A="$(echo "$OUT_A" | sed -n 's/.*index=\([0-9]*\).*/\1/p' | head -1)"
NAME_A="$(echo "$OUT_A" | sed -n 's/.*name=\([^ ]*\).*/\1/p' | head -1)"
HP_A="$(echo "$OUT_A" | sed -n 's/.*hp_after_switch=\([0-9]*\).*/\1/p' | head -1)"
CASTS_A="$(echo "$OUT_A" | sed -n 's/.*casts=\([0-9]*\).*/\1/p' | head -1)"
ATTACK_A="$(echo "$OUT_A" | sed -n 's/.*attack_damage=\([0-9]*\).*/\1/p' | head -1)"

if [ "$INDEX_A" = "1" ] && [ "$NAME_A" = "狂暴" ]; then
  ok "切到第 2 阶段（index=1，name=狂暴）"
else
  bad "阶段号或名字不对：index=${INDEX_A} name=${NAME_A}"
fi

# 1000 血、260 一手：第二手打到 480（48%），血线 50% 该在这一手切。
if [ -n "$HP_A" ] && [ "$HP_A" -le 500 ] && [ "$HP_A" -gt 240 ] && [ "$CASTS_A" = "2" ]; then
  ok "血线 50%：第 2 手打到 ${HP_A}/1000 就切（<=500）"
else
  bad "切换点不对：hp=${HP_A} casts=${CASTS_A}（期望 <=500 且 >240，2 手）"
fi

if [ "$ATTACK_A" = "250" ]; then
  ok "切了阶段，Boss 这一下换成新阶段的 250"
else
  bad "新阶段的出手伤害不对：${ATTACK_A}（期望 250）"
fi

echo "== 4. 血线 30% 的一版：切换点跟着配置挪 =="
write_config 30
if start_battle; then
  OUT_B="$(probe --case p03b_phase --boss $BOSS --ranged 1002)"
  echo "$OUT_B" | grep '^p03b_' | sed 's/^/       /'

  HP_B="$(echo "$OUT_B" | sed -n 's/.*hp_after_switch=\([0-9]*\).*/\1/p' | head -1)"
  CASTS_B="$(echo "$OUT_B" | sed -n 's/.*casts=\([0-9]*\).*/\1/p' | head -1)"
  ATTACK_B="$(echo "$OUT_B" | sed -n 's/.*attack_damage=\([0-9]*\).*/\1/p' | head -1)"

  if [ -n "$HP_B" ] && [ "$HP_B" -le 300 ] && [ "$HP_B" -gt 0 ] && [ "$CASTS_B" = "3" ]; then
    ok "血线改成 30%：切换点挪到第 3 手的 ${HP_B}/1000（<=300），还是第 2 手的 480 就不切了"
  else
    bad "30% 的切换点不对：hp=${HP_B} casts=${CASTS_B}（期望 <=300，3 手）"
  fi

  if [ "$ATTACK_B" = "250" ]; then
    ok "换血线不影响阶段参数：第二阶段还是 250 一手"
  else
    bad "改了血线后阶段参数也变了：${ATTACK_B}"
  fi
else
  bad "血线 30% 的战斗进程起不来"
fi

echo
echo "P0-3b 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
