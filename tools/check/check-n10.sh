#!/usr/bin/env bash
# N10 段内四阶与被动（开发架构第 14 节第 89 行，依赖 P3-1a2）。
#   bash tools/check/check-n10.sh
#   MMO_N10_HAS_DB=1 bash tools/check/check-n10.sh
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
CC="--config $ROOT/config/character.json"
HAS_DB="${MMO_N10_HAS_DB:-0}"
TITLE="N10"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p" 2>/dev/null || true; done; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
if ! "$DOTNET" build src/Character/Character.csproj -c Release --nologo -v q >"$TMP/build-ch.log" 2>&1; then
  BUILD_OK=0
  bad "编译失败：Character"
  grep -E 'error ' "$TMP/build-ch.log" | head -4 | detail
fi
[ "$BUILD_OK" = "1" ] && ok "Character 编译通过" || true

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP/static.txt" <<'PY'
import json, os, re, sys
root = sys.argv[1]
out = open(sys.argv[2], "w", encoding="utf-8")

def R(k, c, i=""):
    out.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")

def rd(p):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server", "CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World", "SectRepository.cs": "Sect", "SocialGrpcService.cs": "Grpc", "WorldGrpcService.cs": "Grpc", "Program.cs": "Cli"}
    _name = __import__("os").path.basename(p)
    if _name in _sub:
        # 14.8 拆分卡之后：主文件 + 分片目录一起读（断言文本不变，只改读法）。
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
    return open(os.path.join(root, p), encoding="utf-8").read() if os.path.exists(os.path.join(root, p)) else ""

realms = json.load(open(os.path.join(root, "config/realms.json"), encoding="utf-8"))
sub = realms.get("sub_phases", {})
by = sub.get("by_realm", {})
lian = by.get("炼体", {})
default = by.get("*", {})
R("sub_names", lian.get("names") == ["一阶", "二阶", "三阶", "四阶", "五阶", "六阶", "七阶", "八阶", "九阶"]
  and default.get("names") == ["初期", "中期", "后期", "大圆满"])
R("passive_full", len(lian.get("passive_pct_bps", [])) == 9
  and len(default.get("passive_pct_bps", [])) == 4
  and 200 <= default.get("passive_pct_bps", [0])[-1] <= 300)
R("breakthrough_index", lian.get("breakthrough_min_index") == 8
  and default.get("breakthrough_min_index") == 2)
R("version", realms.get("version", 0) >= 4)

# A8：大圆满可分配点。炼体末阶是九阶、不是大圆满，配 0；其余每段末阶是大圆满，至少 1 点。
gp_lian = lian.get("grand_points", None)
gp_default = default.get("grand_points", None)
R("grand_points_config", gp_lian == 0 and isinstance(gp_default, int) and 1 <= gp_default <= 50,
  "炼体=%s 默认=%s" % (gp_lian, gp_default))


phase = rd("src/Character/RealmPhaseRepository.cs")
adv = rd("src/Character/RealmAdvanceRepository.cs")
rt = rd("src/Character/RealmTable.cs")
proto = rd("proto/mmo/character.proto")
gwsrc = ""
for dp, _, fs in os.walk(os.path.join(root, "src/Gateway")):
    if "/obj" in dp or "/bin" in dp:
        continue
    for f in fs:
        if not f.endswith(".cs"):
            continue
        for line in open(os.path.join(dp, f), encoding="utf-8").read().splitlines():
            s = line.strip()
            # 注释里写「不读 config/realms.json」这类说明不算读配置。
            if s.startswith("//") or s.startswith("*") or s.startswith("/*"):
                continue
            gwsrc += line + "\n"
allcards = rd("tools/check/check-all.sh")

