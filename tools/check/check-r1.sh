#!/usr/bin/env bash
# R1：炼体 1–9 阶，炼体之后才分初 / 中 / 后 / 大圆满（开发架构 14.5 表第 0 行，依赖 N10）。
#   bash tools/check/check-r1.sh
#   MMO_R1_HAS_DB=1 bash tools/check/check-r1.sh
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
HAS_DB="${MMO_R1_HAS_DB:-0}"
TITLE="R1"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p" 2>/dev/null || true; done; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
if "$DOTNET" build src/Character/Character.csproj -c Release --nologo -v q >"$TMP/build-ch.log" 2>&1; then
  ok "Character 编译通过"
else
  bad "编译失败：Character"
  grep -E 'error ' "$TMP/build-ch.log" | head -4 | detail
fi

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP/static.txt" <<'PY'
import json, os, re, subprocess, sys
root = sys.argv[1]
out = open(sys.argv[2], "w", encoding="utf-8")

def R(k, c, i=""):
    out.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + str(i)) + "\n")

def rd(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

realms = json.load(open(os.path.join(root, "config/realms.json"), encoding="utf-8"))
sub = realms.get("sub_phases", {})
by = sub.get("by_realm", {})
lian = by.get("炼体", {})
default = by.get("*", {})
R("schema_4", realms.get("schema") == "realm-table/4", realms.get("schema"))
R("version_4", realms.get("version", 0) >= 4, realms.get("version"))
R("by_realm_star", "*" in by)
R("lian_names", lian.get("names") == ["一阶", "二阶", "三阶", "四阶", "五阶", "六阶", "七阶", "八阶", "九阶"])
R("lian_passive", lian.get("passive_pct_bps") == [0, 30, 60, 90, 120, 150, 180, 210, 250])
R("lian_promote", lian.get("promote_min_level") == [1, 6, 11, 16, 21, 26, 31, 36, 41])
R("lian_breakthrough", lian.get("breakthrough_min_index") == 8)
R("default_names", default.get("names") == ["初期", "中期", "后期", "大圆满"])
R("default_passive", default.get("passive_pct_bps") == [0, 80, 160, 250])
R("default_promote", default.get("promote_min_level") == [1, 5, 10, 15])
R("default_breakthrough", default.get("breakthrough_min_index") == 2)

rt = rd("src/Character/RealmTable.cs")
phase = rd("src/Character/RealmPhaseRepository.cs")
adv = rd("src/Character/RealmAdvanceRepository.cs")
R("by_realm_model", "SubPhasesFor(" in rt and "SubPhasesByRealm" in rt)
R("no_phase_count", "PhaseCount = 4" not in rt and "RealmSubPhases.PhaseCount" not in rt
  and "RealmSubPhases.PhaseCount" not in phase and "RealmSubPhases.PhaseCount" not in adv)
R("repository_by_realm", "SubPhasesFor(realm)" in phase and "SubPhasesFor(realm)" in adv)
R("phase_count_prop", "int Count => Names.Count" in rt and "phase >= sub.Count - 1" in phase)

ddl = rd("db/character/realm_phase.sql")
main = rd("db/character/character.sql")
for name, text in (("realm_phase.sql", ddl), ("character.sql", main)):
    R("ddl_phase_range_" + name,
      "realm_phase >= 0 AND realm_phase <= 8" in text
      and "from_phase >= 0 AND from_phase <= 8" in text
      and "to_phase >= 1 AND to_phase <= 8" in text)

dirty = subprocess.run(
    ["git", "-C", root, "status", "--porcelain", "--", "proto", "client"],
    capture_output=True, text=True).stdout.strip()
R("proto_client_untouched", dirty == "", dirty)
PY
while IFS= read -r line; do
  case "$line" in
    PASS*) ok "${line#PASS }" ;;
    FAIL*) bad "${line#FAIL  }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 坏配置起不来 =="
python3 - "$ROOT/config/realms.json" "$TMP" <<'PY'
import copy, json, os, sys
base = json.load(open(sys.argv[1], encoding="utf-8"))
tmp = sys.argv[2]

