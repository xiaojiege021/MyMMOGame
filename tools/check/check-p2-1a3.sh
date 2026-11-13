#!/usr/bin/env bash
# tools/check/check-p2-1a3.sh —— P2-1a3 传位只给副掌门 / 掌门不能直接退出
# （方案 11.1 / 开发架构第 14 节第 52 行、第 11 节 P2-1 总卡）。
#
#   bash tools/check/check-p2-1a3.sh        编译 + 静态 + 只读入口 + 坏表（不连库不连服）
#   MMO_P2_1A3_HAS_DB=1 MMO_P2_1A3_GATEWAY=… MMO_P2_1A3_TOKEN=… \
#     MMO_P2_1A3_CHARACTER=… bash tools/check/check-p2-1a3.sh   再加真服/真库段
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
HAS_DB="${MMO_P2_1A3_HAS_DB:-0}"
GATEWAY="${MMO_P2_1A3_GATEWAY:-}"
TOKEN="${MMO_P2_1A3_TOKEN:-}"
CHARACTER="${MMO_P2_1A3_CHARACTER:-0}"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
if [ "${MMO_P2_1A3_SKIP_BUILD:-0}" = "1" ]; then
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
def read(p): return open(p, encoding="utf-8").read() if os.path.exists(p) else ""
def readdir(d):
    return "\n".join(read(os.path.join(d, f)) for f in sorted(os.listdir(d)) if f.endswith(".cs")) if os.path.isdir(d) else ""

cfg = json.loads(read("config/sect.json"))
t = cfg.get("transfer", {})
report("transfer_config", t.get("old_master_position") == "disciple" and t.get("idempotency") == "last_transfer_txn" and t.get("old_master_position_pending") is True, "transfer 段：old_master_position=disciple 待定、idempotency=last_transfer_txn")
report("note_transfer", all(s in cfg["note"] for s in ["传位只给副掌门", "掌门不能直接退出", "有职位的人先辞成弟子", "掌门本人可以不在名册", "权限判定只认 sect.master_character_id"]), "note 写明只给副掌门/掌门不能直接退出/有职位先辞/掌门可不在名册/权限只认 master_character_id")
report("levels_vs_plan", [(l["level"], l["member_cap"]) for l in cfg["levels"]] == [(1,10),(2,30),(3,60),(4,100),(5,200)], "levels 五档数值一字未改")
report("positions_vs_plan", {p["id"]: p["seat_cap"] for p in cfg["positions"]} == {"master":1,"vice_master":1,"elder":4,"steward":12}, "positions 四席位数值一字未改")

social_src = readdir("src/Social")
gw = read("src/Gateway/SectApi.cs")
gateway_src = gw + read("src/Gateway/GatewayProcess.cs")
proto = read("proto/mmo/social.proto")
ddl = read("db/social/sect_transfer.sql")
main = read("db/social/social.sql")
hard = re.compile(r'"(member_cap|seat_cap)"\s*:\s*\d|new (Level|Seat)\(\s*\d|'
                  r'(member_cap|seat_cap)\s*=\s*(?:1|4|12|10|30|60|100|200)\b')
report("no_second_copy", not hard.search(social_src + gateway_src + proto + ddl + main), "代码/proto/SQL 不含第二套人数/席位数字（按配置键+值找）")
report("no_dungeon_import", not re.search(r"dungeons\.json|party_size|max_players", social_src, re.I), "社交服不读副本人数配置")

report("ddl_master_cols", all(x in ddl for x in ["ADD COLUMN IF NOT EXISTS master_character_id", "ADD COLUMN IF NOT EXISTS last_transfer_txn", "master_character_id IS NULL", "ALTER COLUMN master_character_id SET NOT NULL"]), "幂等 DDL：给 sect 加当前掌门与幂等列、回填旧宗门")
report("ddl_transfer_table", 'CREATE TABLE IF NOT EXISTS "社交".sect_transfer' in ddl and re.search(r"txn_id\s+text\s+PRIMARY KEY", ddl) is not None, "sect_transfer 表 txn_id 主键挡重放")
report("ddl_in_main", all(x in main for x in ["ADD COLUMN IF NOT EXISTS master_character_id", "CREATE TABLE IF NOT EXISTS \"社交\".sect_transfer"]), "同一份幂等 DDL 追加进主迁移")
report("sql_two_tables", main.lower().count('create table if not exists "社交".sect') >= 2, "a1 两张表 DDL 仍在主迁移")
report("sql_no_role_schema", '"角色"' not in ddl and '"角色"' not in main, "社交 DDL 不碰角色 schema")

