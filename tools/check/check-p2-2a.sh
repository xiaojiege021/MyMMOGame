#!/usr/bin/env bash
# P2-2a 高难次数与龙巢（方案 10.1 / 10.2；开发架构 11 节 P2-2、14 节第 56 行）。
#   bash tools/check/check-p2-2a.sh                编译+静态+只读+坏表（不连库不连服）
#   MMO_P2_2A_HAS_DB=1 bash tools/check/check-p2-2a.sh   加真服真库
# 本卡口径：龙巢至少 2 人、最多 8 人、每人每周 1 次；进一次扣 1 次，产出不按人数翻倍；
# 奖励写入前崩溃则不发奖，次数以角色服已经写下的进本记录为准；老四条副本一条不回退。
# 段 1–4 不连库；段 5 只在本机有 gamedb 时跑，缺凭据就明确跳过，不伪造。
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CC="--config $ROOT/config/character.json"
CG="--config $ROOT/config/gateway.json"
HAS_DB="${MMO_P2_2A_HAS_DB:-0}"
GATEWAY="${MMO_P2_2A_GATEWAY:-http://127.0.0.1:61000}"
BP="${MMO_P2_2A_BATTLE_PORT:-60001}"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_P2_2A_SKIP_BUILD:-0}" = "1" ]; then printf '  ..   跳过编译\n'; else
  BUILD_OK=1
  for proj in src/Character/Character.csproj src/Battle/Battle.csproj src/Gateway/Gateway.csproj; do
    n="$(basename "$proj" .csproj)"
    "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1 || {
      BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail; }
  done
  [ "$BUILD_OK" = "1" ] && ok "角色服/战斗服/网关编译通过" || true
fi

echo "== 2. 静态断言 =="
python3 - "$TMP/static.txt" <<'PY'
import json, os, re, sys
o = open(sys.argv[1], "w", encoding="utf-8")
def R(k, c, i=""): o.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")
def rd(p): return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

cfg = json.loads(rd("config/dungeons.json"))
ds = {d.get("id"): d for d in cfg.get("dungeons", [])}
R("version", cfg.get("version") == 6, "version=6")
dr = ds.get("dragon", {})
R("dragon", dr.get("tier") == "hard" and dr.get("min_players") == 2 and dr.get("max_players") == 8
  and dr.get("entries_per_week") == 1 and dr.get("reward_per_player") == 1
  and dr.get("charges_fatigue") is False and dr.get("takes_weekly_share") is False, str(dr))
R("caps", cfg.get("weekly_share_cap") == 12 and cfg.get("fatigue_daily_cap") == 6000
  and cfg.get("hot_spring_recovery") == 1500, "周份额/日上限/温泉一字未改")
old = {"main": 1, "abyss": 4, "lair": 4, "sect": 4}
newkeys = ("tier", "min_players", "entries_per_week", "reward_per_player")
R("old_four", all(ds.get(k, {}).get("max_players") == v for k, v in old.items())
  and not any(x in ds.get(k, {}) for k in old for x in newkeys), "老四条人数未改、不带新字段")

ct = rd("src/Character/DungeonTable.cs"); bt = rd("src/Battle/DungeonTable.cs")
R("two_tables", all(k in ct and k in bt for k in ("min_players", "entries_per_week", "reward_per_player", "tier")),
  "两端 DungeonTable 都读 tier/min_players/entries_per_week/reward_per_player")
R("not_hardcoded", not re.search(r'"dragon"', ct + bt)
  and "MinPlayers" in ct and "RewardPerPlayer" in ct and "MinPlayers" in bt and "RewardPerPlayer" in bt
  and not re.search(r'(?i)(min_players|entries_per_week|reward_per_player)\s*[:=]\s*[0-9]', ct + bt),
  "DungeonDef 由 Load 从配置填，代码里没有龙巢的第二套数字")

