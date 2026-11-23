#!/usr/bin/env bash
# N6 掉落表与副本发奖（开发架构第 14 节第 85 行，依赖 P1-1b5、P1-2a）。
#   bash tools/check/check-n6.sh
#   MMO_N6_HAS_DB=1 bash tools/check/check-n6.sh
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
AGG_CHARGRPC="$(src_agg src/Character/CharacterGrpcService.cs)"; export AGG_CHARGRPC

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
PROBE="$ROOT/tools/probe/BattleProbe/BattleProbe.dll"
CC="--config $ROOT/config/character.json"
HAS_DB="${MMO_N6_HAS_DB:-0}"
TITLE="N6"
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
import os, re, sys
root = sys.argv[1]
out = open(sys.argv[2], "w", encoding="utf-8")

def R(k, c, i=""):
    out.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")

def rd(p):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server", "CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World", "SectRepository.cs": "Sect", "SocialGrpcService.cs": "Grpc", "WorldGrpcService.cs": "Grpc"}
    _name = __import__("os").path.basename(p)
    if _name in _sub:
        _os = __import__("os"); _g = __import__("glob")
        _b = p if _os.path.isabs(p) else _os.path.join(_os.getcwd(), p)
        if not _os.path.exists(_b):
            _d = _os.path.dirname(_os.getcwd())
            while _d and not _os.path.exists(_os.path.join(_d, p)):
                _nd = _os.path.dirname(_d)
                if _nd == _d: break
                _d = _nd
            _b = _os.path.join(_d, p)
        return "".join(open(f, encoding="utf-8").read() for f in
                       [_b] + sorted(_g.glob(_os.path.join(_os.path.dirname(_b), _sub[_name], "*.cs"))))
    if p.endswith("src/Character/Program.cs"):
        # D1a 把 CLI 拆进 Cli/：静态断言按 Program + Cli/* 合起来看（名字与内容未变）。
        return "".join(open(x, encoding="utf-8").read() for x in
                       [os.path.join(root, p)] + sorted(__import__("glob").glob(os.path.join(root, "tools/probe/CliProbe/*.cs"))))
    return open(os.path.join(root, p), encoding="utf-8").read() if os.path.exists(os.path.join(root, p)) else ""

drop_cfg = rd("config/dungeon_drops.json")
exp_repo = rd("src/Character/ExpSettlementRepository.cs")
battle_enc = rd("src/Battle/Encounter.cs")
battle_exp = rd("src/Battle/ExpSettlement.cs")
grpc = rd(os.environ["AGG_CHARGRPC"])
proto = rd("proto/mmo/character.proto")
allcards = rd("tools/check/check-all.sh")

R("config_present", "dungeon-drops/1" in drop_cfg and "stone_enhance_low" in drop_cfg)
R("battle_reports_drops", "DungeonDrops.LinesFor" in battle_enc and "SettleDropLine" in battle_exp)
R("grant_in_settle", "TryClaimTxnInTransactionAsync" in exp_repo and "PlanItemDeltas" in rd("src/Character/GrantRepository.cs"))
R("drop_validate", "drop_invalid" in grpc and "MatchesReported" in rd("src/Shared.Domain/DungeonDropTable.cs"))
R("proto_drops", "SettleDropLine" in proto and "repeated SettleDropLine drops = 9" in proto)
R("shared_gen", "SettleDropLine" in rd("src/Shared/Generated/Character.cs"))
R("no_client_hotfix", "dungeon_drops" not in rd("client/MMOGame/Assets/HotUpdate/Ui/UiSettleScreen.cs"))
R("all_cards", "n6)" in allcards and re.search(r"\bn6\b", allcards))
R("spirit_ban", "stone_1" in rd("src/Shared.Domain/DungeonDropTable.cs"))
R("cli_drop_table", "--dungeon-drop-table-path" in rd("src/Character/Program.cs"))
PY
while IFS= read -r line; do
  case "$line" in
    PASS*) ok "${line#PASS }" ;;
    FAIL*) bad "${line#FAIL  }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 坏配置 =="
