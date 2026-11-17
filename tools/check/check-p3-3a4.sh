#!/usr/bin/env bash
# P3-3a4 · 领地回写（方案 11 / 9.5 / 12 章、技术架构 3 / 6 节、
# 开发架构 11 节 P3-3、13 节 P3-3a4、14 节第 66 行）。
#   bash tools/check/check-p3-3a4.sh
#   MMO_P3_3A4_HAS_DB=1 MMO_GAMEDB='Host=...' bash tools/check/check-p3-3a4.sh
# 完成标准：城战胜负写回城池税收和矿脉归属；结束后城池归属改变；个人采集仍采不到灵石。
set -uo pipefail
# 本机代理会把 127.0.0.1 的明文 HTTP/2 请求送进代理，gRPC 段必须先 unset。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
SD="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
WD="$ROOT/src/World/bin/Release/net10.0/World.dll"
PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
CC="--config $ROOT/config/character.json"
CS="--config $ROOT/config/social.json"
CG="--config $ROOT/config/gateway.json"
CW="--config $ROOT/config/world.json"
HAS_DB="${MMO_P3_3A4_HAS_DB:-0}"
TMP="$(mktemp -d)"; PIDS=""
utf8() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      python3 -c 'import sys; sys.stdout.buffer.write(
          sys.stdin.buffer.read().decode("gbk", "replace").encode("utf-8"))' ;;
    *) cat ;;
  esac
}
cleanup() { for p in $PIDS; do stop_pid "$p"; done; rm -rf "$TMP"; }
trap cleanup EXIT
jget() { python3 -c 'import json,sys
o=json.load(sys.stdin); v=o.get(sys.argv[1])
print("" if v is None else ("true" if v is True else "false" if v is False else v))' "$1"; }
listen_of() {
  for _ in $(seq 1 120); do
    local u; u="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$1" 2>/dev/null | tail -1 | sed 's/^listen=//')"
    [ -n "$u" ] && { printf '%s' "$u"; return 0; }
    sleep 0.1
  done
  return 1
}

echo "== 1. 编译 =="
for proj in src/Battle/Battle.csproj src/Social/Social.csproj src/Character/Character.csproj \
            src/Gateway/Gateway.csproj src/World/World.csproj src/Global/Global.csproj \
            tools/probe/BattleProbe/BattleProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 ${n}：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done

echo "== 2. 静态断言（结构保证，不写死配置里的数字）=="
python3 - "$ROOT" >"$TMP/static.txt" 2>&1 <<'PY'
import hashlib, json, os, re, sys
root = sys.argv[1]
def rd(p):
    q = os.path.join(root, p)
    return open(q, encoding="utf-8").read() if os.path.exists(q) else ""
def cfg(p): return json.loads(rd(p))
def emit(k, c, i=""): print(("PASS " if c else "FAIL ") + k + ("" if c else "  " + str(i)[:150]))
t = cfg("config/siege_territory.json")
city = cfg("config/city.json")
sg = cfg("config/siege.json")
bc = cfg("config/battle.json")
cities = t.get("cities", [])
rule = cities[0] if cities else {}
emit("territory_schema", t.get("schema") == "siege-territory/1" and t.get("version", 0) > 0)
emit("territory_pending_note", t.get("pending") is True and "待定" in t.get("note", ""))
emit("territory_numbers_in_config", isinstance(rule.get("battle_timeout_ms"), int)
     and rule["battle_timeout_ms"] > 0 and isinstance(rule.get("tax_pct_on_capture"), int))
emit("territory_no_owner_or_shop", not any(k in t for k in
     ("owner_sect", "winner", "reward", "shop", "price", "balance", "ratio")))
emit("territory_rule_keys", set(rule) <= {"city_id", "match_prefix", "battle_timeout_ms", "tax_pct_on_capture"})
emit("city_ids_cross_checked", rule.get("city_id") in [c["city_id"] for c in city["cities"]])
# K7：归属 / 税率只在库，city.json 连键都不许有。
emit("city_json_untouched", "owner_sect" not in city["cities"][0]
     and "tax_pct" not in city["cities"][0]
     and city["cities"][0].get("vein_id") == "spirit_qiyun_1")