def dump(name, obj):
    json.dump(obj, open(os.path.join(tmp, name), "w", encoding="utf-8"), ensure_ascii=False)

a = copy.deepcopy(base)
for key in ("names", "passive_pct_bps", "promote_min_level"):
    a["sub_phases"]["by_realm"]["炼体"][key] = a["sub_phases"]["by_realm"]["炼体"][key][:8]
a["sub_phases"]["by_realm"]["炼体"]["breakthrough_min_index"] = 7
dump("bad-lian8.json", a)

b = copy.deepcopy(base)
b["sub_phases"]["by_realm"]["炼体"]["breakthrough_min_index"] = 9
dump("bad-index.json", b)

c = copy.deepcopy(base)
c["sub_phases"]["by_realm"].pop("*", None)
dump("bad-no-star.json", c)
PY
for c in lian8 index no-star; do
  out="$("$DOTNET" "$CD" --realm-table-path "$TMP/bad-$c.json" "$ROOT/config/dungeons.json" $CC 2>&1)"
  assert_bad_config "$out" "坏配置 ${c}：起不来（config_error=）"
done

echo "== 4. 无库段：--realm-table 炼体 9 阶 / 默认 4 阶 =="
"$DOTNET" "$CD" --realm-table $CC >"$TMP/rt.txt" 2>&1
python3 - "$TMP/rt.txt" "$TMP/rt-check.txt" <<'PY'
import re, sys
lines = open(sys.argv[1], encoding="utf-8").read().splitlines()
out = open(sys.argv[2], "w", encoding="utf-8")

def R(k, c, i=""):
    out.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + str(i)) + "\n")

groups = {}
for line in lines:
    m = re.match(r"^sub_phase=([^:]+):(\d+) name=(\S+) passive_pct_bps=(-?\d+) promote_min_level=(-?\d+)$", line)
    if m:
        key = m.group(1)
        groups.setdefault(key, []).append(
            (int(m.group(2)), m.group(3), int(m.group(4)), int(m.group(5))))

lian = sorted(groups.get("炼体", []))
default = sorted(groups.get("*", []))
R("lian_count_9", len(lian) == 9, len(lian))
R("default_count_4", len(default) == 4, len(default))
R("lian_names", [x[1] for x in lian] == ["一阶", "二阶", "三阶", "四阶", "五阶", "六阶", "七阶", "八阶", "九阶"])
R("lian_passive", [x[2] for x in lian] == [0, 30, 60, 90, 120, 150, 180, 210, 250])
R("lian_promote", [x[3] for x in lian] == [1, 6, 11, 16, 21, 26, 31, 36, 41])
R("default_names", [x[1] for x in default] == ["初期", "中期", "后期", "大圆满"])
R("default_passive", [x[2] for x in default] == [0, 80, 160, 250])
R("default_promote", [x[3] for x in default] == [1, 5, 10, 15])
R("group_lian", "sub_phase_group=炼体 count=9 breakthrough_min_index=8" in lines)
R("group_star", "sub_phase_group=* count=4 breakthrough_min_index=2" in lines)
R("state_version", any(l.startswith("realm_table_state=ok version=4 ") for l in lines))
PY
while IFS= read -r line; do
  case "$line" in
    PASS*) ok "${line#PASS }" ;;
    FAIL*) bad "${line#FAIL  }" ;;
  esac
done < "$TMP/rt-check.txt"

if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  echo "（跳过真库段：MMO_R1_HAS_DB=1 且配置 MMO_GAMEDB）"
  finish "$TITLE"
  exit 0
fi

echo "== 5. 真服真库 =="
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CG="--config $ROOT/config/gateway.json"
"$DOTNET" build src/Gateway/Gateway.csproj -c Release --nologo -v q >/dev/null 2>&1 || true
"$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/account.sql" $CG >"$TMP/mig-account.log" 2>&1 || true
"$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/character.sql" $CC >"$TMP/mig-char.log" 2>&1 || true
"$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/currency.sql" $CC >"$TMP/mig-currency.log" 2>&1 || true
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
ACCT="r1_$$"; PW="R1-$$-$(date +%s)"
printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" $CG >/dev/null 2>&1
TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
  -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