BAD="$TMP/bad-drop.json"
printf '%s\n' '{"schema":"dungeon-drops/1","version":1,"note":"x","items_config":"items.json","entries":[{"dungeon_id":"main","lines":[{"item_id":"stone_1","count":1}]}]}' >"$BAD"
OUT="$("$DOTNET" "$CD" --dungeon-drop-table-path "$BAD" $CC 2>&1)" || true
if printf '%s' "$OUT" | grep -q 'config_error='; then
  ok "灵石币种进掉落表起不来"
else
  bad "坏配置没拦住" && printf '%s\n' "$OUT" | head -3 | detail
fi
GOOD_OUT="$("$DOTNET" "$CD" --dungeon-drop-table-path "$ROOT/config/dungeon_drops.json" $CC 2>&1)" || true
if printf '%s' "$GOOD_OUT" | grep -q 'dungeon_drop_table_state=ok'; then
  ok "正本掉落表可读"
else
  bad "正本读失败" && printf '%s\n' "$GOOD_OUT" | head -3 | detail
fi

echo "== 4. 真库：通关发掉落 + 幂等 =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真库段（MMO_N6_HAS_DB=1 且 MMO_GAMEDB 才跑）\n'
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
  python3 - "$TMP" "$CHAR_PORT" <<'PY'
import json, os, sys
tmp, cp = sys.argv[1], sys.argv[2]
b = json.load(open(os.path.join(tmp, "battle.json"), encoding="utf-8"))
b["exp_upstream"] = f"http://127.0.0.1:{cp}"
b["character_upstream"] = f"http://127.0.0.1:{cp}"
json.dump(b, open(os.path.join(tmp, "battle-run.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  "$DOTNET" "$BD" --config "$TMP/battle-run.json" >"$TMP/battle.log" 2>&1 &
  PIDS="$PIDS $!"
  BU="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/battle.log" | tail -1 | sed 's/^listen=//')"
  BOSS="$(python3 -c 'import json; print(json.load(open("'"$ROOT/config/battle.json"'"))["boss"]["id"])')"
  SKILL="$(python3 -c 'import json; s=json.load(open("'"$ROOT/config/skills.json"'")); print(s["skills"][0]["id"])')"
  TS="$(date +%s)"
  ACC="n6_$TS"
  PASS="n6-Pw-$TS"
  printf '%s\n' "$PASS" | "$DOTNET" "$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll" \
    --create-account "$ACC" --config "$ROOT/config/gateway.json" 2>/dev/null || true
  CID="$("$DOTNET" "$CD" --create-character "n6$TS" --account "$ACC" $CC 2>&1 | grep -m1 '^character_id=' | sed 's/^character_id=//')"
  if [ -z "$CID" ]; then
    bad "建角失败"
  else
    ok "建角 id=$CID"
    CLEAR="$("$DOTNET" "$PROBE" --url "${BU}/battle?dungeon=abyss&party_size=1" \
      --case n6_clear --boss "$BOSS" --hit "$SKILL" --a-char "$CID" --party-size 1 2>&1)"
  if printf '%s' "$CLEAR" | grep -q '^verdict=ok'; then
      ok "深渊副本通关"
    else
      bad "通关失败" && printf '%s\n' "$CLEAR" | head -6 | detail
    fi
    grep -q 'drops=1' "$TMP/battle.log" && grep -q 'drop_lines=1' "$TMP/char.log" \
      && ok "战斗服报掉落、角色服写入" \
      || bad "掉落日志不对"
    INV="$("$DOTNET" "$CD" --item-count "$CID" stone_enhance_low $CC 2>&1 | grep -m1 '^item_count=' || true)"
    [ "$INV" = "item_count=1 id=$CID item=stone_enhance_low" ] \
      && ok "背包 1 颗低级强化石" || bad "背包不对：$INV"
    TXN="$(grep -m1 'exp_settle=ok' "$TMP/battle.log" | sed -n 's/.*txn=\([^ ]*\).*/\1/p')"
    TC="$("$DOTNET" "$CD" --txn-count "$TXN" $CC 2>&1 | grep -m1 '^txn_count=' || true)"
    [ "$TC" = "txn_count=1 txn=$TXN" ] && ok "grant_txn 幂等占一行" || bad "txn_count 不对：$TC"
  fi
fi

finish "$TITLE"