R("ledger", "realm_phase_promote" in phase)
# A8：发点台账与属性点列都在仓储里；点数只在角色服算（战斗服一行都不读）。
R("points_ledger", "realm_phase_points" in phase and "attr_points" in phase)
R("points_proto", "int32 attr_points = 6" in proto and "int32 points_awarded = 10" in proto)
R("points_gateway_readonly", "/character/attr-points" in gwsrc and "attr_points" in gwsrc)
battle = ""
for dp, _, fs in os.walk(os.path.join(root, "src/Battle")):
    if "/obj" in dp or "/bin" in dp:
        continue
    for f in fs:
        if f.endswith(".cs"):
            battle += open(os.path.join(dp, f), encoding="utf-8").read()
R("points_only_character", "attr_points" not in battle and "realm_phase_points" not in battle)
R("promote_gate", "level_not_enough" in phase and "realm_phase_max" in phase)
R("advance_gate", "realm_phase_not_enough" in adv and "realm_phase = 0" in adv)
R("sub_model", "RealmSubPhases" in rt and "PassivePctBps" in rt)
R("proto", "rpc PromoteRealmPhase" in proto and "rpc DescribeRealmPassive" in proto)
R("gateway_no_local_passive", "realms.json" not in gwsrc and "RealmSubPhases" not in gwsrc)
R("no_client_passive", "passive_pct" not in rd("client/MMOGame/Assets/HotUpdate/Ui/UiEntrySelfCheck.cs"))
R("all_cards", "n10)" in allcards and re.search(r"\bn10\b", allcards))
PY
while IFS= read -r line; do
  case "$line" in
    PASS*) ok "${line#PASS }" ;;
    FAIL*) bad "${line#FAIL  }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 坏配置 =="
