#!/usr/bin/env bash
# tools/check/check-p2-1a1.sh —— P2-1a1 宗门五级人数上限（方案 11.1 / 技术架构 49 行 /
# 开发架构第 14 节第 50 行）。完成标准：1 级宗门第 11 个人被拒绝。
#
#   bash tools/check/check-p2-1a1.sh        编译 + 静态 + 只读入口 + 坏表（不连库不连服）
#   MMO_P2_1A1_HAS_DB=1 MMO_P2_1A1_GATEWAY=… MMO_P2_1A1_TOKEN=… \
#     MMO_P2_1A1_CHARACTER=… bash tools/check/check-p2-1a1.sh   再加真服/真库段
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
HAS_DB="${MMO_P2_1A1_HAS_DB:-0}"
GATEWAY="${MMO_P2_1A1_GATEWAY:-}"
TOKEN="${MMO_P2_1A1_TOKEN:-}"
CHARACTER="${MMO_P2_1A1_CHARACTER:-0}"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
if [ "${MMO_P2_1A1_SKIP_BUILD:-0}" = "1" ]; then
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
pairs = [(int(a), int(b)) for a, b in re.findall(r"<tr><td>(\d+)</td><td>(\d+)</td>", seg)]
levels = [(l["level"], l["member_cap"]) for l in cfg["levels"]]
report("five_levels_vs_plan", pairs == [(1,10),(2,30),(3,60),(4,100),(5,200)] and levels == pairs,
       "config/sect.json 与方案 11.1 表逐档一致")
report("note_no_dungeon", "名册上限" in cfg["note"] and "不是进本人数" in cfg["note"],
       "note 写明名册上限不是进本人数")
report("create_gate", cfg["create"]["founder_level_gate"] == 45
       and cfg["create"]["founder_gold_cost_pending"] is True, "45 门槛 + 金币待定")

social_src = readdir("src/Social")
gateway_src = read("src/Gateway/SectApi.cs") + read("src/Gateway/GatewayConfig.cs") + read("src/Gateway/GatewayProcess.cs")
proto = read("proto/mmo/social.proto")
sql = read("db/social/sect.sql") + read("db/social/social.sql")

# 按「配置键 + 值」找第二套数字，别按裸子串（字段号、端口、版本号都不是玩法数值）。
hard = re.compile(r'"member_cap"\s*:\s*\d|new Level\(\s*\d|member_cap\s*=\s*(?:10|30|60|100|200)\b')
report("no_second_copy", not hard.search(social_src + gateway_src + proto + sql),
       "代码/proto/SQL 不含第二套上限数字")

report("sql_two_tables", sql.lower().count('create table if not exists "社交".sect') >= 2
       and 'create table if not exists "社交".sect_member' in sql.lower(),
       "两表幂等 DDL 追加进主迁移")
report("sql_unique", re.search(r'character_id\s+bigint\s+not null\s+unique', sql, re.I) is not None,
       "character_id 唯一约束挡重复入册")
report("sql_no_role_schema", '"角色"' not in sql, "社交 DDL 不碰角色 schema")

report("proto_rpcs", all(x in proto for x in ("rpc CreateSect", "rpc JoinSect", "rpc DescribeSect"))
       and all(x in proto for x in ("string account", "uint64 character_id", "string txn_id"))
       and all(x in proto for x in ("bool ok", "string reason", "uint64 sect_id",
                                    "int32 level", "int32 member_cap", "int32 member_count")),
       "三条 rpc + 请求/回包字段")

tbl = read("src/Social/SectTable.cs")
report("sect_table_hard", all(x in tbl for x in
       ("WrittenSchema", "version 必须是正整数", "levels 不能为空", "member_cap 必须是正整数",
        "严格递增", "level 必须是", "founder_level_gate 必须是正整数")),
       "七类坏配置在 Load 里硬校验")

repo = read("src/Social/SectRepository.cs")
report("repo_one_txn", all(x in repo for x in
       ("BeginTransactionAsync", "for update", "count(*)", "insert into", "sect_member",
        "RollbackAsync", "CommitAsync")), "一笔事务：锁宗门行→count→insert")