repo = rd("src/Character/HardEntryRepository.cs")
low = repo.lower()
R("repo_lock", "for update" in low, "HardEntryRepository 用 for update 锁角色行")
R("repo_week", "date_trunc('week'" in repo and "count(*)" in low, "按 date_trunc('week') + count(*) 计数")
i = repo.find("EnterHardAsync(")
j = repo.find("\n    public ", i + 1) if i >= 0 else -1
heb = repo[i:] if i >= 0 and j < 0 else (repo[i:j] if i >= 0 else "")
R("repo_reward", "RewardPerPlayer" in heb
  and not re.search(r'(?i)reward[_\w]*\s*=\s*partySize', heb), "reward_shares 传 dungeon.RewardPerPlayer，不按人数")
R("repo_txn", heb.count("BeginTransactionAsync") == 1 and heb.count("await transaction.CommitAsync") == 1,
  "EnterHardAsync 一笔事务、一次 Commit，无中间提交")
R("repo_only_entry", not [w for w in ("set fatigue", "set weekly_share", "set level", "currency", "inventory") if w in low],
  "只写进本记录：疲劳/周份额/等级/货币/背包一概不出现")

grpc = rd("src/Character/CharacterGrpcService.cs"); srv = rd("src/Battle/Server.cs")
R("double_side", "party_too_small" in grpc and "hard_entries_exhausted" in grpc and "party_too_small" in srv,
  "角色服两个 reason + 战斗服 party_too_small 都在")

pc = rd("proto/mmo/character.proto")
R("proto_fields", all(re.search(r'\b' + f + r'\s*=\s*' + str(n) + r'\s*;', pc) for f, n in
  (("min_players", 13), ("hard_entries_used", 14), ("hard_entries_cap", 15), ("hard_entries_remaining", 16))),
  "character.proto 回包字段 13–16 都在")
a = pc.find("message EnterDungeonRequest {"); b = pc.find("message EnterDungeonResponse {")
req = pc[a:b] if a >= 0 and b > a else ""
R("proto_req", req != "" and not re.search(r'entry|reward|entries', req, re.I),
  "EnterDungeonRequest 段没有 entry/reward/entries 这类次数字段")

gw = rd("src/Gateway/CharacterApi.cs")
R("gateway", all(k in gw for k in ("min_players", "hard_entries_used", "hard_entries_cap", "hard_entries_remaining"))
  and gw.count("Npgsql") == 0, "网关透传四个新字段，自己不碰库")
prog = rd("src/Character/Program.cs")
R("readonly_entry", all(k in prog for k in ('case "--dungeon-table"', 'case "--dungeon-table-path"',
  'case "--hard-entry-state"', 'case "--shift-entry-week"')), "四个只读入口都在")

newsrc = ct + bt + repo + grpc + srv + gw + prog
R("no_bareword", not any(w in newsrc for w in ["Play" + "erPrefs", "Resour" + "ces." + "Load"]),
  "新增源码/注释不含 U1 裸词")
o.close()
PY
while read -r tag key rest; do
  case "$tag" in PASS) ok "$key" ;; FAIL) bad "$key${rest:+ $rest}" ;; esac
done < "$TMP/static.txt"
grep -qE '^(PASS|FAIL) ' "$TMP/static.txt" || bad "静态断言没跑起来"
CF="client/MMOGame/Assets/Proto/Generated/Character.cs"
if [ -f "$CF" ]; then
  diff -q src/Shared/Generated/Character.cs "$CF" >/dev/null 2>&1 \
    && ok "两端 Character.cs 逐字相同" || bad "两端 Character.cs 不一致"
else
  printf '       （跳过两端生成物一致性：本机无 client/）\n'
fi

echo "== 3. 只读入口（与启动同一份 Load）=="
DT="$("$DOTNET" "$CD" --dungeon-table $CC 2>&1)"
printf '%s' "$DT" | grep -q '^dungeon_table_state=ok version=6' \
  && printf '%s' "$DT" | grep -qE '^dungeon=dragon tier=hard min=2 max=8 entries_per_week=1 reward_per_player=1 ' \
  && printf '%s' "$DT" | grep -qE '^dungeon=main tier=normal min=1 max=1 ' \
  && ok "副本表只读口径：version=6、龙巢 hard/2–8/每周1/每人1、主线 normal/1–1" \
  || bad "--dungeon-table 不对：$DT"