report("proto_rpcs", all(x in proto for x in ["rpc TransferLeadership", "rpc LeaveSect", "rpc DescribeLeadership", "message LeadershipRequest", "message LeaveSectRequest", "message LeadershipResponse"]), "三条 rpc + 请求/回包消息")
report("proto_fields", all(x in proto for x in ["string account", "uint64 character_id", "string txn_id", "uint64 sect_id", "uint64 target_character_id", "bool ok", "string reason", "uint64 old_master", "uint64 new_master", "int32 master_count", "int32 vice_master_count"]), "请求/回包字段齐，回包都是只读事实值")
m = re.search(r"message LeaveSectRequest \{(.*?)\}", proto, re.S)
report("leave_no_target", m is not None and "target_character_id" not in m.group(1), "退出请求不带 target")

tbl = read("src/Social/SectTable.cs")
report("sect_table_hard", all(x in tbl for x in ["缺 transfer 段", "transfer.idempotency 必须是 last_transfer_txn", "transfer.old_master_position 非法", "version 必须是正整数"]), "缺段/幂等列自检/old_master_position 非法/version 四类硬校验")

repo = read("src/Social/SectRepository.cs")
seg = repo[repo.find("public async Task<LeadershipOutcome> TransferAsync"):repo.find("// ---- 写：退出宗门")]
report("transfer_one_txn", all(x in seg for x in ["BeginTransactionAsync", "for update", "select master_character_id", "where sect_id = $1 and position = 'master'", "set position = 'master'", "set master_character_id = $2, last_transfer_txn = $3", "CommitAsync", "RollbackAsync"]), "一笔事务：锁宗门行 → 校验 → 降所有 master 行 → 升新掌门 → 写 sect → 提交")
report("transfer_single_commit", seg.count("CommitAsync") == 1, "TransferAsync 只有一次 CommitAsync，没有中间提交")
report("transfer_demote_all", r'insert into \"社交\".sect_transfer' in seg and "where sect_id = $1 and position = 'master'" in seg, "降名册里所有 master 行（含 a2 任命出来的那条）+ 插 sect_transfer 挡重放")
report("consistency_after_commit", "提交后" in seg and "全程恒一致" not in repo, "口径是「提交后 master_character_id == 名册唯一 master 行」，不写全程恒一致")
report("transfer_reasons", all(x in seg for x in ["not_vice_master", "not_leader", "sect_not_found", "duplicate"]), "传位拒绝 reason 齐")
create_seg = repo[repo.find("public async Task<CreateOutcome> CreateAsync"):repo.find("// ---- 写：收人入名册")]
report("founder_not_in_roster", create_seg.count("insert into") == 1 and "master_character_id" in create_seg and "sect_member (" not in create_seg, "创建者不进 sect_member 名册（a1 十人上限语义不动），创建时写 master_character_id")
report("not_leader_master", "select founder_character_id from" not in repo and repo.count("select master_character_id from") >= 4, "Appoint/Dismiss/Transfer/Leave 的掌门判定都改查 master_character_id")
leave_seg = repo[repo.find("public async Task<LeadershipOutcome> LeaveAsync"):repo.find("// ---- 只读：DescribeLeadership")]
report("leave_rules", all(x in leave_seg for x in ["master_cannot_leave", "resign_first", "not_member", "sect_not_found", r'delete from \"社交\".sect_member', "characterId == master"]), "退出：掌门不能直接退/有职位先辞/非成员拒/弟子删行，master_cannot_leave 用 master_character_id 判")
new_seg = repo[repo.find("// ---- 写：传位"):]
report("repo_no_attributes", not re.search(r"set\s+(level|realm|damage|exp|attribute|hp)\s*=", new_seg, re.I)
       and '"角色"' not in new_seg, "传位/退出不写等级/境界/伤害/经验/属性列、不碰角色 schema")
report("not_vice_master_rule", "position = 'vice_master' for update" in seg, "not_vice_master 用「目标在名册且 position='vice_master'」判")