emit("siege_scale_unchanged", sg["mode"]["team_size"] == 40 and sg["mode"]["teams"] == 2
     and sg["mode"]["team_size"] * sg["mode"]["teams"] == 80)
emit("battle_json_no_new_table", "siege_territory_table" not in bc)
# proto 与两端生成物
proto = rd("proto/mmo/character.proto")
emit("proto_has_rpc", "rpc ApplySiegeResult(" in proto and "uint64 winner_sect = 6;" in proto)
emit("proto_no_tax_in_request", "tax_pct" not in proto.split("message ApplySiegeResultRequest {")[1].split("}")[0])
s1 = rd("src/Shared/Generated/Character.cs"); s2 = rd("client/MMOGame/Assets/Proto/Generated/Character.cs")
emit("two_ends_character_same", bool(s1) and
     hashlib.sha256(s1.encode()).hexdigest() == hashlib.sha256(s2.encode()).hexdigest())
# 战斗服：可选表降级、事实值判定、不改伤害公式、不连库
bc_code = rd("src/Battle/Config.cs"); srv = rd("src/Battle/Server.cs")
emit("table_optional", "SiegeTerritoryTable.Missing()" in bc_code
     and "static SiegeTerritoryTable Missing()" in rd("src/Battle/SiegeTerritoryTable.cs"))
emit("result_only_in_siege", "FinishSiegeAfterAsync" in srv
     and "StartsWith(\"siege-\"" in srv)
emit("damage_not_in_encounter", "SiegeDamage" not in rd("src/Battle/Encounter.cs")
     and "SiegeDamage" in srv)
emit("roster_survives_detach", "SiegeRoster" in srv and "Detach" in srv)
emit("no_winner_no_write", "no_winner" in srv and "no_sect" in srv)
emit("battle_no_db", "Npgsql" not in "".join(rd(os.path.join("src/Battle", f))
     for f in os.listdir(os.path.join(root, "src/Battle")) if f.endswith(".cs")))
emit("sect_is_read_only", "DescribeCharacterSect" in rd("src/Battle/SiegeResultClient.cs")
     and "NoSiegeResultSink" in rd("src/Battle/SiegeResultClient.cs"))
# 角色服：一笔事务、抢事务号在读回之前、税率只从配置读、不碰账本
repo = rd("src/Character/SiegeTerritoryRepository.cs")
emit("txn_before_readback", repo.index("on conflict (txn_id) do nothing") < repo.index("select owner_sect"))
emit("no_ledger_write", "insert into \"社交\".city_tax_ledger" not in repo
     and "update \"社交\".city_tax_ledger" not in repo)
emit("tax_from_config_only", "policy.TaxPctOf(city)" in repo and "request.TaxPct" not in repo)
emit("reject_paths", all(x in repo for x in
     ("request_missing", "unknown_city", "winner_sect_missing", "duplicate")))
ddl = rd("db/character/siege_result.sql"); main = rd("db/character/character.sql")
emit("ddl_in_both_files", "CREATE TABLE IF NOT EXISTS \"社交\".siege_result" in ddl
     and "CREATE TABLE IF NOT EXISTS \"社交\".siege_result" in main)
emit("ddl_no_gold_or_reward", not re.search(r"gold|reward|item|price", ddl.lower()))
emit("gather_unchanged", json.dumps(cfg("config/gather.json"), ensure_ascii=False).count("spirit_qiyun_1") >= 1)
PY
while IFS= read -r line; do
  case "$line" in "PASS "*) ok "${line#PASS }" ;; "FAIL "*) bad "${line#FAIL }" ;; esac
done < "$TMP/static.txt"