echo "== 4. 坏配置起不来（真实 DungeonTable.Load）=="
python3 - "$ROOT/config/dungeons.json" "$TMP" <<'PY'
import copy, json, os, sys
base = json.load(open(sys.argv[1], encoding="utf-8")); tmp = sys.argv[2]
def dragon(d): return next(x for x in d["dungeons"] if x["id"] == "dragon")
def put(name, d):
    json.dump(d, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
d = copy.deepcopy(base); dragon(d).pop("min_players", None); put("missing-min", d)
d = copy.deepcopy(base); dragon(d)["min_players"] = 9; put("min-over-max", d)
d = copy.deepcopy(base); next(x for x in d["dungeons"] if x["id"] == "lair")["entries_per_week"] = 1; put("lair-entries", d)
d = copy.deepcopy(base); dragon(d)["entries_per_week"] = 0; put("zero-entries", d)
PY
BAD_RC=0
for c in missing-min min-over-max lair-entries zero-entries; do
  out="$("$DOTNET" "$CD" --dungeon-table-path "$TMP/bad-$c.json" $CC 2>&1)"; rc=$?
  assert_bad_config "$out" "坏配置 ${c} 起不来且报 config_error="
  [ "$rc" -ne 0 ] || BAD_RC=1
done
[ "$BAD_RC" = "0" ] && ok "4 类坏表都以非 0 退出" || bad "有坏表以 0 退出"

echo "== 5. 真服 / 真库段 =="
if [ "$HAS_DB" != "1" ]; then
  printf '       （真库段未跑：MMO_P2_2A_HAS_DB 未置 1；本机段已跑完，不伪造）\n'
elif [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  printf '       （真库段未跑：缺 MMO_GAMEDB / /srv/mmo/gamedb.env；本机段已跑完）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/harness"
  for p in 62000 61000 "$BP"; do harness_track_port "$p"; done
  python3 - "$ROOT" "$TMP" "$BP" <<'PY'
import json, os, sys
root, tmp, port = sys.argv[1], sys.argv[2], sys.argv[3]
d = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
d["listen"] = "127.0.0.1:" + port
d["settlement_dir"] = os.path.join(tmp, "settlements")
d["character_dir"] = os.path.join(tmp, "characters")
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"), ("dungeon_table", "dungeons.json")):
    d[k] = os.path.join(root, "config", f)
json.dump(d, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  post() { curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' -d "$2" 2>&1; }
  enter() { post "$GATEWAY/dungeon/enter" "{\"character_id\":$1,\"dungeon_id\":\"$2\",\"party_size\":$3}"; }
  hstate() { "$DOTNET" "$CD" --hard-entry-state "$1" "$2" $CC 2>&1 | grep -m1 '^hard_entry_state='; }
  ecount() { "$DOTNET" "$CD" --entry-count "$1" $CC 2>&1 | grep -oE 'entry_count=[0-9]+' | head -1 | cut -d= -f2; }
  shares() { "$DOTNET" "$CD" --shares-total "$1" dragon $CC 2>&1 | grep -oE 'shares_total=[0-9]+' | head -1 | cut -d= -f2; }
  bag() { "$DOTNET" "$CD" --shop-state "$1" $CC 2>&1 | grep -m1 '^shop_state='; }
  mkchar() { local nm="$1" r id; for _ in $(seq 1 20); do
    r="$(curl -sS --noproxy '*' -m 10 -X POST "$GATEWAY/character?token=$TOK" -H 'Content-Type: application/json' \
      -d "{\"name\":\"$nm\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>/dev/null)"
    id="$(printf '%s' "$r" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")')"
    [ -n "$id" ] && { printf '%s' "$id"; return 0; }; sleep 0.5; done; return 1; }

  MIG=1
  "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/account.sql" $CG >"$TMP/m1" 2>&1 || MIG=0
  : > "$TMP/m2"
  for sql in character.sql dungeon_entry.sql inventory.sql currency.sql transaction.sql exp_settlement.sql; do
    "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/$sql" $CC >>"$TMP/m2" 2>&1 || true; done
  [ "$MIG" = "1" ] && grep -q '^migrate=ok' "$TMP/m1" && grep -q '^migrate=ok' "$TMP/m2" \
    && ok "真 gamedb 主迁移（账号/角色/进本/背包/货币/流水/经验）" || bad "建表失败：$(head -3 "$TMP/m1" "$TMP/m2")"
  ACCOUNT="p22a_$$"; PASSWORD="P22a-$$-$(date +%s)"
  printf '%s\n' "$PASSWORD" | "$DOTNET" "$GD" --seed-account "$ACCOUNT" $CG >/dev/null 2>&1
  ok "自造临时账号（口令/令牌/角色号只在内存，不落盘不打印）"
  for p in 62000 61000 "$BP"; do harness_kill_port "$p"; done
  harness_start "$TMP/harness/battle.log" "$DOTNET" "$BD" --config "$TMP/battle.json" >/dev/null
  harness_start "$TMP/harness/character.log" "$DOTNET" "$CD" $CC >/dev/null
  harness_start "$TMP/harness/gateway.log" "$DOTNET" "$GD" $CG >/dev/null
  harness_wait_ready "$TMP/harness/character.log" "角色服" || bad "角色服没起来"
  harness_wait_ready "$TMP/harness/gateway.log" "网关" || bad "网关没起来"
  BR=0; for _ in $(seq 1 120); do
    grep -q "^listen=http://127.0.0.1:$BP" "$TMP/harness/battle.log" 2>/dev/null && { BR=1; break; }
    sleep 0.1; done
  [ "$BR" = "1" ] && ok "真角色服 62000 + 网关 61000 + 战斗服 $BP 就绪" || bad "战斗服没起来"
  BPID="$(harness_listen_pids "$BP" | head -1)"
  TOK="$(harness_login 61000 "$ACCOUNT" "$PASSWORD")"
  [ -n "$TOK" ] && ok "登录拿到令牌" || bad "登录没拿到令牌"
  TS="$(date +%s | tail -c 6)"
  N1="$(mkchar "p22a_n1$TS")"; N2="$(mkchar "p22a_n2$TS")"; N3="$(mkchar "p22a_n3$TS")"
  N4="$(mkchar "p22a_n4$TS")"; N5="$(mkchar "p22a_n5$TS")"; N6="$(mkchar "p22a_n6$TS")"
  N7="$(mkchar "p22a_n7$TS")"; N8="$(mkchar "p22a_n8$TS")"
  SOLO="$(mkchar "p22a_solo$TS")"; P2="$(mkchar "p22a_p2$TS")"
  CRASH="$(mkchar "p22a_crash$TS")"; WEEK="$(mkchar "p22a_week$TS")"
  [ -n "$N1" ] && [ -n "$N8" ] && [ -n "$SOLO" ] && [ -n "$P2" ] && [ -n "$CRASH" ] && [ -n "$WEEK" ] \
    && ok "造 12 个角色（8 人满编 + 单人 + 2 人 + 崩溃 + 跨周）" || bad "角色没建齐"

  ok8() { [ "$(code_of "$1")" = "200" ] && [ "$(expect_json "$(body_of "$1")" allowed=true reward_shares=1 hard_entries_used=1 hard_entries_cap=1 hard_entries_remaining=0)" = "ok" ]; }

  echo "-- ① 单人被拒，且不留记录 --"
  E0="$(ecount "$SOLO")"
  r="$(enter "$SOLO" dragon 1)"
  if [ "$(code_of "$r")" = "200" ] && [ "$(expect_json "$(body_of "$r")" allowed=false reason=party_too_small reward_shares=0 entry_id=0)" = "ok" ]; then
    ok "单人进龙巢：HTTP 200 allowed=false reason=party_too_small，0 份、无记录"
  else bad "单人没被拒：$r"; fi
  [ "$(ecount "$SOLO")" = "$E0" ] && ok "单人被拒前后 entry_count 不变（$E0）" || bad "单人被拒也留了记录"

  echo "-- ② 8 人各进一次，每人 1 份 --"
  TEAM_OK=1; SUMS=0
  for id in "$N1" "$N2" "$N3" "$N4" "$N5" "$N6" "$N7" "$N8"; do
    r="$(enter "$id" dragon 8)"
    if ! ok8 "$r"; then TEAM_OK=0; bad "8 人里的 $id 没成功：$r"; break; fi
    s="$(shares "$id")"; SUMS=$((SUMS + ${s:-0}))
  done
  [ "$TEAM_OK" = "1" ] && [ "$SUMS" = "8" ] && [ "$(shares "$N1")" = "1" ] \
    && ok "8 人各 1 次：8×reward_shares=1，累计 8 份（不是 64），每人只 1 份" || bad "8 人账不对：累计 $SUMS，N1=$(shares "$N1")"

  echo "-- ③ 这周不能再进 --"
  E1="$(ecount "$N1")"
  r="$(enter "$N1" dragon 8)"
  if [ "$(code_of "$r")" = "200" ] && [ "$(expect_json "$(body_of "$r")" allowed=false reason=hard_entries_exhausted hard_entries_remaining=0)" = "ok" ]; then
    ok "同一角色本周再进：hard_entries_exhausted、剩余 0"
  else bad "本周还能再进：$r"; fi
  [ "$(ecount "$N1")" = "$E1" ] && ok "被拒不写第二次进本记录（$E1）" || bad "被拒也加了记录"

  echo "-- ④ 2 人也只扣 1 次 --"
  r="$(enter "$P2" dragon 2)"
  if ok8 "$r"; then ok "2 人进一次：hard_entries_used=1（不是按人数扣 2）" || true
  else bad "2 人进龙巢不对：$r"; fi

  echo "-- ⑤ 老副本不回退 + 战斗服仍拒单人 --"
  r="$(enter "$SOLO" main 1)"
  [ "$(code_of "$r")" = "200" ] && [ "$(expect_json "$(body_of "$r")" allowed=true)" = "ok" ] \
    && ok "主线单人仍 allowed=true" || bad "主线回退了：$r"
  r="$(enter "$SOLO" abyss 1)"
  [ "$(code_of "$r")" = "200" ] && [ "$(expect_json "$(body_of "$r")" allowed=true)" = "ok" ] \
    && ok "深渊 1 人仍可进（疲劳另扣）" || bad "深渊回退了：$r"
  r="$(enter "$N2" lair 4)"
  if [ "$(code_of "$r")" = "200" ] && [ "$(expect_json "$(body_of "$r")" allowed=true reward_shares=4 weekly_share_cost=4)" = "ok" ]; then
    ok "巢穴 4 人仍 reward_shares=4、weekly_share_cost=4"
  else bad "巢穴回退了：$r"; fi
  # 战斗服只在 WebSocket 请求上做人数判定，不带升级头会先在 IsWebSocketRequest 那关回 400，
  # 那样验不到 join_reject。这里带上正常的升级头，判定才会走到人数下限。
  br="$(curl -sS --noproxy '*' -m 5 -i -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
    -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' \
    "http://127.0.0.1:$BP/battle?dungeon=dragon&party_size=1" 2>&1)"
  printf '%s' "$br" | head -1 | grep -qE '^HTTP/[0-9.]+ 400' \
    && grep -q 'join_reject=party_too_small' "$TMP/harness/battle.log" \
    && ok "战斗服 ?dungeon=dragon&party_size=1 仍 400 + join_reject=party_too_small" \
    || bad "战斗服没拒单人：$(printf '%s' "$br" | head -1)"

  echo "-- ⑥ 四条拒绝路径：前后显式读库 --"
  guard() { printf '%s|%s' "$(ecount "$SOLO")" "$(bag "$SOLO")"; }
  G0="$(guard)"
  r="$(enter "$SOLO" no_such_dungeon 1)"
  [ "$(code_of "$r")" = "400" ] && [ "$(json_field "$(body_of "$r")" error)" = "unknown_dungeon" ] \
    && ok "未知副本：400 unknown_dungeon" || bad "未知副本码不对：$r"
  [ "$(guard)" = "$G0" ] && ok "未知副本：前后读库不变" || bad "未知副本写了库"
  r="$(curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$GATEWAY/dungeon/enter" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$SOLO,\"dungeon_id\":\"dragon\",\"party_size\":2}" 2>&1)"
  [ "$(code_of "$r")" = "401" ] && ok "无令牌：HTTP 401" || bad "无令牌码不对：$r"
  [ "$(guard)" = "$G0" ] && ok "无令牌：前后读库不变" || bad "无令牌写了库"
  r="$(post "$GATEWAY/dungeon/enter" "{\"character_id\":$SOLO,\"dungeon_id\":\"dragon\"}")"
  [ "$(code_of "$r")" = "400" ] && ok "缺 party_size：HTTP 400" || bad "缺字段码不对：$r"
  [ "$(guard)" = "$G0" ] && ok "缺字段：前后读库不变" || bad "缺字段写了库"
  r="$(enter 999999999 dragon 2)"
  [ "$(code_of "$r")" = "404" ] && ok "别人的角色：HTTP 404" || bad "别人的角色码不对：$r"
  [ "$(guard)" = "$G0" ] && ok "别人的角色：前后读库不变" || bad "别人的角色写了库"

  echo "-- ⑦ 奖励写入前崩溃：次数已扣、背包不动、本周不能再进 --"
  E0="$(ecount "$CRASH")"; B0="$(bag "$CRASH")"
  r="$(enter "$CRASH" dragon 2)"
  if ok8 "$r"; then ok "崩溃前已进本：次数在角色服落下（reward_shares=1）" || true
  else bad "崩溃前没进成：$r"; fi
  E1="$(ecount "$CRASH")"; HS="$(hstate "$CRASH" dragon)"
  [ $((E1 - E0)) -eq 1 ] && ok "进本记录 +1（$E0→$E1），次数以角色服记录为准" || bad "进本记录没加：$E0→$E1"
  printf '%s' "$HS" | grep -qE 'used=1 .*remaining=0' && ok "崩溃前 hard_entry_state: $HS" || bad "次数没扣：$HS"
  [ -n "$BPID" ] && kill -9 "$BPID" 2>/dev/null; sleep 0.5
  kill -0 "$BPID" 2>/dev/null && bad "战斗服没被 kill -9 掉" || ok "战斗服已 kill -9（这次不结算奖励）"
  [ "$(bag "$CRASH")" = "$B0" ] && ok "背包/货币一字未动：$B0" || bad "崩溃后背包变了：$B0 → $(bag "$CRASH")"
  E2="$(ecount "$CRASH")"; r="$(enter "$CRASH" dragon 2)"
  if [ "$(code_of "$r")" = "200" ] && [ "$(expect_json "$(body_of "$r")" allowed=false reason=hard_entries_exhausted hard_entries_remaining=0)" = "ok" ]; then
    ok "崩溃后本周不能再进：hard_entries_exhausted、剩余 0（次数不退回）"
  else bad "崩溃后次数退回了：$r"; fi
  [ "$(ecount "$CRASH")" = "$E2" ] && ok "崩溃后重进被拒：entry_count 不变（$E2）" || bad "被拒又加记录"

  echo "-- ⑧ 跨周归零 --"
  r="$(enter "$WEEK" dragon 2)"; HS="$(hstate "$WEEK" dragon)"
  ok8 "$r" && printf '%s' "$HS" | grep -qE 'used=1 .*remaining=0' && ok "本周用掉唯一一次：$HS" || bad "本周第一次不对：$r / $HS"
  "$DOTNET" "$CD" --shift-entry-week "$WEEK" dragon $CC >/dev/null 2>&1
  HS="$(hstate "$WEEK" dragon)"
  printf '%s' "$HS" | grep -qE 'used=0 .*remaining=1' && ok "把 entered_at 推前 7 天后：used=0 remaining=1" || bad "跨周没归零：$HS"
  r="$(enter "$WEEK" dragon 2)"; HS="$(hstate "$WEEK" dragon)"
  ok8 "$r" && ok "跨周后能再进一次：$HS" || bad "跨周后进不去：$r / $HS"
  harness_cleanup
fi
finish "P2-2a（server）"