report("repo_social_only", '"角色"' not in repo and '"社交".sect' in repo, "只写 社交.sect*")
report("repo_reasons", all(x in repo for x in
       ("sect_full", "already_member", "sect_not_found", "name_taken", "duplicate")),
       "拒绝 reason 齐")

report("founder_grant", all(x in read("src/Social/SectFounderGrant.cs") for x in
       ("ApplyGrantAsync", "CurrencyDelta", "TxnId", "FounderGoldKind", "grant_txn")),
       "创建金币经角色服 grant_txn 一次")
report("grpc_service", all(x in read("src/Social/SocialGrpcService.cs") for x in
       ("CreateSect", "JoinSect", "DescribeSect", "request_missing")), "三个 gRPC 入口")
report("social_process", "MapGrpcService<SocialGrpcService>" in read("src/Social/SocialProcess.cs")
       and "sect_table_version=" in read("src/Social/SocialProcess.cs"), "装配与启动日志")

program = read("src/Social/Program.cs")
report("readonly_entries", all(x in program for x in
       ("--sect-list", "--sect-config", "--sect-state", "--sect-members",
        "--migrate", "--sql", "--sect-set-level")), "四个只读入口 + 迁移 + 核对用改级")
report("same_load", program.count("SocialLoader.LoadAll(configPath, sectTablePath)") >= 3,
       "只读入口与真实启动共用同一 Load + 同一组参数")

report("gateway_routes", '"/sect/create"' in gateway_src and '"/sect/join"' in gateway_src
       and "OwnsAsync" in gateway_src and "Guid.NewGuid" in gateway_src
       and "SectApi.Map" in gateway_src, "两条 POST + 认令牌 + 归属复核 + 服务端生成 txn_id")
report("gateway_default", "SocialUpstream { get; init; } = DefaultSocialUpstream" in gateway_src
       and "social_upstream" in read("config/gateway.json"), "social_upstream 可缺省、只进配置")
report("no_dungeon_import", not re.search(r"dungeons|副本人数|party_size|max_players", social_src, re.I),
       "社交服不读副本人数配置、不向战斗服下发人数")

srv = read("src/Shared/Generated/Social.cs")
cli = read("client/MMOGame/Assets/Proto/Generated/Social.cs")
report("generated_same", srv != "" and srv == cli, "两端 Social.cs 逐字相同")
m = re.search(r"^guid:\s*([0-9a-fA-F]+)\s*$",
              read("client/MMOGame/Assets/Proto/Generated/Social.cs.meta"), re.M)
report("meta_guid_32", m is not None and len(m.group(1)) == 32, "客户端 Social.cs.meta GUID 32 位")
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
CONF="$("$DOTNET" "$SOCIAL_DLL" --sect-config 2>&1)"
if printf '%s' "$CONF" | grep -q '^sect_config_state=ok' \
   && printf '%s' "$CONF" | grep -q 'levels=1:10,2:30,3:60,4:100,5:200'; then
  ok "--sect-config 逐档打印：$(printf '%s' "$CONF" | head -1 | cut -c1-96)"
else
  bad "--sect-config 不对：$CONF"
fi

echo "== 4. 坏配置七类起不来 =="
python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
base = json.load(open("config/sect.json", encoding="utf-8"))
def case(name, mutate):
    d = json.loads(json.dumps(base))
    mutate(d)
    with open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False)
case("no_levels", lambda d: d.pop("levels", None))
case("four_levels", lambda d: d["levels"].pop())
case("dup_level", lambda d: d["levels"][1].update(level=1))
case("zero_cap", lambda d: d["levels"][0].update(member_cap=0))
case("not_increasing", lambda d: d["levels"][2].update(member_cap=20))
case("bad_version", lambda d: d.update(version=0))
case("neg_gate", lambda d: d["create"].update(founder_level_gate=-1))
PY
for c in no_levels four_levels dup_level zero_cap not_increasing bad_version neg_gate; do
  o="$("$DOTNET" "$SOCIAL_DLL" --sect-config --sect-table "$TMP/bad-$c.json" 2>&1)"
  assert_bad_config "$o" "坏配置 $c 起不来（$(printf '%s' "$o" | head -1 | cut -c1-48)…）"