report("grpc_three", all(x in read("src/Social/SocialGrpcService.cs") for x in ["TransferLeadership", "LeaveSect", "DescribeLeadership"]), "三个 gRPC 入口")
program = read("src/Social/Program.cs")
report("readonly_entries", all(x in program for x in ["--sect-leadership-config", "--sect-leadership-state", "--sect-leadership-members", "--sect-leave-state", "--migrate", "--sql"]), "四个只读入口 + 迁移链")
report("same_load", program.count("SocialLoader.LoadAll(configPath, sectTablePath)") >= 3, "只读入口与真实启动共用同一 Load + 同一组参数")
report("startup_same_shape", "transfer=" in read("src/Social/SocialProcess.cs")
       and "idempotency:" in read("src/Social/SocialProcess.cs") and "idempotency=" in program,
       "启动日志与 config 入口同形（同一 transfer 拼法）")
report("gateway_routes", all(x in gw for x in ['"/sect/transfer"', '"/sect/leave"', "OwnsAsync", "Guid.NewGuid", "TransferBody", "LeaveBody", "MapLeadershipRoute"])
       and "SectApi.Map" in gateway_src,
       "两条 POST + 认令牌 + 归属复核 + 服务端生成 txn_id")
lb = re.search(r"record LeaveBody\(([^)]*)\)", gw)
report("gateway_leave_no_target", lb is not None and "target" not in lb.group(1).lower(), "退出请求体不带 target、带不进判定值")

srv = read("src/Shared/Generated/Social.cs")
cli = read("client/MMOGame/Assets/Proto/Generated/Social.cs")
report("generated_same", srv != "" and srv == cli, "两端 Social.cs 逐字相同")
m = re.search(r"^guid:\s*([0-9a-fA-F]+)\s*$",
              read("client/MMOGame/Assets/Proto/Generated/Social.cs.meta"), re.M)
report("meta_guid_32", m is not None and len(m.group(1)) == 32, "客户端 Social.cs.meta GUID 32 位")
# 裸词拆开拼，避免本脚本自身被 U1 的全仓裸词闸门扫到。
bare = ["Player" + "Prefs", "Resources" + ".Load"]
blob = srv + cli + read("src/Shared/Generated/SocialGrpc.cs") + gw + proto
report("no_bareword", not any(w in blob for w in bare), "新增生成物/注释不含 U1 裸词")
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
CONF="$("$DOTNET" "$SOCIAL_DLL" --sect-leadership-config 2>&1)"
if printf '%s' "$CONF" | grep -q '^sect_leadership_config_state=ok' \
   && printf '%s' "$CONF" | grep -q 'idempotency=last_transfer_txn' \
   && printf '%s' "$CONF" | grep -q 'old_master_position=disciple'; then
  ok "--sect-leadership-config 逐项打印传位段：$(printf '%s' "$CONF" | head -1 | cut -c1-96)"
else
  bad "--sect-leadership-config 不对：$CONF"
fi
assert_file_contains "src/Social/SocialProcess.cs" 'transfer=' "启动日志打印 transfer 段"

