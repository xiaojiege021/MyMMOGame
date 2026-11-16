#!/usr/bin/env bash
# N8 世界 Boss 个人掉落（开发架构第 14 节第 87 行，依赖 N6、P3-5a）。
#   bash tools/check/check-n8.sh
#   MMO_N8_HAS_DB=1 bash tools/check/check-n8.sh
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
PROBE="$ROOT/tools/probe/BattleProbe/BattleProbe.dll"
CC="--config $ROOT/config/character.json"
HAS_DB="${MMO_N8_HAS_DB:-0}"
TITLE="N8"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p" 2>/dev/null || true; done; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Battle/Battle.csproj src/Character/Character.csproj \
  tools/probe/BattleProbe/BattleProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  if ! "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ "$BUILD_OK" = "1" ] && ok "Battle + Character + 探针编译通过" || true

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP/static.txt" <<'PY'
import json, os, re, sys
root = sys.argv[1]
out = open(sys.argv[2], "w", encoding="utf-8")

def R(k, c, i=""):
    out.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")

def rd(p):
    return open(os.path.join(root, p), encoding="utf-8").read() if os.path.exists(os.path.join(root, p)) else ""

drop_cfg = json.load(open(os.path.join(root, "config/dungeon_drops.json"), encoding="utf-8"))
wb = [e for e in drop_cfg.get("entries", []) if e.get("dungeon_id") == "world_boss"]
R("world_boss_in_drop_table", len(wb) == 1 and len(wb[0].get("lines", [])) >= 1)
R("encounter_drop_key", '"world_boss"' in rd("src/Battle/Encounter.cs")
    and "SettlementSource" in rd("src/Battle/Encounter.cs"))
R("drop_dungeon_not_real", "WorldBossDropDungeonId" in rd("src/Character/ExpSettlementRepository.cs")
    and "dungeon_unknown" in rd("src/Character/CharacterGrpcService.cs"))
R("n6_path", "MatchesReported" in rd("src/Character/DungeonDropTable.cs")
    and "SettleDropLine" in rd("src/Battle/ExpSettlement.cs"))
R("no_world_boss_in_dungeons",
    "world_boss" not in rd("config/dungeons.json") or '"id": "world_boss"' not in rd("config/dungeons.json"))
R("world_boss_table_still_no_drop",
    '"drop"' not in json.load(open(os.path.join(root, "config/world_boss.json"), encoding="utf-8")))
R("all_cards", "n8)" in rd("tools/check/check-all.sh") and re.search(r"\bn8\b", rd("tools/check/check-all.sh")))
PY
while IFS= read -r line; do
  case "$line" in
    PASS*) ok "${line#PASS }" ;;
    FAIL*) bad "${line#FAIL  }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 掉落表只读 =="
OUT="$("$DOTNET" "$CD" --dungeon-drop-table-path "$ROOT/config/dungeon_drops.json" $CC 2>&1)" || true
if printf '%s' "$OUT" | grep -q 'dungeon_drop_table_state=ok'; then
  ok "正本含 world_boss 可读"
else
  bad "读表失败" && printf '%s\n' "$OUT" | head -3 | detail
fi
printf '%s' "$OUT" | grep -q 'dungeon_drop=world_boss lines=2' \
  && ok "CLI 列出 world_boss 两行" || bad "缺少 dungeon_drop=world_boss"

echo "== 4. 真库：世界 Boss 每人一份掉落 =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真库段（MMO_N8_HAS_DB=1 且 MMO_GAMEDB 才跑）\n'
else
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
b = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
b["listen"] = "127.0.0.1:0"
b["settlement_dir"] = os.path.join(tmp, "settle")
b["character_dir"] = os.path.join(tmp, "chr")
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"), ("dungeon_table", "dungeons.json")):
    b[k] = os.path.join(root, "config", f)
json.dump(b, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 &
  PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  CHAR_PORT="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/char.log" 2>/dev/null | tail -1 | sed 's/.*://' || echo 62000)"
  python3 - "$TMP" "$CHAR_PORT" "$ROOT" <<'PY'
import json, os, sys
tmp, cp, root = sys.argv[1], sys.argv[2], sys.argv[3]
b = json.load(open(os.path.join(tmp, "battle.json"), encoding="utf-8"))
b["exp_upstream"] = f"http://127.0.0.1:{cp}"
b["character_upstream"] = f"http://127.0.0.1:{cp}"
b["dungeon_drop_table"] = os.path.join(root, "config", "dungeon_drops.json")
json.dump(b, open(os.path.join(tmp, "battle-run.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  "$DOTNET" "$BD" --config "$TMP/battle-run.json" >"$TMP/battle.log" 2>&1 &
  PIDS="$PIDS $!"
  BU="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/battle.log" | tail -1 | sed 's/^listen=//')"
  WB_BOSS="$(python3 -c 'import json; print(json.load(open("'"$ROOT/config/world_boss.json"'"))["boss"]["id"])')"
  SKILL="$(python3 -c 'import json; s=json.load(open("'"$ROOT/config/skills.json"'")); print(s["skills"][0]["id"])')"
  TS="$(date +%s)"
  ACC="n8_$TS"
  CID="$("$DOTNET" "$CD" --create-character "n8$TS" --account "$ACC" $CC 2>&1 | grep -m1 '^character_id=' | sed 's/^character_id=//')"
  if [ -z "$CID" ]; then
    bad "建角失败"
  else
    ok "建角 id=$CID"
    CLEAR="$("$DOTNET" "$PROBE" --url "${BU}/battle?worldboss=world_boss_1" \
      --case n8_wb --boss "$WB_BOSS" --hit "$SKILL" --a-char "$CID" --party-size 1 2>&1)" || CLEAR=""
    if printf '%s' "$CLEAR" | grep -q '^verdict=ok'; then
      ok "世界 Boss 单人打掉"
    else
      bad "清场失败" && printf '%s\n' "$CLEAR" | head -8 | detail
    fi
    SETTLE="$(grep "exp_settle=ok" "$TMP/battle.log" | grep " character=$CID " | head -1)"
    printf '%s' "$SETTLE" | grep -q 'source=world_boss' \
      && printf '%s' "$SETTLE" | grep -q 'dungeon=world_boss' \
      && printf '%s' "$SETTLE" | grep -q 'drops=2' \
      && ok "战斗服：来源 world_boss、掉落键 world_boss、报 2 行" \
      || bad "结算日志不对：$SETTLE"
    grep -q 'drop_lines=2' "$TMP/char.log" && ok "角色服同事务写入 2 行掉落" || bad "角色服 drop_lines 不对"
    C1="$("$DOTNET" "$CD" --item-count "$CID" crystal_attack $CC 2>&1 | grep -m1 '^item_count=' || true)"
    C2="$("$DOTNET" "$CD" --item-count "$CID" stone_enhance_low $CC 2>&1 | grep -m1 '^item_count=' || true)"
    printf '%s' "$C1" | grep -q 'item_count=1' && printf '%s' "$C2" | grep -q 'item_count=1' \
      && ok "每人背包各 +1（与表一致）" || bad "背包：$C1 ; $C2"
  fi
fi

finish "$TITLE"