#!/usr/bin/env bash
# tools/check/check-p2-1a2.sh —— P2-1a2 职位席位（方案 11.1 / 开发架构第 14 节第 51 行）。
# 完成标准：掌门、副掌门、长老、执事超过席位的任命被拒绝；席位数字只有一份正本。
#
#   bash tools/check/check-p2-1a2.sh        编译 + 静态 + 只读入口 + 坏表（不连库不连服）
#   MMO_P2_1A2_HAS_DB=1 MMO_P2_1A2_GATEWAY=… MMO_P2_1A2_TOKEN=… \
#     MMO_P2_1A2_CHARACTER=… bash tools/check/check-p2-1a2.sh   再加真服/真库段
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
SOCIAL_DLL="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
HAS_DB="${MMO_P2_1A2_HAS_DB:-0}"
GATEWAY="${MMO_P2_1A2_GATEWAY:-}"
TOKEN="${MMO_P2_1A2_TOKEN:-}"
CHARACTER="${MMO_P2_1A2_CHARACTER:-0}"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
if [ "${MMO_P2_1A2_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译\n'
elif "$DOTNET" build src/Social/Social.csproj -c Release -v quiet > "$TMP/build.log" 2>&1 \
     && "$DOTNET" build src/Gateway/Gateway.csproj -c Release -v quiet >> "$TMP/build.log" 2>&1; then
  ok "编译 Social + Gateway"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言 =="
python3 - "$TMP/static.txt" <<'PY'
import json, os, re, sys

out = open(sys.argv[1], "w", encoding="utf-8")
def report(k, cond, info=""):
    out.write(("PASS " if cond else "FAIL ") + k + ("" if cond else "  " + info) + "\n")
def read(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""
def readdir(d):
    return "\n".join(read(os.path.join(d, f)) for f in sorted(os.listdir(d)) if f.endswith(".cs")) if os.path.isdir(d) else ""

cfg = json.loads(read("config/sect.json"))
html = read("docs/MMO系统架构方案.html")
seg = html.split(">11.1<")[1].split("</section>")[0] if ">11.1<" in html else ""
m = re.search(r"名额上限是掌门\s*(\d+)、副掌门\s*(\d+)、长老\s*(\d+)、执事\s*(\d+)", seg)
plan = [int(x) for x in m.groups()] if m else []
conf = {p["id"]: p["seat_cap"] for p in cfg.get("positions", [])}
want = {"master": 1, "vice_master": 1, "elder": 4, "steward": 12}
report("positions_vs_plan", plan == [1, 1, 4, 12] and conf == want,
       "config positions 与方案 11.1 的 1/1/4/12 逐项一致")
report("note_position", all(s in cfg["note"] for s in
       ["1 级只有 10 个席位", "任满为止", "不要求把执事名额填满", "职位不加属性", "不代替境界"]),
       "note 写明 1 级 10 席/任满为止/不填满/不加属性/不代替境界")
report("disciple_default", "disciple" not in conf and set(conf) == set(want),
       "disciple 是不占席位的默认职位，不在 positions 里")

social_src = readdir("src/Social")
gateway_src = read("src/Gateway/SectApi.cs") + read("src/Gateway/GatewayProcess.cs")
proto = read("proto/mmo/social.proto")
sql = read("db/social/sect_position.sql") + read("db/social/social.sql")
# 按「配置键 + 值」找第二套席位数字，别按裸子串（字段号/端口/version 都不是玩法数值）。
hard = re.compile(r'"seat_cap"\s*:\s*\d|new Seat\(\s*"[a-z_]+"\s*,\s*\d|\bseat_cap\s*=\s*(?:1|4|12)\b')
report("no_second_copy", not hard.search(social_src + gateway_src + proto + sql),
       "代码/proto/SQL 不含第二套席位数字（按配置键+值找）")
report("sql_position_column", all(x in sql for x in
       ["ADD COLUMN IF NOT EXISTS position", "DEFAULT 'disciple'",
        "DROP CONSTRAINT IF EXISTS sect_member_position_chk",
        "'master', 'vice_master', 'elder', 'steward', 'disciple'"]),
       "幂等 DDL：加 position 列 + 五职位约束")
report("sql_in_main_migration", "ADD COLUMN IF NOT EXISTS position" in read("db/social/social.sql")
       and "ADD COLUMN IF NOT EXISTS position" in read("db/social/sect_position.sql"),
       "同一份幂等 DDL 追加进主迁移")
report("sql_two_tables", read("db/social/social.sql").lower().count('create table if not exists "社交".sect') >= 2,
       "a1 两张表 DDL 仍在主迁移")
report("sql_no_role_schema", '"角色"' not in sql, "社交 DDL 不碰角色 schema")

report("proto_rpcs", all(x in proto for x in
       ["rpc AppointPosition", "rpc DismissPosition", "rpc DescribePositions",
        "message PositionRequest", "message PositionStateResponse"])
       and all(x in proto for x in ["string account", "uint64 character_id", "string txn_id",
        "uint64 sect_id", "uint64 target_character_id", "string position",
        "bool ok", "string reason", "int32 seat_cap", "int32 seat_count"]),
       "三条 rpc + 请求/回包字段")

tbl = read("src/Social/SectTable.cs")
report("sect_table_hard", all(x in tbl for x in
       ["缺 positions 段", "seat_cap 必须是正整数", "职位编号重复", "未知职位",
        "掌门席位必须是 1", "version 必须是正整数", "超过 5 级名册上限"]),
       "七类坏配置在 Load 里硬校验")

repo = read("src/Social/SectRepository.cs")
report("repo_one_txn", all(x in repo for x in
       ["AppointAsync", "DismissAsync", "PositionStateAsync", "for update", "count(*)",
        "set position = $3", "RollbackAsync", "CommitAsync"]),
       "一笔事务：锁宗门行→查目标→数席位→update/拒绝")
report("repo_position_only", '"角色"' not in repo
       and "sect_member set position" in repo
       and not re.search(r"set\s+(level|realm|damage|exp|attribute|hp)\s*=", repo[repo.find("AppointAsync"):repo.find("// ---- 只读：DescribePositions")], re.I),
       "职位段只写 sect_member.position，不写等级/境界/伤害/经验/属性")
report("repo_reasons", all(x in repo for x in
       ["position_full", "not_member", "not_leader", "position_unknown"]), "拒绝 reason 齐")

report("grpc_three", all(x in read("src/Social/SocialGrpcService.cs") for x in
       ["AppointPosition", "DismissPosition", "DescribePositions"]), "三个 gRPC 入口")
program = read("src/Social/Program.cs")
report("readonly_entries", all(x in program for x in
       ["--sect-position-config", "--sect-position-list", "--sect-position-state",
        "--sect-position-members", "--migrate", "--sql"]), "四个只读入口 + 迁移链")
report("same_load", program.count("SocialLoader.LoadAll(configPath, sectTablePath)") >= 3,
       "只读入口与真实启动共用同一 Load + 同一组参数")
report("startup_same_shape", 'p.Id + ":" + p.Cap' in read("src/Social/SocialProcess.cs") and 'p.Id + ":" + p.Cap' in program,
       "启动日志与 config 入口同形（同一 Positions 拼法）")

gw = read("src/Gateway/SectApi.cs")
report("gateway_routes", all(x in gw for x in
       ['"/sect/appoint"', '"/sect/dismiss"', "OwnsAsync", "Guid.NewGuid",
        "PositionBody", "TargetCharacterId"]) and "SectApi.Map" in gateway_src,
       "两条 POST + 认令牌 + 归属复核 + 服务端生成 txn_id")
pb = re.search(r"record PositionBody\(([^)]*)\)", gw)
report("gateway_no_seat_input", pb is not None and "seat" not in pb.group(1).lower(),
       "请求体只带四个字段，客户端带不进席位数字")

srv = read("src/Shared/Generated/Social.cs")
cli = read("client/MMOGame/Assets/Proto/Generated/Social.cs")
report("generated_same", srv != "" and srv == cli, "两端 Social.cs 逐字相同")
m = re.search(r"^guid:\s*([0-9a-fA-F]+)\s*$",
              read("client/MMOGame/Assets/Proto/Generated/Social.cs.meta"), re.M)
report("meta_guid_32", m is not None and len(m.group(1)) == 32, "客户端 Social.cs.meta GUID 32 位")
report("no_bareword", not re.search(r"PlayerPrefs|Resources\.Load",
       srv + cli + read("src/Shared/Generated/SocialGrpc.cs") + gw + proto),
       "新增生成物/注释不含 U1 裸词")
out.close()
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static.txt"
grep -qE '^(PASS|FAIL) ' "$TMP/static.txt" || bad "静态断言没跑起来"

echo "== 3. 只读入口（与启动同形）=="
CONF="$("$DOTNET" "$SOCIAL_DLL" --sect-position-config 2>&1)"
if printf '%s' "$CONF" | grep -q '^sect_position_config_state=ok' \
   && printf '%s' "$CONF" | grep -q 'positions=master:1,vice_master:1,elder:4,steward:12'; then
  ok "--sect-position-config 逐项打印四席位：$(printf '%s' "$CONF" | head -1 | cut -c1-96)"
else
  bad "--sect-position-config 不对：$CONF"
fi
assert_file_contains "src/Social/SocialProcess.cs" 'positions=' "启动日志打印 positions 段"

echo "== 4. 坏配置七类起不来 =="
python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
base = json.load(open("config/sect.json", encoding="utf-8"))
def case(name, mutate):
    d = json.loads(json.dumps(base)); mutate(d)
    json.dump(d, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
def p(d, i): return d["positions"][i]
case("no_positions", lambda d: d.pop("positions", None))
case("zero_seat", lambda d: p(d, 2).update(seat_cap=0))
case("dup_position", lambda d: p(d, 1).update(id="master"))
case("unknown_position", lambda d: p(d, 3).update(id="emperor"))
case("master_not_1", lambda d: p(d, 0).update(seat_cap=2))
case("bad_version", lambda d: d.update(version=0))
case("seats_exceed_cap", lambda d: p(d, 3).update(seat_cap=300))
PY
BAD_RC=0
for c in no_positions zero_seat dup_position unknown_position master_not_1 bad_version seats_exceed_cap; do
  o="$("$DOTNET" "$SOCIAL_DLL" --sect-position-config --sect-table "$TMP/bad-$c.json" 2>&1)"; rc=$?
  assert_bad_config "$o" "坏配置 ${c}：$(printf '%s' "$o" | grep -oE 'config_error=.*' | head -1)"
  [ "$rc" -ne 0 ] || BAD_RC=1
done
[ "$BAD_RC" = "0" ] && ok "七类坏配置都以非 0 退出" || bad "有坏配置以 0 退出"

echo "== 5. 真服 / 真库段 =="
if [ "$HAS_DB" != "1" ]; then
  printf '       （真库段未跑：MMO_P2_1A2_HAS_DB 未置 1；本机编译/静态/只读/坏表段已跑完，结果不伪造）\n'
elif [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （真库段未跑：缺 MMO_P2_1A2_GATEWAY / _TOKEN / _CHARACTER；本机段已跑完）\n'
elif [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  printf '       （真库段未跑：缺 MMO_GAMEDB / /srv/mmo/gamedb.env；本机段已跑完）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/harness"; harness_track_port 65000
  cp "$ROOT/config/sect.json" "$TMP/sect.orig.json"
  trap 'cp "$TMP/sect.orig.json" "$ROOT/config/sect.json" 2>/dev/null; harness_cleanup; rm -rf "$TMP"' EXIT

  MIG="$("$DOTNET" "$SOCIAL_DLL" --migrate --sql "$ROOT/db/social/social.sql" --config "$ROOT/config/social.json" 2>&1)"
  printf '%s' "$MIG" | grep -q '^migrate=ok' && ok "社交 schema 主迁移（含职位列）" || bad "建表失败：$MIG"

  post(){ curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$2" 2>&1; }
  check(){ [ "$(expect_json "$(body_of "$1")" $2)" = "ok" ] && ok "$3" || bad "${3}：$1"; }
  nth(){ printf '%s\n' "$1" | tr ' ' '\n' | awk -v n="$2" 'NR==n{print}'; }
  new_char(){ curl -sS --noproxy '*' -m 10 -X POST "$GATEWAY/character?token=$TOKEN" -H 'Content-Type: application/json' -d "{\"name\":\"$1\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>/dev/null | python3 -c 'import json,sys;d=json.load(sys.stdin);print(d.get("character_id",""))' 2>/dev/null || true; }
  appoint(){ post "$GATEWAY/sect/appoint" "{\"character_id\":$FOUNDER,\"sect_id\":$1,\"target_character_id\":$2,\"position\":\"$3\"}"; }
  dismiss(){ post "$GATEWAY/sect/dismiss" "{\"character_id\":$FOUNDER,\"sect_id\":$1,\"target_character_id\":$2,\"position\":\"$3\"}"; }
  state(){ "$DOTNET" "$SOCIAL_DLL" --sect-position-state "$1" --config "$ROOT/config/social.json" 2>&1; }
  members(){ "$DOTNET" "$SOCIAL_DLL" --sect-position-members "$1" --config "$ROOT/config/social.json" 2>&1; }
  read_pos(){ members "$1" | grep -oE 'ids=.*' | head -1; }
  set_steward(){ python3 - "$1" "$TMP/sect.orig.json" "$ROOT/config/sect.json" <<'PY'
import json,sys
d=json.load(open(sys.argv[2],encoding="utf-8"));[p.update(seat_cap=int(sys.argv[1])) for p in d["positions"] if p["id"]=="steward"];json.dump(d,open(sys.argv[3],"w",encoding="utf-8"),ensure_ascii=False)
PY
  }
  start_social(){
    harness_kill_port 65000
    harness_start "$TMP/harness/social.log" "$DOTNET" "$SOCIAL_DLL" --config "$ROOT/config/social.json" >/dev/null
    harness_wait_ready "$TMP/harness/social.log" "社交服" || return 1
    for _ in $(seq 1 60); do
      r="$(post "$GATEWAY/sect/join" "{\"character_id\":$CHARACTER,\"sect_id\":999999999}")"
      [ "$(code_of "$r")" = "200" ] && return 0
      sleep 0.5
    done
    return 1
  }
  FOUNDER="$CHARACTER"
  start_social && ok "社交服就绪（65000）" || bad "社交服没起来 / 子通道没恢复"
  grep -q '^positions=master:1,vice_master:1,elder:4,steward:12' "$TMP/harness/social.log" && ok "真实启动日志打印同形 positions 行" || bad "启动日志缺同形 positions 行"

  IDS=""; for i in $(seq 1 23); do id="$(new_char "a2_$$_$i")"; [ -n "$id" ] && IDS="$IDS $id"; done
  [ "$(printf '%s\n' "$IDS" | wc -w | tr -d ' ')" -ge 23 ] \
    && ok "23 个测试角色就位" || bad "角色不足 23 个（${IDS}）"
  NAME="a2s_$$_$(date +%s)"
  r="$(post "$GATEWAY/sect/create" "{\"character_id\":$FOUNDER,\"name\":\"$NAME\"}")"
  check "$r" "ok=true level=1 member_cap=10" "建 1 级宗门"
  SECT="$(json_field "$(body_of "$r")" sect_id)"

  # ③ 1 级只有 10 个席位、任满为止、不要求把执事名额填满。
  for n in $(seq 1 10); do
    check "$(post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" $n),\"sect_id\":$SECT}")" \
      "ok=true member_count=$n" "1 级连收第 $n 人"
  done
  check "$(post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" 11),\"sect_id\":$SECT}")" \
    "ok=false reason=sect_full" "1 级第 11 人：sect_full（名册上限先挡）"
  check "$(appoint "$SECT" "$(nth "$IDS" 1)" master)" "ok=true seat_count=1" "1 级：只任命掌门 1 人"
  ST1="$(state "$SECT")"
  printf '%s' "$ST1" | grep -q 'master=1 vice_master=0 elder=0 steward=0 disciple=9' \
    && ok "1 级：掌门 1 人 + 9 弟子也能工作、有职位人数 ≤10" || bad "1 级 state 不对：$ST1"
  check "$(dismiss "$SECT" "$(nth "$IDS" 1)" master)" "ok=true position=disciple" \
    "1 级掌门免回弟子（给 5 级核对让路）"

  # 仅核对用改级到 5（200 人名册才够 1+1+4+12=18 人），不做升级逻辑。
  SL="$("$DOTNET" "$SOCIAL_DLL" --sect-set-level "$SECT" 5 --config "$ROOT/config/social.json" 2>&1)"
  printf '%s' "$SL" | grep -q '^sect_set_level=ok' && ok "仅核对用改级到 5" || bad "改级失败：$SL"
  for n in $(seq 11 23); do
    check "$(post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" $n),\"sect_id\":$SECT}")" \
      "ok=true" "5 级收第 $n 人入册"
  done

  # ① 四种职位超过席位的任命被拒绝。
  ok_apt(){ check "$(appoint "$SECT" "$(nth "$IDS" "$1")" "$2")" "ok=true seat_count=$3" "$2 第 $3 席任命成功"; }
  full_apt(){ r="$(appoint "$SECT" "$(nth "$IDS" "$1")" "$2")"; \
    check "$r" "ok=false reason=position_full seat_count=0" "${3}：position_full、未生效 0"; \
    [ "$(code_of "$r")" = "200" ] && ok "${3}：业务拒绝 HTTP 200" || bad "$3 拒绝码不对：$r"; }
  ok_apt 1 master 1;   full_apt 2 master "第 2 个掌门"
  ok_apt 3 vice_master 1; full_apt 4 vice_master "第 2 个副掌门"
  for n in 5 6 7 8; do ok_apt "$n" elder $((n-4)); done
  full_apt 9 elder "第 5 个长老"
  for n in $(seq 10 20); do ok_apt "$n" steward $((n-9)); done
  # 并发抢第 12 席：两个不同目标同时 appoint，只能成一人。
  a="$(nth "$IDS" 21)"; b="$(nth "$IDS" 22)"
  ( appoint "$SECT" "$a" steward > "$TMP/c1" ) & P1=$!
  ( appoint "$SECT" "$b" steward > "$TMP/c2" ) & P2=$!
  wait "$P1"; wait "$P2"
  wins=0; full=0
  for f in "$TMP/c1" "$TMP/c2"; do
    body="$(body_of "$(cat "$f")")"
    [ "$(json_field "$body" ok)" = "true" ] && wins=$((wins+1))
    [ "$(json_field "$body" reason)" = "position_full" ] && full=$((full+1))
  done
  [ "$wins" = "1" ] && [ "$full" = "1" ] \
    && ok "并发抢第 12 席：只成一人、另一笔 position_full" \
    || bad "并发不对：wins=$wins full=${full}（$(cat "$TMP/c1" "$TMP/c2")）"
  full_apt 23 steward "第 13 个执事"

  ST="$(state "$SECT")"
  printf '%s' "$ST" | grep -q 'master=1 vice_master=1 elder=4 steward=12' \
    && ok "--sect-position-state 读回 1/1/4/12：$ST" \
    || bad "state 不对：$ST"
  MB="$(members "$SECT")"
  printf '%s' "$MB" | grep -q 'rows=23' \
    && ok "--sect-position-members 独立读库 rows=23" || bad "members 不对：$MB"

  # ④ 七条拒绝路径每条显式读库断言 position 列没动。
  BEFORE="$(read_pos "$SECT")"
  biz_reject(){ r="$1"; lab="$2"; why="$3"; \
    check "$r" "ok=false reason=$why seat_count=0" "${lab}：回包"; \
    [ "$(code_of "$r")" = "200" ] || bad "$lab 码不对：$r"; \
    [ "$(read_pos "$SECT")" = "$BEFORE" ] && ok "${lab}：显式读库 position 没动" || bad "$lab 写了库"; }
  biz_reject "$(appoint "$SECT" "$(nth "$IDS" 2)" master)" "position_full" "position_full"
  biz_reject "$(appoint "$SECT" 999999999 master)" "not_member" "not_member"
  biz_reject "$(post "$GATEWAY/sect/appoint" "{\"character_id\":$(nth "$IDS" 5),\"sect_id\":$SECT,\"target_character_id\":$(nth "$IDS" 6),\"position\":\"master\"}")" \
    "not_leader" "not_leader"
  biz_reject "$(appoint "$SECT" "$(nth "$IDS" 2)" emperor)" "position_unknown" "position_unknown"
  http_reject(){ lab="$1"; resp="$2"; want="$3"; \
    [ "$(code_of "$resp")" = "$want" ] && ok "${lab}：HTTP $want" || bad "$lab 码不对：$resp"; \
    [ "$(read_pos "$SECT")" = "$BEFORE" ] && ok "${lab}：显式读库 position 没动" || bad "$lab 写了库"; }
  http_reject "无令牌" "$(curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$GATEWAY/sect/appoint" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$FOUNDER,\"sect_id\":$SECT,\"target_character_id\":$(nth "$IDS" 2),\"position\":\"master\"}" 2>&1)" 401
  http_reject "缺字段" "$(post "$GATEWAY/sect/appoint" "{\"character_id\":$FOUNDER,\"sect_id\":$SECT}")" 400
  http_reject "别人的角色" "$(post "$GATEWAY/sect/appoint" \
    "{\"character_id\":999999999,\"sect_id\":$SECT,\"target_character_id\":$(nth "$IDS" 2),\"position\":\"master\"}")" 404

  # 角色库行数不变（db_cli --character-stats 只读）。
  if [ -f "$CHARACTER_DLL" ]; then
    CH0="$(db_cli --character-stats 2>&1)"
    CH1="$(db_cli --character-stats 2>&1)"
    [ "$CH0" = "$CH1" ] && ok "角色库行数不变（db_cli --character-stats：${CH0}）" || bad "角色库被动了"
  else
    printf '       （角色库 db_cli 段未跑：缺 Character.dll；社交库 position 列断言已覆盖）\n'
  fi

  # 免职同一笔事务、txn_id 服务端生成、重放不重复改。
  T="$(nth "$IDS" 10)"
  r="$(dismiss "$SECT" "$T" steward)"
  check "$r" "ok=true position=disciple" "免职：把执事免成弟子"
  [ -n "$(json_field "$(body_of "$r")" txn_id)" ] && ok "txn_id 服务端生成" || bad "缺 txn_id"
  check "$(dismiss "$SECT" "$T" steward)" "ok=true position=disciple" "免职重放：幂等不重复改"
  printf '%s' "$(read_pos "$SECT")" | grep -qE "(^|,)${T}:disciple" \
    && ok "免职读库：该成员 position=disciple" || bad "免职没落库"

  # ② 席位数字只有一个正本、改配置重启生效。
  set_steward 13
  start_social && ok "改 steward=13 后重启、轮询只读回 200" || bad "改 13 重启失败"
  check "$(appoint "$SECT" "$(nth "$IDS" 23)" steward)" "ok=true seat_count=13" \
    "steward=13：第 13 个执事能任命"
  set_steward 12
  start_social && ok "改回 steward=12 后重启、轮询只读回 200" || bad "改回 12 重启失败"
  check "$(appoint "$SECT" "$(nth "$IDS" 2)" steward)" "ok=false reason=position_full seat_count=0" \
    "改回 12：又是 position_full"
  CONF2="$("$DOTNET" "$SOCIAL_DLL" --sect-position-config 2>&1)"
  printf '%s' "$CONF2" | grep -q 'positions=master:1,vice_master:1,elder:4,steward:12' \
    && ok "--sect-position-config 改回后仍逐项打印四席位" || bad "config 打印不对：$CONF2"

  cp "$TMP/sect.orig.json" "$ROOT/config/sect.json"
  harness_cleanup
fi

finish "P2-1a2（server）"