[ -n "$TOK" ] && ok "登录" || bad "登录失败"
ID="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"r1$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin).get("character_id",""))' 2>/dev/null)"
[ -n "$ID" ] && ok "建角 id=$ID" || { bad "建角失败"; finish "$TITLE"; exit 1; }

TX="r1-$$-$(date +%s)"
PS="$("$DOTNET" "$CD" --realm-passive-state "$ID" $CC 2>&1 | grep -m1 '^realm_passive_state=')"
printf '%s' "$PS" | grep -q 'realm=炼体 realm_phase=0 phase_name=一阶 passive_pct_bps=0' \
  && ok "新建角色炼体一阶、被动 0" || bad "被动读口不对：$PS"

RAW="$("$DOTNET" "$CD" --raw-realm-phase-write "$ID" 8 $CC 2>&1 | grep -m1 '^raw_realm_phase=')"
printf '%s' "$RAW" | grep -q 'raw_realm_phase=written .* phase=8' \
  && ok "库 CHECK 已放宽：段内阶可写 8" || bad "段内阶 8 被库挡住：$RAW"

PMAX="$("$DOTNET" "$CD" --promote-realm-phase "$ID" "$TX-max" $CC 2>&1 | grep -m1 '^promote_realm_phase=')"
printf '%s' "$PMAX" | grep -q 'reason=realm_phase_max' \
  && ok "炼体第 9 阶已满：不能再升阶" || bad "满阶判定不对：$PMAX"

"$DOTNET" "$CD" --raw-realm-phase-write "$ID" 7 $CC >/dev/null
A7="$("$DOTNET" "$CD" --advance-realm "$ID" "$TX-a7" $CC 2>&1 | grep -m1 '^advance_realm=')"
printf '%s' "$A7" | grep -q 'reason=realm_phase_not_enough' \
  && ok "炼体第 8 阶不能突破筑基" || bad "第 8 阶门槛不对：$A7"

python3 - "$ROOT/config/realms.json" "$ROOT/config/character.json" "$TMP" <<'PY'
import json, sys
realms_path, char_path, tmp = sys.argv[1], sys.argv[2], sys.argv[3]
r = json.load(open(realms_path, encoding="utf-8"))
for x in r["advance"]:
    if x["from"] == "炼体":
        x["success_pct"] = 100
json.dump(r, open(tmp + "/r1-100.json", "w", encoding="utf-8"), ensure_ascii=False)
c = json.load(open(char_path, encoding="utf-8"))
c["realm_table"] = tmp + "/r1-100.json"
json.dump(c, open(tmp + "/c-r1-100.json", "w", encoding="utf-8"), ensure_ascii=False)
PY
C100="--config $TMP/c-r1-100.json"
"$DOTNET" "$CD" --raw-realm-phase-write "$ID" 8 $CC >/dev/null
"$DOTNET" "$CD" --raw-main-stage-write "$ID" 9 $CC >/dev/null
"$DOTNET" "$CD" --raw-drill-write "$ID" 100 $CC >/dev/null
# 灵石行要先由网关 /currency/add 建出来：--raw-currency-write 是 UPDATE，
# 行不存在时影响 0 行、余额仍是 0，突破会假红成 stone_not_enough。
curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/currency/add?token=$TOK" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":$ID,\"kind\":\"stone_1\",\"delta\":100}" >/dev/null
A8="$("$DOTNET" "$CD" --advance-realm "$ID" "$TX-a8" $C100 2>&1 | grep -m1 '^advance_realm=')"
printf '%s' "$A8" | grep -q 'executed=true .* success=true .*next=筑基 realm=筑基' \
  && ok "炼体第 9 阶放行：突破进筑基" || bad "第 9 阶仍被挡住：$A8"

PS2="$("$DOTNET" "$CD" --realm-passive-state "$ID" $CC 2>&1 | grep -m1 '^realm_passive_state=')"
printf '%s' "$PS2" | grep -q 'realm=筑基 realm_phase=0 phase_name=初期 passive_pct_bps=0' \
  && ok "突破后新境界从初期开始（realm_phase=0）" || bad "突破后段内阶没归零：$PS2"

finish "$TITLE"