BAD="$TMP/bad-realms.json"
python3 - "$ROOT/config/realms.json" "$BAD" <<'PY'
import copy, json, sys
j = json.load(open(sys.argv[1], encoding="utf-8"))
j["sub_phases"]["by_realm"]["*"]["passive_pct_bps"] = [0, 50, 100, 500]
json.dump(j, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
OUT="$("$DOTNET" "$CD" --realm-table-path "$BAD" "$ROOT/config/dungeons.json" $CC 2>&1)" || true
if printf '%s' "$OUT" | grep -q 'config_error='; then
  ok "大圆满 passive 超限时起不来"
else
  bad "坏 sub_phases 没拦住"
fi

echo "== 4. 缺 sub_phases =="
NO="$TMP/no-sub.json"
python3 - "$ROOT/config/realms.json" "$NO" <<'PY'
import json, sys
j = json.load(open(sys.argv[1], encoding="utf-8"))
j.pop("sub_phases", None)
json.dump(j, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
OUT2="$("$DOTNET" "$CD" --realm-table-path "$NO" "$ROOT/config/dungeons.json" $CC 2>&1)" || true
printf '%s' "$OUT2" | grep -q 'config_error=' && ok "缺 sub_phases 起不来" || bad "缺 sub_phases 没报错"

GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CG="--config $ROOT/config/gateway.json"

echo "== 4b. A8 坏配置：末阶不是大圆满却配了点 / 大圆满不给点 / 缺字段 =="
bad_realm() {
  # bad_realm <说明> <python 改法>
  local label="$1" mutator="$2"
  local f="$TMP/bad-gp.json"
  python3 - "$ROOT/config/realms.json" "$f" "$mutator" <<'PY'
import json, sys
j = json.load(open(sys.argv[1], encoding="utf-8"))
by = j["sub_phases"]["by_realm"]
exec(sys.argv[3])
json.dump(j, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
  local out
  out="$("$DOTNET" "$CD" --realm-table-path "$f" "$ROOT/config/dungeons.json" $CC 2>&1)" || true
  if printf '%s' "$out" | grep -q 'config_error=.*grand_points'; then
    ok "$label"
  else
    bad "$label —— 没拦住：$(printf '%s' "$out" | grep -m1 'config_error=' || printf '%s' "$out" | head -1)"
  fi
}
bad_realm "炼体（末阶九阶）配 1 点起不来" 'by["炼体"]["grand_points"] = 1'
bad_realm "大圆满配 0 点起不来" 'by["*"]["grand_points"] = 0'
bad_realm "缺 grand_points 起不来" 'by["*"].pop("grand_points", None)'
bad_realm "grand_points 越界（51）起不来" 'by["*"]["grand_points"] = 51'

if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  echo "（跳过真库段：MMO_N10_HAS_DB=1 且配置 MMO_GAMEDB）"
  finish "$TITLE"
  exit 0
fi

echo "== 5. 真服真库 =="
"$DOTNET" build src/Gateway/Gateway.csproj -c Release --nologo -v q >/dev/null 2>&1 || true
"$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/account.sql" $CG >"$TMP/mig-account.log" 2>&1 || true
"$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/character.sql" $CC >"$TMP/mig-char.log" 2>&1 || true
"$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 &
PIDS="$PIDS $!"
wait_ready "$TMP/char.log" '^ready' "角色服" || true
"$DOTNET" "$GD" $CG >"$TMP/gw.log" 2>&1 &
PIDS="$PIDS $!"
for _ in $(seq 1 120); do
  code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
    -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
  [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
done
ACCT="n10_$$"; PW="N10-$$-$(date +%s)"
printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" $CG >/dev/null 2>&1
TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
  -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
[ -n "$TOK" ] && ok "登录" || bad "登录失败"
TX="n10-$$-$(date +%s)"
ID="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"n10$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin).get("character_id",""))' 2>/dev/null)"
[ -n "$ID" ] && ok "建角 id=$ID" || { bad "建角失败"; finish "$TITLE"; exit 1; }

PS="$("$DOTNET" "$CD" --realm-passive-state "$ID" $CC 2>&1 | grep -m1 '^realm_passive_state=')"
printf '%s' "$PS" | grep -q "realm=炼体 realm_phase=0 phase_name=一阶 passive_pct_bps=0" \
  && ok "新建角色炼体一阶、被动 0" || bad "被动读口不对：$PS"

"$DOTNET" "$CD" --raw-level-write "$ID" 6 $CC >/dev/null
P1="$("$DOTNET" "$CD" --promote-realm-phase "$ID" "$TX-p1" $CC 2>&1 | grep -m1 '^promote_realm_phase=')"
printf '%s' "$P1" | grep -q 'executed=true .*to=1 phase_name=二阶 passive_pct_bps=30' \
  && ok "升到炼体二阶" || bad "升阶失败：$P1"

Pdup="$("$DOTNET" "$CD" --promote-realm-phase "$ID" "$TX-p1" $CC 2>&1 | grep -m1 '^promote_realm_phase=')"
printf '%s' "$Pdup" | grep -q 'duplicate=true' \
  && ok "升阶事务号幂等" || bad "升阶幂等失败：$Pdup"

"$DOTNET" "$CD" --raw-main-stage-write "$ID" 9 $CC >/dev/null
"$DOTNET" "$CD" --raw-drill-write "$ID" 100 $CC >/dev/null
ADV0="$("$DOTNET" "$CD" --advance-realm "$ID" "$TX-adv0" $CC 2>&1 | grep -m1 '^advance_realm=')"
printf '%s' "$ADV0" | grep -q 'reason=realm_phase_not_enough' \
  && ok "炼体二阶不能突破筑基" || bad "突破门槛不对：$ADV0"

"$DOTNET" "$CD" --raw-level-write "$ID" 11 $CC >/dev/null
"$DOTNET" "$CD" --promote-realm-phase "$ID" "$TX-p2" $CC >/dev/null
PS2="$("$DOTNET" "$CD" --realm-passive-state "$ID" $CC 2>&1 | grep -m1 '^realm_passive_state=')"
printf '%s' "$PS2" | grep -q 'realm_phase=2 phase_name=三阶 passive_pct_bps=60' \
  && ok "到炼体三阶被动 60bps" || bad "三阶被动不对：$PS2"

echo "== 6. A8 大圆满发点 =="
# 每段给几点现取配置，不写死 5。
GP="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1], encoding="utf-8"))["sub_phases"]["by_realm"]["*"]["grand_points"])' "$ROOT/config/realms.json")"
GP2=$((GP * 2))
# 换到筑基（四阶境界），直写到大圆满前一阶再按正常升阶走一次。
"$DOTNET" "$CD" --raw-realm-write "$ID" 筑基 $CC >/dev/null
"$DOTNET" "$CD" --raw-realm-phase-write "$ID" 2 $CC >/dev/null
"$DOTNET" "$CD" --raw-level-write "$ID" 15 $CC >/dev/null
GP0="$("$DOTNET" "$CD" --realm-passive-state "$ID" $CC 2>&1 | grep -m1 '^realm_passive_state=')"
printf '%s' "$GP0" | grep -q 'phase_name=后期 .*attr_points=0' \
  && ok "大圆满之前手上 0 点" || bad "大圆满前点数不对：$GP0"

G1="$("$DOTNET" "$CD" --promote-realm-phase "$ID" "$TX-gp1" $CC 2>&1 | grep -m1 '^promote_realm_phase=')"
printf '%s' "$G1" | grep -q "executed=true .*to=3 phase_name=大圆满 .*points_awarded=$GP attr_points=$GP" \
  && ok "升到大圆满发 5 点（配置现值）" || bad "大圆满发点不对：$G1"

GDUP="$("$DOTNET" "$CD" --promote-realm-phase "$ID" "$TX-gp1" $CC 2>&1 | grep -m1 '^promote_realm_phase=')"
printf '%s' "$GDUP" | grep -q "duplicate=true .*points_awarded=0 attr_points=$GP" \
  && ok "同一个升阶事务号重放不发第二次" || bad "重放发重了：$GDUP"

GMAX="$("$DOTNET" "$CD" --promote-realm-phase "$ID" "$TX-gp2" $CC 2>&1 | grep -m1 '^promote_realm_phase=')"
printf '%s' "$GMAX" | grep -q "reason=realm_phase_max .*points_awarded=0 attr_points=$GP" \
  && ok "已在大圆满再升不发点" || bad "大圆满重复发点：$GMAX"

# 存量补齐：直写成大圆满（没走过升阶），下一次升阶动作把这一段补上。
"$DOTNET" "$CD" --raw-realm-write "$ID" 结丹 $CC >/dev/null
"$DOTNET" "$CD" --raw-realm-phase-write "$ID" 3 $CC >/dev/null
GB="$("$DOTNET" "$CD" --promote-realm-phase "$ID" "$TX-gp3" $CC 2>&1 | grep -m1 '^promote_realm_phase=')"
printf '%s' "$GB" | grep -q "reason=realm_phase_max .*points_awarded=$GP attr_points=$GP2" \
  && ok "存量已在大圆满的一段补发一次（$GP → $GP2）" || bad "存量补齐不对：$GB"

# 炼体末阶是九阶、不是大圆满：不发点。
"$DOTNET" "$CD" --raw-realm-write "$ID" 炼体 $CC >/dev/null
"$DOTNET" "$CD" --raw-realm-phase-write "$ID" 8 $CC >/dev/null
GL="$("$DOTNET" "$CD" --promote-realm-phase "$ID" "$TX-gp4" $CC 2>&1 | grep -m1 '^promote_realm_phase=')"
printf '%s' "$GL" | grep -q "reason=realm_phase_max .*points_awarded=0 attr_points=$GP2" \
  && ok "炼体九阶不给点" || bad "炼体也发点了：$GL"

AP="$(curl -s --noproxy '*' "http://127.0.0.1:61000/character/attr-points?character_id=$ID&token=$TOK" 2>/dev/null)"
printf '%s' "$AP" | grep -q '"ok":true' && printf '%s' "$AP" | grep -q "\"attr_points\":$GP2" \
  && ok "只读口 /character/attr-points 回 $GP2 点" || bad "只读口不对：$AP"

finish "$TITLE"