echo "== 3. 只读入口、坏配置与纯计算 =="
SO="$("$DOTNET" "$BD" --siege-territory-table 2>&1)"; SRC=$?
TO="$(python3 -c 'import json;print(json.load(open("config/siege_territory.json",encoding="utf-8"))["cities"][0]["battle_timeout_ms"])')"
TAX="$(python3 -c 'import json;print(json.load(open("config/siege_territory.json",encoding="utf-8"))["cities"][0]["tax_pct_on_capture"])')"
[ "$SRC" = 0 ] && printf '%s' "$SO" | grep -q "pending=true cities=1" \
  && printf '%s' "$SO" | grep -q "timeout_ms=$TO tax_pct=$TAX" \
  && ok "战斗服领地表只读入口从配置现取（$TO / $TAX / pending=true）" \
  || bad "战斗服只读入口不对（rc=${SRC}）：$(printf '%s' "$SO" | utf8 | head -2)"
printf '%s' "$SO" | grep -q 'siege_territory_match=prefix=qiyun city=qiyun unknown=-' \
  && ok "纯计算：哪场打哪座城只按服务端前缀，匹配不上不写回" \
  || bad "城池前缀匹配不对"
CO="$("$DOTNET" "$CD" $CC --siege-territory-table 2>&1 | utf8)"; CRC=$?
[ "$CRC" = 0 ] && printf '%s' "$CO" | grep -q "siege_territory_table_state=ok .*pending=true" \
  && printf '%s' "$CO" | grep -q "tax_pct=$TAX" \
  && ok "角色服读同一份领地表，抽成比例只从配置来" \
  || bad "角色服只读入口不对（rc=${CRC}）：$(printf '%s' "$CO" | head -2)"