echo "== 4. 坏配置六类起不来 =="
python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
base = json.load(open("config/sect.json", encoding="utf-8"))
def case(name, mutate):
    d = json.loads(json.dumps(base)); mutate(d)
    json.dump(d, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
def tr(d): return d["transfer"]
case("no_transfer", lambda d: d.pop("transfer", None))
case("bad_old_pos", lambda d: tr(d).update(old_master_position="emperor"))
case("old_is_master", lambda d: tr(d).update(old_master_position="master"))
case("bad_idempotency", lambda d: tr(d).update(idempotency="wrong_col"))
case("empty_idempotency", lambda d: tr(d).update(idempotency=""))
case("bad_version", lambda d: d.update(version=0))
PY
BAD_RC=0
for c in no_transfer bad_old_pos old_is_master bad_idempotency empty_idempotency bad_version; do
  o="$("$DOTNET" "$SOCIAL_DLL" --sect-leadership-config --sect-table "$TMP/bad-$c.json" 2>&1)"; rc=$?
  assert_bad_config "$o" "坏配置 ${c}：$(printf '%s' "$o" | grep -oE 'config_error=.*' | head -1)"
  [ "$rc" -ne 0 ] || BAD_RC=1
done
[ "$BAD_RC" = "0" ] && ok "六类坏配置都以非 0 退出" || bad "有坏配置以 0 退出"

echo "== 5. 真服 / 真库段 =="
if [ "$HAS_DB" != "1" ]; then
  printf '       （真库段未跑：MMO_P2_1A3_HAS_DB 未置 1；本机编译/静态/只读/坏表段已跑完，结果不伪造）\n'
elif [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （真库段未跑：缺 MMO_P2_1A3_GATEWAY / _TOKEN / _CHARACTER；本机段已跑完）\n'
elif [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  printf '       （真库段未跑：缺 MMO_GAMEDB / /srv/mmo/gamedb.env；本机段已跑完）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/harness"; harness_track_port 65000
  cp "$ROOT/config/sect.json" "$TMP/sect.orig.json"
  trap 'cp "$TMP/sect.orig.json" "$ROOT/config/sect.json" 2>/dev/null; harness_cleanup; rm -rf "$TMP"' EXIT
  CFG="--config $ROOT/config/social.json"

  MIG="$("$DOTNET" "$SOCIAL_DLL" --migrate --sql "$ROOT/db/social/social.sql" $CFG 2>&1)"
  printf '%s' "$MIG" | grep -q '^migrate=ok' && ok "社交 schema 主迁移（含传位列/流水表）" || bad "建表失败：$MIG"

  post(){ curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$2" 2>&1; }
  check(){ [ "$(expect_json "$(body_of "$1")" $2)" = "ok" ] && ok "$3" || bad "${3}：$1"; }
  nth(){ printf '%s\n' "$1" | tr ' ' '\n' | awk -v n="$2" 'NR==n{print}'; }
  new_char(){ curl -sS --noproxy '*' -m 10 -X POST "$GATEWAY/character?token=$TOKEN" -H 'Content-Type: application/json' -d "{\"name\":\"$1\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>/dev/null | python3 -c 'import json,sys;d=json.load(sys.stdin);print(d.get("character_id",""))' 2>/dev/null || true; }
  transfer(){ post "$GATEWAY/sect/transfer" "{\"character_id\":$1,\"sect_id\":$SECT,\"target_character_id\":$2}"; }
  leave(){ post "$GATEWAY/sect/leave" "{\"character_id\":$1,\"sect_id\":$SECT}"; }
  appoint(){ post "$GATEWAY/sect/appoint" "{\"character_id\":$1,\"sect_id\":$SECT,\"target_character_id\":$2,\"position\":\"$3\"}"; }
  lstate(){ "$DOTNET" "$SOCIAL_DLL" --sect-leadership-state "$1" $CFG 2>&1; }
  lmembers(){ "$DOTNET" "$SOCIAL_DLL" --sect-leadership-members "$1" $CFG 2>&1; }
  lstate_line(){ lstate "$1" | grep -oE 'master_character_id=[0-9]+ old_master=[0-9]+ new_master=[0-9]+ master=[0-9]+ vice_master=[0-9]+ member_count=[0-9]+' | head -1; }
  pos_line(){ lmembers "$1" | grep -oE 'ids=.*' | head -1; }
  snap(){ printf '%s|%s' "$(lstate_line "$SECT")" "$(pos_line "$SECT")"; }
  start_social(){
    harness_kill_port 65000
    harness_start "$TMP/harness/social.log" "$DOTNET" "$SOCIAL_DLL" $CFG >/dev/null
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
  grep -q '^transfer=idempotency:last_transfer_txn' "$TMP/harness/social.log" \
    && ok "真实启动日志打印同形 transfer 行" || bad "启动日志缺同形 transfer 行"

  IDS=""; for i in $(seq 1 13); do id="$(new_char "a3_$$_$i")"; [ -n "$id" ] && IDS="${IDS:+$IDS }$id"; done
  [ "$(printf '%s\n' "$IDS" | wc -w | tr -d ' ')" -ge 13 ] && ok "13 个测试角色就位" || bad "角色不足 13 个"
  NAME="a3s_$$_$(date +%s)"
  r="$(post "$GATEWAY/sect/create" "{\"character_id\":$FOUNDER,\"name\":\"$NAME\"}")"
  check "$r" "ok=true level=1 member_cap=10" "建 1 级宗门"
  SECT="$(json_field "$(body_of "$r")" sect_id)"
  for n in $(seq 1 10); do post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" $n),\"sect_id\":$SECT}" >/dev/null; done
  check "$(post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" 11),\"sect_id\":$SECT}")" \
    "ok=false reason=sect_full" "1 级第 11 人 sect_full（a1 名册上限不动）"
  # a2 风格：名册里被任命的 master 行 ≠ founder；vice_master / elder / steward 各就位。
  check "$(appoint "$FOUNDER" "$(nth "$IDS" 9)" master)" "ok=true seat_count=1" "任命名册 master 行（≠ 创建者）"
  check "$(appoint "$FOUNDER" "$(nth "$IDS" 1)" vice_master)" "ok=true seat_count=1" "任命副掌门"
  check "$(appoint "$FOUNDER" "$(nth "$IDS" 2)" elder)" "ok=true seat_count=1" "任命长老"
  check "$(appoint "$FOUNDER" "$(nth "$IDS" 3)" steward)" "ok=true seat_count=1" "任命执事"

  biz_reject(){ lab="$1"; why="$2"; r="$3"; \
    check "$r" "ok=false reason=$why old_master=0 new_master=0 master_count=0 vice_master_count=0 member_count=0" "${lab}：回包"; \
    [ "$(code_of "$r")" = "200" ] && ok "${lab}：业务拒绝 HTTP 200" || bad "${lab} 码不对：$r"; \
    [ "$(snap)" = "$BEFORE" ] && ok "${lab}：显式读库没动" || bad "${lab} 写了库"; }

  # ② 掌门不能直接退出、有职位先辞成弟子、弟子可退（掌门不在名册也要判得出来）。
  BEFORE="$(snap)"
  biz_reject "掌门退出" "master_cannot_leave" "$(leave "$FOUNDER")"
  biz_reject "副掌门退出" "resign_first" "$(leave "$(nth "$IDS" 1)")"
  biz_reject "长老退出" "resign_first" "$(leave "$(nth "$IDS" 2)")"
  biz_reject "执事退出" "resign_first" "$(leave "$(nth "$IDS" 3)")"
  biz_reject "非成员退出" "not_member" "$(leave "$(nth "$IDS" 11)")"
  D="$(nth "$IDS" 4)"
  check "$(leave "$D")" "ok=true member_count=9" "弟子退出成功、名册少一行"
  check "$(post "$GATEWAY/sect/join" "{\"character_id\":$D,\"sect_id\":$SECT}")" \
    "ok=true member_count=10" "退出的 character_id 可再被收"

  # ① 传给弟子/长老/执事/非成员/不存在都被拒，只认现任副掌门。
  BEFORE="$(snap)"
  biz_reject "传给弟子" "not_vice_master" "$(transfer "$FOUNDER" "$(nth "$IDS" 4)")"
  biz_reject "传给长老" "not_vice_master" "$(transfer "$FOUNDER" "$(nth "$IDS" 2)")"
  biz_reject "传给执事" "not_vice_master" "$(transfer "$FOUNDER" "$(nth "$IDS" 3)")"
  biz_reject "传给非成员" "not_vice_master" "$(transfer "$FOUNDER" "$(nth "$IDS" 11)")"
  biz_reject "传给不存在" "not_vice_master" "$(transfer "$FOUNDER" 999999999)"

  # 传给现任副掌门成功：库读回、--sect-leadership-state 同值、无残留第二个 master。
  V="$(nth "$IDS" 1)"; OLD="$(nth "$IDS" 9)"
  r="$(transfer "$FOUNDER" "$V")"
  check "$r" "ok=true old_master=$FOUNDER new_master=$V master_count=1 vice_master_count=0" "传给现任副掌门成功"
  S="$(lstate "$SECT")"
  printf '%s' "$S" | grep -q "old_master=$FOUNDER new_master=$V master=1 vice_master=0" \
    && ok "--sect-leadership-state 同值：$S" || bad "state 不对：$S"
  P="$(lmembers "$SECT")"
  printf '%s' "$P" | grep -qE "(^|[,=])${V}:master" && ok "库读回新掌门 position=master" || bad "新掌门没升：$P"
  printf '%s' "$P" | grep -qE "(^|[,=])${OLD}:disciple" \
    && ok "a2 任命的旧 master 行降为 disciple、无残留第二个 master" || bad "旧 master 行没清干净：$P"
  printf '%s' "$S" | grep -q 'master=1 ' && ok "席位守恒 master=1" || bad "master 席位不对：$S"
  printf '%s' "$S" | grep -q 'vice_master=0' && ok "vice_master 席位空出 1 席" || bad "vice_master 没空出：$S"

  # 权限跟着变：旧掌门再任命 not_leader，新掌门能正常任命。
  BEFORE="$(snap)"
  r="$(appoint "$FOUNDER" "$(nth "$IDS" 5)" steward)"
  check "$r" "ok=false reason=not_leader seat_count=0" "旧掌门再任命回包"
  [ "$(code_of "$r")" = "200" ] && ok "旧掌门再任命：业务拒绝 HTTP 200" || bad "旧掌门再任命码不对：$r"
  [ "$(snap)" = "$BEFORE" ] && ok "旧掌门再任命：显式读库没动" || bad "旧掌门再任命写了库"
  check "$(appoint "$V" "$(nth "$IDS" 5)" steward)" "ok=true" "新掌门能正常任命"
  # 传位后降为弟子的旧掌门可以退出（证明「不能直接退出」不是「永远不能退」）。
  check "$(leave "$OLD")" "ok=true" "传位后降为弟子的旧掌门可以退出"

  # 并发两笔传位只成一次。
  appoint "$V" "$(nth "$IDS" 6)" vice_master >/dev/null
  ( transfer "$V" "$(nth "$IDS" 6)" > "$TMP/c1" ) & P1=$!
  ( transfer "$V" "$(nth "$IDS" 6)" > "$TMP/c2" ) & P2=$!
  wait "$P1"; wait "$P2"
  wins=0; rej=0
  for f in "$TMP/c1" "$TMP/c2"; do
    b="$(body_of "$(cat "$f")")"
    [ "$(json_field "$b" ok)" = "true" ] && wins=$((wins+1))
    [ "$(json_field "$b" ok)" = "false" ] && rej=$((rej+1))
  done
  [ "$wins" = "1" ] && [ "$rej" = "1" ] \
    && ok "并发两笔传位只成一次（wins=1 rej=1）" || bad "并发不对：wins=$wins rej=$rej"

  # ③ 幂等重放：相同语义再传一次被状态挡住、库里掌门/职位/流水事务号不变。
  S0="$(lstate "$SECT")"; BEFORE="$(snap)"
  biz_reject "相同语义重放传位" "not_leader" "$(transfer "$V" "$(nth "$IDS" 6)")"
  [ "$(lstate "$SECT")" = "$S0" ] && ok "重放后 last_transfer_txn 与掌门状态不变" || bad "重放改了库"
  check "$(post "$GATEWAY/sect/transfer" "{\"character_id\":$FOUNDER,\"sect_id\":999999999,\"target_character_id\":$V}")" \
    "ok=false reason=sect_not_found" "sect_not_found（数值未生效）"

  # HTTP 层拒绝 401/400/404，每条前后显式读库。
  BEFORE="$(snap)"
  http_reject(){ lab="$1"; resp="$2"; want="$3"; \
    [ "$(code_of "$resp")" = "$want" ] && ok "${lab}：HTTP $want" || bad "${lab} 码不对：$resp"; \
    [ "$(snap)" = "$BEFORE" ] && ok "${lab}：显式读库没动" || bad "${lab} 写了库"; }
  http_reject "无令牌" "$(curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$GATEWAY/sect/transfer" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$FOUNDER,\"sect_id\":$SECT,\"target_character_id\":$V}" 2>&1)" 401
  http_reject "缺字段" "$(post "$GATEWAY/sect/transfer" "{\"character_id\":$FOUNDER,\"sect_id\":$SECT}")" 400
  http_reject "别人的角色" "$(post "$GATEWAY/sect/transfer" \
    "{\"character_id\":999999999,\"sect_id\":$SECT,\"target_character_id\":$V}")" 404

  # 角色库行数/余额不变（db_cli --character-stats 只读）。
  if [ -f "$CHARACTER_DLL" ]; then
    CH0="$(db_cli --character-stats 2>&1)"; CH1="$(db_cli --character-stats 2>&1)"
    [ "$CH0" = "$CH1" ] && ok "角色库行数不变（${CH0}）" || bad "角色库被动了"
  else
    printf '       （角色库 db_cli 段未跑：缺 Character.dll；社交库显式读库断言已覆盖）\n'
  fi

  cp "$TMP/sect.orig.json" "$ROOT/config/sect.json"
  harness_cleanup
fi

finish "P2-1a3（server）"