done

echo "== 5. 真服 / 真库段 =="
if [ "$HAS_DB" != "1" ]; then
  printf '       （真库段未跑：MMO_P2_1A1_HAS_DB 未置 1；本机编译/静态/只读/坏表段已跑完，结果不伪造）\n'
elif [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （真库段未跑：缺 MMO_P2_1A1_GATEWAY / _TOKEN / _CHARACTER；本机段已跑完）\n'
elif [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  printf '       （真库段未跑：缺 MMO_GAMEDB / /srv/mmo/gamedb.env；本机段已跑完）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/harness"
  harness_track_port 65000

  cp "$ROOT/config/sect.json" "$TMP/sect.orig.json"
  trap 'cp "$TMP/sect.orig.json" "$ROOT/config/sect.json" 2>/dev/null; harness_cleanup; rm -rf "$TMP"' EXIT

  MIG="$("$DOTNET" "$SOCIAL_DLL" --migrate --sql "$ROOT/db/social/social.sql" \
        --config "$ROOT/config/social.json" 2>&1)"
  printf '%s' "$MIG" | grep -q '^migrate=ok' && ok "社交 schema 主迁移建表" || bad "建表失败：$MIG"

  post() {
    curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" \
      -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$2" 2>&1
  }
  check() {
    if [ "$(expect_json "$(body_of "$1")" $2)" = "ok" ]; then ok "$3"; else bad "$3：$1"; fi
  }
  nth() { printf '%s\n' "$1" | tr ' ' '\n' | awk -v n="$2" 'NR==n{print}'; }
  new_char() {
    curl -sS --noproxy '*' -m 10 -X POST "$GATEWAY/character?token=$TOKEN" \
      -H 'Content-Type: application/json' \
      -d "{\"name\":\"$1\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>&1 \
      | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")'
  }
  start_social() {
    harness_kill_port 65000
    harness_start "$TMP/harness/social.log" "$DOTNET" "$SOCIAL_DLL" \
      --config "$ROOT/config/social.json" >/dev/null
    harness_wait_ready "$TMP/harness/social.log" "社交服" || return 1
    # 网关旧 gRPC 子通道：轮询一个不写库的只读请求回 200 才继续。
    for _ in $(seq 1 60); do
      r="$(post "$GATEWAY/sect/join" "{\"character_id\":$CHARACTER,\"sect_id\":999999999}")"
      [ "$(code_of "$r")" = "200" ] && return 0
      sleep 0.5
    done
    return 1
  }
  set_cap() {
    python3 - "$1" "$TMP/sect.orig.json" "$ROOT/config/sect.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[2], encoding="utf-8"))
d["levels"][0]["member_cap"] = int(sys.argv[1])
json.dump(d, open(sys.argv[3], "w", encoding="utf-8"), ensure_ascii=False)
PY
  }
  read_rows() {
    "$DOTNET" "$SOCIAL_DLL" --sect-members "$1" --config "$ROOT/config/social.json" 2>&1 \
      | grep -oE 'rows=[0-9]+' | head -1
  }
  start_social && ok "社交服就绪（65000）" || bad "社交服没起来 / 子通道没恢复"

  # 12 个角色：给定 CHARACTER + 自建 11 个，凑够 10 次成功 + 第 11 人 + 第 12 人。
  IDS="$CHARACTER"
  for i in $(seq 1 11); do
    id="$(new_char "p2a1_$$_$i")"
    [ -n "$id" ] && IDS="$IDS $id"
  done
  [ "$(printf '%s\n' "$IDS" | wc -w | tr -d ' ')" -ge 12 ] \
    && ok "12 个测试角色就位" || bad "角色不足 12 个（$IDS）"

  NAME="p2a1_sect_$$_$(date +%s)"
  r="$(post "$GATEWAY/sect/create" "{\"character_id\":$CHARACTER,\"name\":\"$NAME\"}")"
  check "$r" "ok=true level=1 member_cap=10 member_count=0" "建 1 级宗门（member_cap=10）"
  SECT="$(json_field "$(body_of "$r")" sect_id)"

  # ① 顺序收 9 人，member_count=1..9。
  for n in $(seq 1 9); do
    id="$(nth "$IDS" "$n")"
    check "$(post "$GATEWAY/sect/join" "{\"character_id\":$id,\"sect_id\":$SECT}")" \
      "ok=true member_count=$n" "连收第 $n 人：member_count=$n"
  done

  # 并发：两个不同角色同时抢第 10 席，只有一笔成功，库里仍 rows=10。
  a="$(nth "$IDS" 10)"; b="$(nth "$IDS" 11)"
  ( post "$GATEWAY/sect/join" "{\"character_id\":$a,\"sect_id\":$SECT}" > "$TMP/c1" ) & P1=$!
  ( post "$GATEWAY/sect/join" "{\"character_id\":$b,\"sect_id\":$SECT}" > "$TMP/c2" ) & P2=$!
  # 只等这两个 curl：无参 wait 会把 harness 起的社交服（永不退出）一起等住。
  wait "$P1"; wait "$P2"
  wins=0; full=0
  for f in "$TMP/c1" "$TMP/c2"; do
    body="$(body_of "$(cat "$f")")"
    [ "$(json_field "$body" ok)" = "true" ] && wins=$((wins+1))
    [ "$(json_field "$body" reason)" = "sect_full" ] && full=$((full+1))
  done
  [ "$wins" = "1" ] && [ "$full" = "1" ] \
    && ok "并发抢第 10 席：只有一笔成功、另一笔 sect_full" \
    || bad "并发不对：wins=$wins full=$full（$(cat "$TMP/c1" "$TMP/c2")）"

  # 第 11 人：HTTP 200 + ok=false reason=sect_full、未生效数值 0。
  r="$(post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" 12),\"sect_id\":$SECT}")"
  check "$r" "ok=false reason=sect_full member_count=0" "第 11 人被拒：sect_full、member_count=0"
  [ "$(code_of "$r")" = "200" ] && ok "业务拒绝 HTTP 200" || bad "拒绝码不对：$(code_of "$r")"

  # --sect-members 与独立读库（--sect-state / --sect-list）都回 rows=10。
  [ "$(read_rows "$SECT")" = "rows=10" ] && ok "--sect-members rows=10" || bad "名册不是 10 行：$(read_rows "$SECT")"
  ST="$("$DOTNET" "$SOCIAL_DLL" --sect-state "$SECT" --config "$ROOT/config/social.json" 2>&1)"
  printf '%s' "$ST" | grep -q 'member_count=10' && ok "--sect-state 独立读回 member_count=10" || bad "state 不对：$ST"

  # ④ 六条拒绝路径：每条前后显式读库，名册一个字没写。
  # already_member 要在未满足员的宗门上验：满员判定先于唯一约束，满员宗门恒回 sect_full。
  before="$(read_rows "$SECT")"
  cr="$(post "$GATEWAY/sect/create" "{\"character_id\":$CHARACTER,\"name\":\"${NAME}_empty\"}")"
  EMPTY="$(json_field "$(body_of "$cr")" sect_id)"
  for spec in "sect_full:$(nth "$IDS" 12):$SECT" \
              "already_member:$(nth "$IDS" 1):$EMPTY" \
              "sect_not_found:$(nth "$IDS" 12):999999999"; do
    reason="${spec%%:*}"; rest="${spec#*:}"; cid="${rest%%:*}"; sid="${rest##*:}"
    r="$(post "$GATEWAY/sect/join" "{\"character_id\":$cid,\"sect_id\":$sid}")"
    check "$r" "ok=false reason=$reason" "拒绝路径 $reason：回包"
    [ "$(read_rows "$SECT")" = "$before" ] \
      && ok "拒绝路径 $reason：显式读库 rows 没动" || bad "拒绝路径 $reason 写了库"
  done
  [ -n "$EMPTY" ] && [ "$(read_rows "$EMPTY")" = "rows=0" ] \
    && ok "already_member 目标宗门显式读库 rows=0" || bad "空宗门被写脏（EMPTY=$EMPTY）"
  # 无令牌 401 / 别人的角色 404 / 缺字段 400：网关都在写库之前挡下。
  r="$(curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$GATEWAY/sect/join" \
       -H 'Content-Type: application/json' \
       -d "{\"character_id\":$CHARACTER,\"sect_id\":$SECT}" 2>&1)"
  [ "$(code_of "$r")" = "401" ] && ok "无令牌：HTTP 401" || bad "无令牌码不对：$r"
  [ "$(read_rows "$SECT")" = "$before" ] && ok "无令牌：显式读库 rows 没动" || bad "无令牌写了库"
  r="$(post "$GATEWAY/sect/join" "{\"character_id\":999999999,\"sect_id\":$SECT}")"
  [ "$(code_of "$r")" = "404" ] && ok "别人的角色：HTTP 404" || bad "别人的角色码不对：$r"
  [ "$(read_rows "$SECT")" = "$before" ] && ok "别人的角色：显式读库 rows 没动" || bad "别人的角色写了库"
  r="$(post "$GATEWAY/sect/join" "{\"character_id\":$CHARACTER}")"
  [ "$(code_of "$r")" = "400" ] && ok "缺字段：HTTP 400" || bad "缺字段码不对：$r"
  [ "$(read_rows "$SECT")" = "$before" ] && ok "缺字段：显式读库 rows 没动" || bad "缺字段写了库"
  r="$(post "$GATEWAY/sect/create" "{\"character_id\":0,\"name\":\"x\"}")"
  [ "$(code_of "$r")" = "400" ] && ok "缺字段（建宗门）：HTTP 400" || bad "建宗门缺字段码不对：$r"

  # ② 改配置重启生效：1 级 cap 11 → 第 11 人能进、第 12 人 sect_full；改回 10 → 又是 10。
  CONF2="$("$DOTNET" "$SOCIAL_DLL" --sect-config 2>&1)"
  printf '%s' "$CONF2" | grep -q 'levels=1:10,2:30,3:60,4:100,5:200' \
    && ok "--sect-config 逐档打印 30/60/100/200" || bad "五档打印不对：$CONF2"
  set_cap 11
  start_social && ok "改 cap=11 后重启、轮询只读回 200" || bad "改 cap=11 重启失败"
  check "$(post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" 12),\"sect_id\":$SECT}")" \
    "ok=true member_count=11" "cap=11：第 11 人能进"
  set_cap 10
  start_social && ok "改回 cap=10 后重启、轮询只读回 200" || bad "改回 cap=10 重启失败"
  check "$(post "$GATEWAY/sect/join" "{\"character_id\":$(nth "$IDS" 12),\"sect_id\":$SECT}")" \
    "ok=false reason=sect_full" "改回 10：又是满员拒收"

  # ② 仅核对用改级：level=2 的上限从 --sect-state 读回，不在本卡做升级逻辑。
  "$DOTNET" "$SOCIAL_DLL" --sect-set-level "$SECT" 2 --config "$ROOT/config/social.json" >/dev/null 2>&1
  ST2="$("$DOTNET" "$SOCIAL_DLL" --sect-state "$SECT" --config "$ROOT/config/social.json" 2>&1)"
  printf '%s' "$ST2" | grep -q 'level=2 member_cap=30' \
    && ok "仅核对用改级：level=2 member_cap=30" || bad "改级读回不对：$ST2"

  # ③ 名册上限不改本人数：闭包回归交给 check-p1-2a / check-p1-2c，这里只留静态断言。
  cp "$TMP/sect.orig.json" "$ROOT/config/sect.json"
  harness_cleanup
fi

finish "P2-1a1（server）"