mk_bad() {
  python3 - "$ROOT/config/siege_territory.json" "$TMP/bad-$1.json" "$1" <<'PY'
import json, sys
src, dst, mode = sys.argv[1:4]
d = json.load(open(src, encoding="utf-8"))
r = d["cities"][0]
if mode == "schema": d["schema"] = "siege-territory/2"
elif mode == "version": d["version"] = 0
elif mode == "timeout0": r["battle_timeout_ms"] = 0
elif mode == "timeout_big": r["battle_timeout_ms"] = 60001
elif mode == "tax101": r["tax_pct_on_capture"] = 101
elif mode == "no_cities": d["cities"] = []
elif mode == "bad_city": r["city_id"] = "nowhere"
elif mode == "balance": d["balance"] = {"ratio": 1.3}
elif mode == "owner": d["owner_sect"] = "sect-1"
elif mode == "item": r["item_id"] = "stuff"
json.dump(d, open(dst, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
}
for c in schema version timeout0 timeout_big tax101 no_cities bad_city balance owner item; do
  mk_bad "$c"
  out="$("$DOTNET" "$BD" --siege-territory-table-path "$TMP/bad-$c.json" 2>&1)"; rc=$?
  [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error=' \
    && ok "坏配置 ${c}：config_error= 且非 0 退出" \
    || bad "坏配置 ${c} 没挡住（rc=${rc}）：$(printf '%s' "$out" | utf8 | head -2)"
done

echo "== 4. 假服：缺表降级与老口径不回退 =="
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1:3]
d = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
d.update(listen="127.0.0.1:0", settlement_dir=os.path.join(tmp, "fake-set"),
         character_dir=os.path.join(tmp, "fake-chr"))
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json")):
    d[k] = os.path.join(root, "config", f)
d["siege_table"] = os.path.join(root, "config", "siege.json")
for k in ("siege_territory_table", "sect_tech_table", "social_upstream", "exp_upstream"):
    d.pop(k, None)
json.dump(d, open(os.path.join(tmp, "fake.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
"$DOTNET" "$BD" --config "$TMP/fake.json" >"$TMP/fake.log" 2>&1 & PIDS="$PIDS $!"
U="$(listen_of "$TMP/fake.log")"
[ -n "$U" ] && ok "假服就绪（临时目录没有领地表）" || bad "假服没起来"
grep -q 'siege_territory=missing' "$TMP/fake.log" \
  && ok "领地表可选降级：siege_territory=missing，不排结束定时器也不写回" \
  || bad "缺表没降级：$(grep -m1 'siege_territory_table=' "$TMP/fake.log" | detail)"
grep -q 'siege=missing\|siege_version=' "$TMP/fake.log" && grep -q '^resume=off' "$TMP/fake.log" \
  && grep -q '^config=' "$TMP/fake.log" \
  && ok "城战表、config=、resume=off 老启动日志仍在" || bad "老启动日志被改了"

echo "== 5. 真服真库（本节全部需要真服；跳过时只算静态证据）=="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真服真库段（MMO_P3_3A4_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1:3]
sg = json.load(open(os.path.join(root, "config", "siege.json"), encoding="utf-8"))
sg["fill_timeout_ms"] = 300
json.dump(sg, open(os.path.join(tmp, "siege-fast.json"), "w", encoding="utf-8"), ensure_ascii=False)
t = json.load(open(os.path.join(root, "config", "siege_territory.json"), encoding="utf-8"))
t["cities"][0]["battle_timeout_ms"] = 3000
t["cities"][0]["tax_pct_on_capture"] = 7
t["city_table"] = os.path.join(root, "config", "city.json")
json.dump(t, open(os.path.join(tmp, "territory-fast.json"), "w", encoding="utf-8"), ensure_ascii=False)
d = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
d.update(listen="127.0.0.1:0", settlement_dir=os.path.join(tmp, "real-set"),
         character_dir=os.path.join(tmp, "real-chr"),
         exp_upstream="http://127.0.0.1:62000", character_upstream="http://127.0.0.1:62000",
         social_upstream="http://127.0.0.1:65000")
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json")):
    d[k] = os.path.join(root, "config", f)
d["siege_table"] = os.path.join(tmp, "siege-fast.json")
d["siege_territory_table"] = os.path.join(tmp, "territory-fast.json")
json.dump(d, open(os.path.join(tmp, "battle-real.json"), "w", encoding="utf-8"), ensure_ascii=False)
c = json.load(open(os.path.join(root, "config", "character.json"), encoding="utf-8"))
c["siege_territory_table"] = os.path.join(tmp, "territory-fast.json")
json.dump(c, open(os.path.join(tmp, "character-real.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
  "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/character.sql" $CG >"$TMP/mig-character.log" 2>&1 || true
  "$DOTNET" "$SD" --migrate --sql "$ROOT/db/social/social.sql" $CS >"$TMP/mig-social.log" 2>&1 || true
  grep -q '^migrate=ok' "$TMP/mig-character.log" \
    && ok "真库：主迁移自包含，city 与 siege_result 都在" \
    || bad "主迁移失败：$(head -3 "$TMP/mig-character.log" | detail)"
  CR="--config $TMP/character-real.json"
  "$DOTNET" "$CD" $CR >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"; wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$SD" $CS >"$TMP/social.log" 2>&1 & PIDS="$PIDS $!"; wait_ready "$TMP/social.log" '^ready' "社交服" || true
  "$DOTNET" "$BD" --config "$TMP/battle-real.json" >"$TMP/battle.log" 2>&1 & PIDS="$PIDS $!"
  U="$(listen_of "$TMP/battle.log")"; WU="ws${U#http}"; BPORT="${U##*:}"
  python3 - "$ROOT" "$TMP" "$BPORT" <<'PY'
import json, os, sys
root, tmp, port = sys.argv[1:4]
d = json.load(open(os.path.join(root, "config", "gateway.json"), encoding="utf-8"))
d["battle_upstream"] = "ws://127.0.0.1:" + port + "/battle"
json.dump(d, open(os.path.join(tmp, "gateway-real.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
  GR="--config $TMP/gateway-real.json"
  "$DOTNET" "$GD" $GR >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  "$DOTNET" "$WD" $CW --gather-config "$ROOT/config/gather.json" >"$TMP/world.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/world.log" '^ready' "世界服" || true
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
      -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p334_$$"; PW="P334-$$-$(date +%s)"; TX="p334-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 $GR >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" | jget token 2>/dev/null)"
  [ -n "$TOK" ] && ok "真服：登录拿到令牌（账号/口令只在内存）" || bad "真服：登录没拿到令牌"
  post() { curl -s --noproxy '*' -X POST "http://127.0.0.1:61000$1?token=$TOK" \
    -H 'Content-Type: application/json' -d "$2"; }
  mkchar() {
    local cls="$1" o id
    for _ in $(seq 1 10); do
      o="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
        -H 'Content-Type: application/json' -d "{\"name\":\"p334_$cls$RANDOM\",\"race\":\"mai\",\"class_id\":\"$cls\"}" 2>&1)"
      id="$(printf '%s' "$o" | jget character_id 2>/dev/null)"
      [ -n "$id" ] && { printf '%s' "$id"; return 0; }; sleep 0.5
    done
  }
  A="$(mkchar warrior)"; B="$(mkchar priest)"
  [ -n "$A" ] && [ -n "$B" ] && ok "真服：造两个角色（$A / ${B}）" || bad "真服：角色没建出来"
  SECT="$(post /sect/create "{\"character_id\":$A,\"name\":\"p334_sect_$$\"}" | jget sect_id 2>/dev/null)"
  [ -n "$SECT" ] && ok "真库：A 建了宗门（sect=${SECT}）" || bad "真库：宗门没建出来"
  BEFORE="$("$DOTNET" "$CD" $CR --siege-result-state 2>&1 | utf8 | grep -m1 '^siege_result_city=')"
  ok "真库：写回前城池状态 $BEFORE"
  BOSS="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["boss"]["id"])' "$ROOT/config/battle.json")"
  M="qiyun-$$-$(date +%s)"
  "$DOTNET" "$PROBE" --url "$WU/battle?siege=$M&team=b&char=$B&class=warrior" \
    --case battlefield_open --hold-ms 5000 >"$TMP/sb.log" 2>&1 & PB=$!
  sleep 0.2
  "$DOTNET" "$PROBE" --url "$WU/battle?siege=$M&team=a&char=$A&class=warrior" \
    --case damage --boss "$BOSS" --ranged 1002 >"$TMP/sa.log" 2>&1
  wait "$PB" 2>/dev/null || true
  grep -q 'verdict=ok' "$TMP/sa.log" && ok "真服：a 队技能由战斗服结算（城战里打中 Boss）" \
    || bad "a 队没打出伤害：$(head -3 "$TMP/sa.log" | detail)"
  python3 - "$TMP/battle.log" "$M" "$SECT" >"$TMP/parse.txt" 2>&1 <<'PY'
import re, sys
log, match, sect = sys.argv[1:4]
rows = [l.strip() for l in open(log, encoding="utf-8") if l.startswith("siege_result=match=" + match)]
def num(l, k):
    m = re.search(r" " + k + r"=([0-9]+)", l)
    return int(m.group(1)) if m else -1
print("PASS one_result_per_match" if len(rows) == 1 else "FAIL one_result_per_match rows=%d" % len(rows))
if rows:
    l = rows[0]
    print("PASS winner_by_damage" if num(l, "damage_a") > 0 and num(l, "damage_b") == 0
          and num(l, "winner_team") == 0 else "FAIL winner_by_damage " + l[:160])
    print("PASS winner_sect_is_social_fact" if f"winner_sect={sect} " in l else
          "FAIL winner_sect_is_social_fact " + l[:160])
    print("PASS writeback_ok" if "ok=true duplicate=false" in l
          and f"owner={sect} " in l and "tax_pct=7" in l else "FAIL writeback_ok " + l[:160])
    print("PASS txn_is_server_fact" if f"txn=siege:{match}:" in l else "FAIL txn_is_server_fact")
PY
  while IFS= read -r line; do
    case "$line" in "PASS "*) ok "真服：${line#PASS }" ;; "FAIL "*) bad "真服：${line#FAIL }" ;; esac
  done < "$TMP/parse.txt"
  STATE="$("$DOTNET" "$CD" $CR --siege-result-state 2>&1 | utf8)"
  printf '%s' "$STATE" | grep -q "siege_result_city=city=qiyun owner=$SECT tax_pct=7" \
    && ok "真库：从库里独立读回归属与抽成比例（owner=$SECT tax_pct=7）" \
    || bad "库里读回不对：$(printf '%s' "$STATE" | grep 'siege_result_city=' | head -1)"
  printf '%s' "$STATE" | grep -q "winner_sect=$SECT" \
    && ok "真库：流水里记的是社交服给回的胜方宗门，不是客户端带的" \
    || bad "流水没记胜方宗门：$(printf '%s' "$STATE" | grep 'siege_result_last=' | head -1)"
  TXN="$(grep -oE "txn=siege:$M:[0-9]+" "$TMP/battle.log" | head -1 | cut -d= -f2)"
  REPLAY="$("$DOTNET" "$CD" $CR --apply-siege-result "$TXN" qiyun "$M" 0 "$SECT" 2>&1 | utf8 | grep -m1 '^siege_result_apply=')"
  printf '%s' "$REPLAY" | grep -q 'duplicate=true' && ok "真库：同一事务号重放回 duplicate，不改第二次" \
    || bad "重放没有幂等：$REPLAY"
  for spec in "0:winner_sect_missing:$TX-r0:qiyun:0" "0:unknown_city:$TX-r1:nowhere:$SECT" "0:request_missing::qiyun:$SECT"; do
    IFS=: read -r team want txn cid sect <<< "$spec"
    R="$("$DOTNET" "$CD" $CR --apply-siege-result "$txn" "$cid" "$M" "$team" "$sect" 2>&1 | utf8 | grep -m1 '^siege_result_apply=')"
    printf '%s' "$R" | grep -q "reason=$want" && printf '%s' "$R" | grep -q 'ok=false' \
      && ok "真库：拒绝路径 $want 一分不写" || bad "拒绝路径 $want 不对：$R"
  done
  R="$(post /world/gather "{\"character_id\":$A,\"node_id\":\"spirit_qiyun_1\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "false" ] \
    && [ "$(printf '%s' "$R" | jget reason)" = "spirit_stone_forbidden" ] \
    && [ "$(printf '%s' "$R" | jget count)" = "0" ] \
    && ok "写回之后个人采集灵石矿脉仍被拒：spirit_stone_forbidden、未生效数值 0" \
    || bad "写回后矿脉被打开了：$R"
  grep -qE 'damage=|skill_result|skill_settle|结算伤害' "$TMP/world.log" \
    && bad "真服：世界服日志出现技能结算行" \
    || ok "真服：世界服全程不结算这场技能（第 11 节 P3-3 不回退）"
  TECHLINES="$(grep -c 'sect_tech_level=' "$TMP/battle.log" || true)"
  NONCITY="$(grep 'sect_tech_level=' "$TMP/battle.log" | grep -cv '^siege_join=\|^siege_open=' || true)"
  [ "${NONCITY:-0}" = "0" ] && [ "${TECHLINES:-0}" -gt 0 ] \
    && ok "真服：科技字段只在城战进场/开场日志（$TECHLINES 行），老卡口径不回退" \
    || bad "科技字段漏进别的路径：非城战行 ${NONCITY:-0}"
fi

echo "== 6. 回归闸门 =="
if bash tools/check/preflight.sh >"$TMP/preflight.log" 2>&1; then
  ok "preflight.sh 第 4 道闸门绿"
else
  bad "preflight.sh 没过：$(tail -4 "$TMP/preflight.log" | detail)"
fi
if git diff --check >"$TMP/diffcheck.log" 2>&1; then
  ok "git diff --check 通过"
else
  bad "git diff --check 有空白错误：$(head -3 "$TMP/diffcheck.log" | detail)"
fi

finish "P3-3a4（领地回写：城池税收与矿脉归属）"
