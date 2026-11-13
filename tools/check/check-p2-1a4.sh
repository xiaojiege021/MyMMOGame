#!/usr/bin/env bash
# tools/check/check-p2-1a4.sh —— P2-1a4 升级只耗建设
# （方案 11.1 / 开发架构 4.4、第 14 节第 53 行、第 11 节 P2-1 总卡）。
#
#   bash tools/check/check-p2-1a4.sh        编译 + 静态 + 只读入口 + 坏表（不连库不连服）
#   MMO_P2_1A4_HAS_DB=1 MMO_P2_1A4_GATEWAY=… MMO_P2_1A4_TOKEN=… \
#     MMO_P2_1A4_CHARACTER=… bash tools/check/check-p2-1a4.sh   再加真服/真库段
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
SOCIAL_DLL="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
HAS_DB="${MMO_P2_1A4_HAS_DB:-0}"
GATEWAY="${MMO_P2_1A4_GATEWAY:-}"
TOKEN="${MMO_P2_1A4_TOKEN:-}"
CHARACTER="${MMO_P2_1A4_CHARACTER:-0}"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
if [ "${MMO_P2_1A4_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译\n'
elif "$DOTNET" build src/Social/Social.csproj -c Release -v quiet > "$TMP/build.log" 2>&1 \
     && "$DOTNET" build src/Gateway/Gateway.csproj -c Release -v quiet >> "$TMP/build.log" 2>&1; then
  ok "编译 Social + Gateway"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言 =="
python3 - "$TMP/static.txt" <<'PY'
import glob, json, os, re, sys
out = open(sys.argv[1], "w", encoding="utf-8")
def report(k, cond, info=""):
    out.write(("PASS " if cond else "FAIL ") + k + ("" if cond else "  " + info) + "\n")
def read(p): return open(p, encoding="utf-8").read() if os.path.exists(p) else ""
def readdir(d):
    return "\n".join(read(os.path.join(d, f)) for f in sorted(os.listdir(d)) if f.endswith(".cs")) if os.path.isdir(d) else ""

cfg = json.loads(read("config/sect.json"))
u = cfg.get("upgrade", {})
costs = u.get("costs", [])
report("upgrade_tiers_count", len(costs) == 4, "upgrade 四档齐全（1→2/2→3/3→4/4→5）")
report("upgrade_tiers_shape", [(c.get("from_level"), c.get("to_level")) for c in costs] == [(1,2),(2,3),(3,4),(4,5)], "四档等级连续、5 级封顶不再配 cost")
report("upgrade_costs_positive", all(isinstance(c.get("cost"), int) and c["cost"] > 0 for c in costs), "四档 cost 都是正整数占位（能真跑通建设不足被拒）")
report("upgrade_pending", u.get("upgrade_cost_pending") is True, "消耗数字标待定（方案没给）")
report("upgrade_idempotency", u.get("idempotency") == "last_upgrade_txn", "idempotency 钉 sect.last_upgrade_txn")
note = cfg.get("note", "") + u.get("note", "")
report("note_sources", all(s in note for s in ["宗门秘境", "宗门任务", "不从商城买", "待定", "master_character_id", "not_leader", "P2-1b", "商城买不到建设"]), "note 写明来源只有秘境/任务、不从商城买、发起者掌门待定")
configs = "\n".join(read(p) for p in sorted(glob.glob("config/*.json")))
report("no_shop_buy_claim", "商城可买建设" not in configs, "任何配置都不允许出现「商城可买建设」")
report("levels_vs_plan", [(l["level"], l["member_cap"]) for l in cfg["levels"]] == [(1,10),(2,30),(3,60),(4,100),(5,200)], "五档人数一字未改")
report("positions_vs_plan", {p["id"]: p["seat_cap"] for p in cfg["positions"]} == {"master":1,"vice_master":1,"elder":4,"steward":12}, "四席位一字未改")
report("transfer_config", cfg["transfer"].get("idempotency") == "last_transfer_txn" and cfg["transfer"].get("old_master_position") == "disciple", "传位段一字未改")

# 商城买不到建设：shop.json 18 条清单、items.json、ShopTable.DeliveredKinds
shop = json.loads(read("config/shop.json"))
items = json.loads(read("config/items.json"))
bad = re.compile(r"建设|贡献|construction|contribution", re.I)
report("shop_18_items", len(shop["items"]) == 18, "商城 18 条上架清单未变")
report("shop_no_construction", not bad.search(json.dumps(shop, ensure_ascii=False)), "shop.json 无建设/贡献类商品或发货编号")
report("items_no_construction", not bad.search(json.dumps(items, ensure_ascii=False)), "items.json 无建设/贡献类物品")
shoptable = read("src/Character/ShopTable.cs")
dk = re.search(r"DeliveredKinds\s*=\s*\{(.*?)\}", shoptable, re.S)
report("delivered_kinds_no_construction", dk is not None and not bad.search(dk.group(1)), "ShopTable.DeliveredKinds 无新增建设/贡献种类")

social_src = readdir("src/Social")
gw = read("src/Gateway/SectApi.cs")
gateway_src = gw + read("src/Gateway/GatewayProcess.cs")
proto = read("proto/mmo/social.proto")
ddl = read("db/social/sect_upgrade.sql")
main = read("db/social/social.sql")
hard = re.compile(r'"(member_cap|seat_cap)"\s*:\s*\d|new (Level|Seat)\(\s*\d|(member_cap|seat_cap)\s*=\s*(?:1|4|12|10|30|60|100|200)\b')
report("no_second_copy", not hard.search(social_src + gateway_src + proto + ddl + main), "代码/proto/SQL 不含第二套人数/席位数字")
costvals = [c["cost"] for c in costs]
hardcost = re.compile(r'\bcost\b\s*[:=]\s*(?:' + '|'.join(str(v) for v in costvals) + r')\b')
report("no_cost_copy", not hardcost.search(social_src + gateway_src + proto + ddl + main), "四档消耗只从 config/sect.json 读，代码/proto/SQL 不写第二套")

report("ddl_construction", all(x in ddl for x in ["ADD COLUMN IF NOT EXISTS construction bigint NOT NULL DEFAULT 0", "CHECK (construction >= 0)", "ADD COLUMN IF NOT EXISTS last_upgrade_txn"]), "幂等 DDL：sect 加建设列(>=0)与幂等事务号列")
report("ddl_upgrade_table", 'CREATE TABLE IF NOT EXISTS "社交".sect_upgrade' in ddl and re.search(r"txn_id\s+text\s+PRIMARY KEY", ddl) is not None and all(x in ddl for x in ["from_level", "to_level", "cost", "created_at"]), "sect_upgrade 表 txn_id 主键 + 四事实列")
report("ddl_in_main", all(x in main for x in ["ADD COLUMN IF NOT EXISTS construction bigint NOT NULL DEFAULT 0", 'CREATE TABLE IF NOT EXISTS "社交".sect_upgrade']), "同一份幂等 DDL 追加进主迁移")
report("sql_two_tables", main.lower().count('create table if not exists "社交".sect') >= 2, "a1 两表 DDL 仍在主迁移")
report("sql_preserves_a2_a3", all(x in main for x in ["ADD COLUMN IF NOT EXISTS position", "ADD COLUMN IF NOT EXISTS master_character_id", 'CREATE TABLE IF NOT EXISTS "社交".sect_transfer']), "a2 position 列 / a3 掌门列与传位表结构不动")
report("sql_no_role_schema", '"角色"' not in ddl and '"角色"' not in main, "社交 DDL 不碰角色 schema")

report("proto_rpcs", all(x in proto for x in ["rpc UpgradeSect", "rpc DescribeUpgrade", "message UpgradeSectRequest", "message UpgradeResponse"]), "两条 RPC + 请求/回包消息")
req = re.search(r"message UpgradeSectRequest \{(.*?)\}", proto, re.S)
report("proto_request_minimal", req is not None and all(x in req.group(1) for x in ["string account", "uint64 character_id", "string txn_id", "uint64 sect_id"]) and not re.search(r'\b(new_level|construction|upgrade_cost|member_cap|target_level)\b', req.group(1)), "请求只带 account/character_id/txn_id/sect_id，不带目标等级/建设数/消耗")
report("proto_response_fields", all(x in proto for x in ["bool ok", "string reason", "int32 old_level", "int32 new_level", "int32 member_cap", "int64 construction_before", "int64 construction_after", "int64 upgrade_cost", "string txn_id"]), "回包九个只读事实值齐全")

tbl = read("src/Social/SectTable.cs")
report("sect_table_hard", all(x in tbl for x in ["缺 upgrade 段", "cost 必须是正整数", "等级不连续", "必须正好", "last_upgrade_txn", "商城可买建设", "version 必须是正整数"]), "缺段/四档/cost>0/连续/5级封顶/version/反商城口径 七类硬校验")
report("capfor_sole_source", "public int CapFor(int level)" in tbl and "CostFor(int fromLevel)" in tbl and "CapFor(newLevel)" in read("src/Social/SectRepository.cs"), "CapFor 仍是名册上限唯一来源，消耗经 CostFor 读")

repo = read("src/Social/SectRepository.cs")
seg = repo[repo.find("public async Task<UpgradeOutcome> UpgradeAsync"):repo.find("// ---- 只读：DescribeUpgrade")]
report("upgrade_one_txn", all(x in seg for x in ["BeginTransactionAsync", "for update", "construction = construction - $2", "level = level + 1", r'insert into \"社交\".sect_upgrade', "last_upgrade_txn = $3", "CommitAsync", "RollbackAsync"]), "一笔事务：锁宗门行→校验→扣建设→level+1→插流水→提交")
report("upgrade_single_commit", seg.count("await transaction.CommitAsync") == 1, "UpgradeAsync 只有一次 CommitAsync，没有中间提交")
report("upgrade_idempotency", "sect_upgrade where txn_id = $1 for update" in seg and "last_upgrade_txn" in seg, "sect_upgrade.txn_id 主键 + sect.last_upgrade_txn 挡重放")
report("upgrade_reasons", all(x in seg for x in ["not_enough_construction", "at_max_level", "not_leader", "sect_not_found", "duplicate"]), "升级拒绝 reason 齐")
report("upgrade_no_role_money", '"角色"' not in seg and not re.search(r"grant_txn|ApplyGrant|set\s+currency", seg, re.I), "升级不碰角色 schema / 不动角色货币")

report("grpc_two", all(x in read("src/Social/SocialGrpcService.cs") for x in ["UpgradeSect", "DescribeUpgrade"]), "两个 gRPC 入口")
program = read("src/Social/Program.cs")
report("readonly_entries", all(x in program for x in ["--sect-upgrade-config", "--sect-upgrade-state", "--sect-upgrade-members", "--sect-construction-set", "--migrate", "--sql"]), "四个只读/核对入口 + 迁移链")
report("same_load", program.count("SocialLoader.LoadAll(configPath, sectTablePath)") >= 5, "只读入口与真实启动共用同一 Load + 同一组参数")
report("startup_same_shape", "upgrade_costs=" in read("src/Social/SocialProcess.cs") and "upgrade_costs=" in program, "启动日志与 config 入口同形（同一 upgrade_costs 拼法）")
report("gateway_route", all(x in gw for x in ['"/sect/upgrade"', "OwnsAsync", "Guid.NewGuid", "UpgradeBody", "UpgradeSectRequest"]) and "SectApi.Map" in gateway_src, "POST /sect/upgrade + 认令牌 + 归属复核 + 服务端生成 txn_id")
ub = re.search(r"record UpgradeBody\(([^)]*)\)", gw)
report("gateway_body_minimal", ub is not None and all(x in ub.group(1) for x in ["CharacterId", "SectId"]) and not re.search(r"Level|Cost|Construction|Target", ub.group(1), re.I), "升级请求体只带 character_id/sect_id")

srv = read("src/Shared/Generated/Social.cs")
cli = read("client/MMOGame/Assets/Proto/Generated/Social.cs")
report("generated_same", srv != "" and srv == cli, "两端 Social.cs 逐字相同")
report("generated_has_upgrade", "UpgradeSectRequest" in srv and "UpgradeResponse" in srv, "生成物含新消息")
m = re.search(r"^guid:\s*([0-9a-fA-F]+)\s*$", read("client/MMOGame/Assets/Proto/Generated/Social.cs.meta"), re.M)
report("meta_guid_32", m is not None and len(m.group(1)) == 32, "客户端 Social.cs.meta GUID 32 位")
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
EXPECT="$(python3 -c '
import json
d=json.load(open("config/sect.json"))
print("upgrade_costs=" + ",".join("%d->%d:%d"%(c["from_level"],c["to_level"],c["cost"]) for c in d["upgrade"]["costs"]))')"
CONF="$("$DOTNET" "$SOCIAL_DLL" --sect-upgrade-config 2>&1)"
if printf '%s' "$CONF" | grep -q '^sect_upgrade_config_state=ok' \
   && printf '%s' "$CONF" | grep -qF "$EXPECT" \
   && printf '%s' "$CONF" | grep -q 'levels=1:10,2:30,3:60,4:100,5:200' \
   && printf '%s' "$CONF" | grep -q 'max_level=5'; then
  ok "--sect-upgrade-config 逐档打印消耗与五档上限：$(printf '%s' "$CONF" | head -1 | cut -c1-100)"
else
  bad "--sect-upgrade-config 不对：$CONF"
fi
assert_file_contains "src/Social/SocialProcess.cs" 'upgrade_costs=' "启动日志打印同形 upgrade 段"

echo "== 4. 坏配置八类起不来 =="
python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
base = json.load(open("config/sect.json", encoding="utf-8"))
def case(name, mutate):
    d = json.loads(json.dumps(base)); mutate(d)
    json.dump(d, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
def cs(d): return d["upgrade"]["costs"]
case("no_upgrade", lambda d: d.pop("upgrade", None))
case("missing_tier", lambda d: d["upgrade"].update(costs=cs(d)[:3]))
case("extra_tier", lambda d: cs(d).append({"from_level":5,"to_level":6,"cost":1}))
case("cost_zero", lambda d: cs(d)[0].update(cost=0))
case("cost_negative", lambda d: cs(d)[1].update(cost=-5))
case("level_gap", lambda d: cs(d)[1].update(from_level=2, to_level=4))
case("bad_version", lambda d: d.update(version=0))
case("shop_buy_claim", lambda d: d["upgrade"].update(note=d["upgrade"]["note"] + " 商城可买建设"))
PY
BAD_RC=0
for c in no_upgrade missing_tier extra_tier cost_zero cost_negative level_gap bad_version shop_buy_claim; do
  o="$("$DOTNET" "$SOCIAL_DLL" --sect-upgrade-config --sect-table "$TMP/bad-$c.json" 2>&1)"; rc=$?
  assert_bad_config "$o" "坏配置 ${c}：$(printf '%s' "$o" | grep -oE 'config_error=.*' | head -1 | cut -c1-70)"
  [ "$rc" -ne 0 ] || BAD_RC=1
done
[ "$BAD_RC" = "0" ] && ok "八类坏配置都以非 0 退出" || bad "有坏配置以 0 退出"

echo "== 5. 真服 / 真库段 =="
if [ "$HAS_DB" != "1" ]; then
  printf '       （真库段未跑：MMO_P2_1A4_HAS_DB 未置 1；本机编译/静态/只读/坏表段已跑完，结果不伪造）\n'
elif [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （真库段未跑：缺 MMO_P2_1A4_GATEWAY / _TOKEN / _CHARACTER；本机段已跑完）\n'
elif [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  printf '       （真库段未跑：缺 MMO_GAMEDB / /srv/mmo/gamedb.env；本机段已跑完）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/harness"; harness_track_port 65000
  cp "$ROOT/config/sect.json" "$TMP/sect.orig.json"
  trap 'cp "$TMP/sect.orig.json" "$ROOT/config/sect.json" 2>/dev/null; harness_cleanup; rm -rf "$TMP"' EXIT
  CFG="--config $ROOT/config/social.json"

  MIG="$("$DOTNET" "$SOCIAL_DLL" --migrate --sql "$ROOT/db/social/social.sql" $CFG 2>&1)"
  printf '%s' "$MIG" | grep -q '^migrate=ok' && ok "社交 schema 主迁移（含升级列/流水表）" || bad "建表失败：$MIG"

  post(){ curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$2" 2>&1; }
  check(){ [ "$(expect_json "$(body_of "$1")" $2)" = "ok" ] && ok "$3" || bad "${3}：$1"; }
  nth(){ printf '%s\n' "$1" | tr ' ' '\n' | awk -v n="$2" 'NR==n{print}'; }
  new_char(){ curl -sS --noproxy '*' -m 10 -X POST "$GATEWAY/character?token=$TOKEN" -H 'Content-Type: application/json' -d "{\"name\":\"$1\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>/dev/null | python3 -c 'import json,sys;d=json.load(sys.stdin);print(d.get("character_id",""))' 2>/dev/null || true; }
  upgrade(){ post "$GATEWAY/sect/upgrade" "{\"character_id\":$1,\"sect_id\":$2}"; }
  cset(){ "$DOTNET" "$SOCIAL_DLL" --sect-construction-set "$1" "$2" $CFG 2>&1; }
  ustate(){ "$DOTNET" "$SOCIAL_DLL" --sect-upgrade-state "$1" $CFG 2>&1; }
  umembers(){ "$DOTNET" "$SOCIAL_DLL" --sect-upgrade-members "$1" $CFG 2>&1; }
  money(){ db_cli --shop-state "$CHARACTER" 2>&1 | grep -oE 'balances=[^ ]*' | head -1; }
  snap(){ printf '%s|%s' "$(ustate "$1" | grep -oE 'sect_upgrade_state=.*' | head -1)" "$(money)"; }
  start_social(){
    harness_kill_port 65000
    harness_start "$TMP/harness/social.log" "$DOTNET" "$SOCIAL_DLL" $CFG >/dev/null
    harness_wait_ready "$TMP/harness/social.log" "社交服" || return 1
    for _ in $(seq 1 60); do
      r="$(post "$GATEWAY/sect/upgrade" "{\"character_id\":$CHARACTER,\"sect_id\":999999999}")"
      [ "$(code_of "$r")" = "200" ] && return 0
      sleep 0.5
    done
    return 1
  }
  biz_reject(){ lab="$1"; why="$2"; r="$3"; \
    check "$r" "ok=false reason=$why new_level=0 member_cap=0 construction_after=0" "${lab}：回包"; \
    [ "$(code_of "$r")" = "200" ] && ok "${lab}：业务拒绝 HTTP 200" || bad "${lab} 码不对：$r"; \
    [ "$(snap "$SECT")" = "$BEFORE" ] && ok "${lab}：显式读库没动" || bad "${lab} 写了库"; }
  http_reject(){ lab="$1"; resp="$2"; want="$3"; \
    [ "$(code_of "$resp")" = "$want" ] && ok "${lab}：HTTP $want" || bad "${lab} 码不对：$resp"; \
    [ "$(snap "$SECT")" = "$BEFORE" ] && ok "${lab}：显式读库没动" || bad "${lab} 写了库"; }
  check_state(){ lab="$1"; lv="$2"; cap="$3"; \
    S="$(ustate "$SECT")"; \
    printf '%s' "$S" | grep -q "level=$lv " && printf '%s' "$S" | grep -q "member_cap=$cap " \
      && ok "$lab：--sect-upgrade-state level=$lv member_cap=$cap" || bad "$lab：state 不对 $S"; }

  FOUNDER="$CHARACTER"
  start_social && ok "社交服就绪（65000）" || bad "社交服没起来 / 子通道没恢复"
  grep -q '^upgrade_costs=' "$TMP/harness/social.log" && ok "真实启动日志打印同形 upgrade 行" || bad "启动日志缺同形 upgrade 行"

  C1="$(python3 -c 'import json;print(json.load(open("config/sect.json"))["upgrade"]["costs"][0]["cost"])')"
  C2="$(python3 -c 'import json;print(json.load(open("config/sect.json"))["upgrade"]["costs"][1]["cost"])')"
  C3="$(python3 -c 'import json;print(json.load(open("config/sect.json"))["upgrade"]["costs"][2]["cost"])')"
  C4="$(python3 -c 'import json;print(json.load(open("config/sect.json"))["upgrade"]["costs"][3]["cost"])')"
  BIG=$((C1 + 1000000))
  BUF=$((C1 + 7))

  IDS=""; for i in $(seq 1 31); do id="$(new_char "a4_$$_$i")"; [ -n "$id" ] && IDS="${IDS:+$IDS }$id"; done
  [ "$(printf '%s\n' "$IDS" | wc -w | tr -d ' ')" -ge 31 ] && ok "31 个测试角色就位" || bad "角色不足 31 个"
  NAME="a4s_$$_$(date +%s)"
  r="$(post "$GATEWAY/sect/create" "{\"character_id\":$FOUNDER,\"name\":\"$NAME\"}")"
  check "$r" "ok=true level=1 member_cap=10" "建 1 级宗门"
  SECT="$(json_field "$(body_of "$r")" sect_id)"

  # ① 1 级收满 10 人、第 11 人 sect_full（a1 上限不动）。
  for n in $(seq 1 10); do post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" $n),\"sect_id\":$SECT}" >/dev/null; done
  check "$(post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" 11),\"sect_id\":$SECT}")" "ok=false reason=sect_full" "1 级第 11 人 sect_full"
  MC0="$(ustate "$SECT" | grep -oE 'member_count=[0-9]+' | head -1)"

  # ① 建设不足不能升级、一个字不扣。
  cset "$SECT" 0 >/dev/null
  BEFORE="$(snap "$SECT")"
  biz_reject "建设不足" "not_enough_construction" "$(upgrade "$FOUNDER" "$SECT")"
  S="$(ustate "$SECT")"
  printf '%s' "$S" | grep -q 'level=1 ' && printf '%s' "$S" | grep -q 'construction=0 ' \
    && printf '%s' "$S" | grep -q 'member_cap=10 ' && ok "显式读库 level=1 construction=0 member_cap=10" || bad "不足时库状态不对：$S"

  # ① 给足建设 → 升级成功、只扣建设、不动角色货币、名册人数不变。
  cset "$SECT" "$BUF" >/dev/null
  r="$(upgrade "$FOUNDER" "$SECT")"
  check "$r" "ok=true old_level=1 new_level=2 member_cap=30" "给足建设升级成功 1→2 member_cap=30"
  b="$(body_of "$r")"
  BF="$(json_field "$b" construction_before)"; AF="$(json_field "$b" construction_after)"; UC="$(json_field "$b" upgrade_cost)"
  [ "$((BF - AF))" = "$C1" ] && [ "$UC" = "$C1" ] && [ "$BF" = "$BUF" ] && [ "$AF" = "7" ] \
    && ok "construction_after=$AF=before-cost、upgrade_cost=$C1 等于配置那档" || bad "建设扣减不对：$b"
  check_state "--sect-upgrade-state 同值" 2 30
  MC1="$(ustate "$SECT" | grep -oE 'member_count=[0-9]+' | head -1)"
  [ "$MC0" = "$MC1" ] && ok "升级后名册人数不变（$MC1）" || bad "名册人数被改：$MC0 -> $MC1"

  # ① 升级后第 31 人才能进（成员不踢、上限随等级变）。
  check "$(post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" 11),\"sect_id\":$SECT}")" "ok=true" "第 11 人（原 sect_full）升级后可进"
  for n in $(seq 12 30); do post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" $n),\"sect_id\":$SECT}" >/dev/null; done
  check "$(post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" 31),\"sect_id\":$SECT}")" "ok=false reason=sect_full" "升级后第 31 人 sect_full（member_cap=30）"
  UM="$(umembers "$SECT")"
  printf '%s' "$UM" | grep -q 'rows=30' && ok "--sect-upgrade-members 名册 30 人" || bad "名册行数不对：$UM"

  # ② 商城买不到建设、升级不动角色货币。
  BEFORE_M="$(money)"
  check "$(post "$GATEWAY/shop/buy" "{\"character_id\":$FOUNDER,\"item_id\":\"shop_construction_999\"}")" \
    "ok=false reason=not_in_list charged_yuanbao=0" "清单外建设类编号 not_in_list 且元宝不扣"
  [ "$(money)" = "$BEFORE_M" ] && ok "升级/清单外购买前后角色库金币/元宝/灵石余额一字未动（$BEFORE_M）" || bad "角色货币被动了"

  # ③ 逐档 2→3→4→5，5 级封顶，改配置重启生效。
  UCFG="$("$DOTNET" "$SOCIAL_DLL" --sect-upgrade-config $CFG 2>&1)"
  printf '%s' "$UCFG" | grep -q 'levels=1:10,2:30,3:60,4:100,5:200' && ok "--sect-upgrade-config 五档上限 10/30/60/100/200" || bad "config 不对：$UCFG"
  cset "$SECT" "$C2" >/dev/null; check "$(upgrade "$FOUNDER" "$SECT")" "ok=true old_level=2 new_level=3 member_cap=60" "2→3 member_cap=60"; check_state "逐档" 3 60
  cset "$SECT" "$C3" >/dev/null; check "$(upgrade "$FOUNDER" "$SECT")" "ok=true old_level=3 new_level=4 member_cap=100" "3→4 member_cap=100"; check_state "逐档" 4 100
  cset "$SECT" "$C4" >/dev/null; check "$(upgrade "$FOUNDER" "$SECT")" "ok=true old_level=4 new_level=5 member_cap=200" "4→5 member_cap=200"; check_state "逐档" 5 200
  BEFORE="$(snap "$SECT")"
  biz_reject "5 级再升" "at_max_level" "$(upgrade "$FOUNDER" "$SECT")"

  # ④ 并发两笔升级只成一次、建设只扣一份、只升一级、流水只一行。
  NAME="a4c_$$_$(date +%s)"
  r="$(post "$GATEWAY/sect/create" "{\"character_id\":$FOUNDER,\"name\":\"$NAME\"}")"
  SECT_C="$(json_field "$(body_of "$r")" sect_id)"
  cset "$SECT_C" "$C1" >/dev/null
  ( upgrade "$FOUNDER" "$SECT_C" > "$TMP/c1" ) & P1=$!
  ( upgrade "$FOUNDER" "$SECT_C" > "$TMP/c2" ) & P2=$!
  wait "$P1"; wait "$P2"
  wins=0; rej=0; WTXN=""
  for f in "$TMP/c1" "$TMP/c2"; do
    cb="$(body_of "$(cat "$f")")"
    [ "$(json_field "$cb" ok)" = "true" ] && wins=$((wins+1)) && WTXN="$(json_field "$cb" txn_id)"
    [ "$(json_field "$cb" ok)" = "false" ] && rej=$((rej+1))
  done
  [ "$wins" = "1" ] && [ "$rej" = "1" ] && ok "并发两笔升级只成一次（wins=1 rej=1）" || bad "并发不对：wins=$wins rej=$rej"
  SC="$(ustate "$SECT_C")"
  printf '%s' "$SC" | grep -q 'level=2 ' && printf '%s' "$SC" | grep -q 'construction=0 ' \
    && printf '%s' "$SC" | grep -q 'upgrade_txns=1' && ok "并发只升一级、建设只扣一份、流水只一行" || bad "并发库状态不对：$SC"
  printf '%s' "$SC" | grep -q "last_upgrade_txn=$WTXN" && ok "同一笔 txn_id 落 last_upgrade_txn、主键挡重放" || bad "幂等事务号不对：$SC"
  check "$(upgrade "$FOUNDER" "$SECT_C")" "ok=false reason=not_enough_construction" "相同语义重放：建设已扣尽、不重复升一级"

  # ③ 改配置重启生效：改大某档 cost → 原本够的变不足；改回 → 又能升。
  NAME="a4r_$$_$(date +%s)"
  r="$(post "$GATEWAY/sect/create" "{\"character_id\":$FOUNDER,\"name\":\"$NAME\"}")"
  SECT_R="$(json_field "$(body_of "$r")" sect_id)"
  cset "$SECT_R" "$C1" >/dev/null
  S0="$(ustate "$SECT_R")"
  printf '%s' "$S0" | grep -q "construction=$C1 " && printf '%s' "$S0" | grep -q "next_cost=$C1 " \
    && ok "原本够：建设=$C1 等于下一档消耗=$C1" || bad "初始状态不对：$S0"
  python3 -c 'import json,sys;d=json.load(open("config/sect.json",encoding="utf-8"));d["upgrade"]["costs"][0]["cost"]=int(sys.argv[1]);json.dump(d,open("config/sect.json","w",encoding="utf-8"),ensure_ascii=False,indent=2)' "$BIG"
  start_social && ok "改大 cost 后重启社交服就绪（轮询只读回 200）" || bad "改大重启没起来"
  check "$(upgrade "$FOUNDER" "$SECT_R")" "ok=false reason=not_enough_construction" "改大 cost 后原本够的变不足被拒"
  cp "$TMP/sect.orig.json" "$ROOT/config/sect.json"
  start_social && ok "改回 cost 后重启社交服就绪（轮询只读回 200）" || bad "改回重启没起来"
  check "$(upgrade "$FOUNDER" "$SECT_R")" "ok=true old_level=1 new_level=2 member_cap=30" "改回 cost 后又能升"

  # ④ 七条拒绝路径里剩余五条：每条前后显式读库没动。
  BEFORE="$(snap "$SECT")"
  biz_reject "非掌门发起" "not_leader" "$(upgrade "$(nth "$IDS" 1)" "$SECT")"
  biz_reject "宗门不存在" "sect_not_found" "$(upgrade "$FOUNDER" 999999999)"
  http_reject "无令牌" "$(curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$GATEWAY/sect/upgrade" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$FOUNDER,\"sect_id\":$SECT}" 2>&1)" 401
  http_reject "缺字段" "$(post "$GATEWAY/sect/upgrade" "{\"character_id\":$FOUNDER}")" 400
  http_reject "别人的角色" "$(post "$GATEWAY/sect/upgrade" "{\"character_id\":999999999,\"sect_id\":$SECT}")" 404
  [ "$(money)" = "$BEFORE_M" ] && ok "升级全程角色库货币余额一字未动（$BEFORE_M）" || bad "角色货币被动了"
  printf '       （社交服只写 社交 schema 的 sect*：由 UpgradeAsync 的 SQL 静态断言 + 显式读库覆盖）\n'

  cp "$TMP/sect.orig.json" "$ROOT/config/sect.json"
  harness_cleanup
fi

finish "P2-1a4（server）"
