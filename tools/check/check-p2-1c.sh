#!/usr/bin/env bash
# P2-1c 同门经验（方案 11.1；开发架构 4.4、11、14 节 55 行；P1-2d 口径 v1.43）。
#   bash tools/check/check-p2-1c.sh                编译+静态+只读+坏表（不连库不连服）
#   MMO_P2_1C_HAS_DB=1 [MMO_P2_1C_GATEWAY=… _TOKEN=… _CHARACTER=…] bash tools/check/check-p2-1c.sh  加真服真库
# 本卡：同宗门两人比不同门两人经验多一截（更窄），伤害不变；经验结算只加经验、不碰别的账。
# 战斗服只改了 GrpcExpSettlement.SettleParty 的 memberIds 透传（一行），由编译+静态断言覆盖；
# 经验结算走「网关→角色服」同一条 gRPC，真库段用 /exp/settle 驱动即同一路径。
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
SD="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
HAS_DB="${MMO_P2_1C_HAS_DB:-0}"
GATEWAY="${MMO_P2_1C_GATEWAY:-http://127.0.0.1:61000}"
TOKEN="${MMO_P2_1C_TOKEN:-}"; CHARACTER="${MMO_P2_1C_CHARACTER:-0}"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_P2_1C_SKIP_BUILD:-0}" = "1" ]; then printf '  ..   跳过编译\n'; else
  BUILD_OK=1
  for proj in src/Character/Character.csproj src/Battle/Battle.csproj src/Gateway/Gateway.csproj src/Social/Social.csproj; do
    n="$(basename "$proj" .csproj)"
    "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1 || {
      BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail; }
  done
  [ "$BUILD_OK" = "1" ] && ok "角色服/战斗服/网关/社交服编译通过" || true
fi

echo "== 2. 静态断言 =="
python3 - "$TMP/static.txt" <<'PY'
import json, os, re, sys
o = open(sys.argv[1], "w", encoding="utf-8")
def R(k, c, i=""): o.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")
def rd(p): return open(p, encoding="utf-8").read() if os.path.exists(p) else ""
cfg = json.loads(rd("config/exp.json"))
R("schema_version", cfg.get("schema") == "exp-table/1" and cfg.get("version") == 1, "exp-table/1 version=1")
R("base_unchanged", cfg.get("base_exp") == 120 and cfg.get("party_bonus_pct") == 10 and cfg.get("level_cap") == 100
  and cfg.get("party_min_size") == 2 and cfg.get("exp_per_level") == 1000, "base/party/level_cap 等既有数值一字未改")
ss = cfg.get("same_sect_bonus_pct")
R("same_sect_key", isinstance(ss, int) and 0 <= ss <= 1000, "same_sect_bonus_pct 存在且 0..1000")
R("narrower", isinstance(ss, int) and 0 < ss < cfg.get("party_bonus_pct", 0), "同宗加成幅度比组队加成更窄")
note = cfg.get("note", "")
R("note_updated", "同宗门加成已接入" in note, "note 写明同宗门加成已接入")
R("note_no_stale", "不做同宗门加成" not in note, "note 不再写「不做同宗门加成」")
tbl = rd("src/Character/ExpTable.cs")
R("table_field", "SameSectBonusPct" in tbl and "same_sect_bonus_pct" in tbl, "ExpTable 读 same_sect_bonus_pct")
R("table_calc", "SameSectBonus(int partySize, bool allSameSect)" in tbl and "BaseExp * SameSectBonusPct / 100" in tbl,
  "SameSectBonus 只在整队同门且 >=2 人时 BaseExp*pct/100")
R("table_validate", "same_sect_bonus_pct 必须在 0 到 1000" in tbl, "same_sect_bonus_pct 越界硬校验（缺/越界起不来）")
R("table_independent", "BaseExp + PartyBonus(partySize) + SameSectBonus(partySize, allSameSect)" in tbl,
  "同门加成独立叠在组队加成之外")
repo = rd("src/Character/ExpSettlementRepository.cs")
R("settle_sig", "bool allSameSect" in repo, "SettleAsync 接收 allSameSect")
R("settle_calc", "table.SameSectBonus(partySize, allSameSect)" in repo
  and "rawGain = baseExp + partyBonus + sameSectBonus" in repo, "结算把同门加成并入 rawGain")
R("settle_column", "same_sect_bonus" in repo and "insert into \"角色\".exp_settlement" in repo.replace('\\"','"'),
  "exp_settlement 落独立列 same_sect_bonus")
R("settle_single_commit", (lambda b: b.count("BeginTransactionAsync") == 1 and b.count("await transaction.CommitAsync") == 1)(
  repo[repo.find("SettleAsync("): (repo.find("\n    public ", repo.find("SettleAsync(") + 1) if repo.find("\n    public ", repo.find("SettleAsync(") + 1) != -1 else len(repo))]),
  "SettleAsync 一笔事务、一次 CommitAsync、无中间提交")
R("settle_only_exp", "set level = $2, exp = $3" in repo, "结算只写 level/exp，不碰伤害/掉落/疲劳/次数")
R("settle_no_damage", not re.search(r"(damage|attack|hp|health)\s*=\s*\$", repo, re.I), "结算 SQL 不含伤害/血量写入")
grpc = rd("src/Character/CharacterGrpcService.cs")
R("same_sect_judge", "ComputeAllSameSectAsync" in grpc and "DescribeCharacterSectAsync" in grpc, "角色服逐人问社交服判同门")
R("judge_before_settle", grpc.find("ComputeAllSameSectAsync(request.MemberIds") < grpc.find("expSettlements.SettleAsync"),
  "判同门（跨服）在结算之前、且在 DB 行锁事务之外")
R("judge_strict", "memberIds.Count < 2" in grpc and "commonSect" in grpc, "人数<2 或宗门不一致判 false，不臆造")
social = rd("src/Social/SectRepository.cs")
R("sect_char_lookup", "sect_member sm where sm.character_id" in social and "master_character_id = $1" in social
  and re.search(r"union", social, re.I) is not None, "查宗门同时覆盖名册与 master_character_id（创建者不在名册）")
proto_c = rd("proto/mmo/character.proto"); proto_s = rd("proto/mmo/social.proto")
R("proto_member_ids", re.search(r"repeated\s+uint64\s+member_ids\s*=\s*6\s*;", proto_c) is not None, "character.proto 加 member_ids=6")
R("proto_same_sect", re.search(r"int64\s+same_sect_bonus\s*=\s*14\s*;", proto_c) is not None, "character.proto 加 same_sect_bonus=14")
R("proto_social_rpc", "rpc DescribeCharacterSect(" in proto_s and "message DescribeCharacterSectRequest" in proto_s,
  "social.proto 加 DescribeCharacterSect 只读 RPC")
battle = rd("src/Battle/ExpSettlement.cs"); gw = rd("src/Gateway/CharacterApi.cs")
R("battle_passthrough", "MemberIds = { memberIds }" in battle, "战斗服 SettleParty 透传 memberIds")
R("gw_passthrough", "MemberIds = { body.MemberIds" in gw, "网关 /exp/settle 透传 member_ids")
R("readonly_config", 'case "--exp-config"' in rd("src/Character/Program.cs"), "新增 --exp-config 只读入口")
R("startup_same_load", "ExpTable.Load(config.ExpTable)" in rd("src/Character/CharacterProcess.cs"), "启动也走 ExpTable.Load 同一份")
newsrc = rd("src/Character/Program.cs") + grpc + repo + social + battle + gw
R("no_bareword", not any(w in newsrc for w in ["Player" + "Prefs", "Resources" + "." + "Load"]), "新增源码/注释不含 U1 裸词")
o.close()
PY
while read -r tag key rest; do
  case "$tag" in PASS) ok "$key" ;; FAIL) bad "$key${rest:+ $rest}" ;; esac
done < "$TMP/static.txt"
grep -qE '^(PASS|FAIL) ' "$TMP/static.txt" || bad "静态断言没跑起来"
CF="client/MMOGame/Assets/Proto/Generated/Social.cs"
if [ -f "$CF" ]; then
  diff -q src/Shared/Generated/Social.cs "$CF" >/dev/null 2>&1 \
    && ok "两端 Social.cs 逐字相同" || bad "两端 Social.cs 不一致"
  [ "$(grep -E '^guid:' "$CF.meta" | awk '{print length($2)}')" = "32" ] \
    && ok "客户端 Social.cs.meta GUID 32 位" || bad "Social.cs.meta GUID 不是 32 位"
else
  printf '       （跳过两端生成物一致性：本机无 client/，服务端段不校对客户端；Mac 全量段会校）\n'
fi

echo "== 3. 只读入口（与启动同形）=="
EC="$("$DOTNET" "$CD" --exp-config 2>&1)"
printf '%s' "$EC" | grep -q '^exp_config_state=ok' \
  && printf '%s' "$EC" | grep -q 'same_sect_bonus_pct=5' \
  && printf '%s' "$EC" | grep -q 'gain1=120' \
  && printf '%s' "$EC" | grep -q 'gain2=132' \
  && printf '%s' "$EC" | grep -q 'gain2_same=138' \
  && ok "--exp-config 三档口径：单人 120 / 混合门 132 / 同门 138" || bad "--exp-config 不对：$EC"
ACC="$(printf '%s' "$EC" | grep -oE 'gain2=[0-9]+' | head -1 | cut -d= -f2)"
ASS="$(printf '%s' "$EC" | grep -oE 'gain2_same=[0-9]+' | head -1 | cut -d= -f2)"
[ -n "$ACC" ] && [ -n "$ASS" ] && [ "$ASS" -gt "$ACC" ] && ok "同门 138 > 混合门 132（只读事实值）" || bad "同门未大于混合门：$EC"

echo "== 4. 坏配置起不来（真实 ExpTable.Load）=="
BAD_RC=0
for c in missing negative oversize; do
  python3 - "$ROOT/config/exp.json" "$TMP/bad-$c.json" "$c" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8")); c = sys.argv[3]
if c == "missing": d.pop("same_sect_bonus_pct", None)
elif c == "negative": d["same_sect_bonus_pct"] = -1
else: d["same_sect_bonus_pct"] = 1001
json.dump(d, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
  out="$("$DOTNET" "$CD" --exp-config-path "$TMP/bad-$c.json" 2>&1)"; rc=$?
  assert_bad_config "$out" "坏配置 ${c}：$(printf '%s' "$out" | head -1 | cut -c1-58)"
  [ "$rc" -ne 0 ] || BAD_RC=1
done
[ "$BAD_RC" = "0" ] && ok "3 类坏 same_sect_bonus_pct 都以非 0 退出" || bad "有坏配置以 0 退出"

echo "== 5. 真服 / 真库段 =="
if [ "$HAS_DB" != "1" ]; then
  printf '       （真库段未跑：MMO_P2_1C_HAS_DB 未置 1；本机段已跑完，不伪造）\n'
elif [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  printf '       （真库段未跑：缺 MMO_GAMEDB / /srv/mmo/gamedb.env；本机段已跑完）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/harness"
  for p in 65000 62000 61000; do harness_track_port "$p"; done
  CC="--config $ROOT/config/character.json"; CS="--config $ROOT/config/social.json"; CG="--config $ROOT/config/gateway.json"
  start_all() {
    local p f
    for p in 65000 62000 61000; do harness_kill_port "$p"; done
    harness_start "$TMP/harness/social.log" "$DOTNET" "$SD" $CS >/dev/null
    harness_start "$TMP/harness/character.log" "$DOTNET" "$CD" $CC >/dev/null
    harness_start "$TMP/harness/gateway.log" "$DOTNET" "$GD" $CG >/dev/null
    harness_wait_ready "$TMP/harness/social.log" "社交服" || return 1
    harness_wait_ready "$TMP/harness/character.log" "角色服" || return 1
    harness_wait_ready "$TMP/harness/gateway.log" "网关" || return 1
    harness_wait_http "$GATEWAY/health" 200
  }
  post() { curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' -d "$2" 2>&1; }
  settle() { post "$GATEWAY/exp/settle" "$1"; }
  expstate() { "$DOTNET" "$CD" --exp-state "$CH" $CC 2>&1 | grep -m1 '^exp_state='; }
  mem() { "$DOTNET" "$SD" --sect-members "$SECT" $CS 2>&1 | grep -m1 '^sect_members_state='; }
  lead() { "$DOTNET" "$SD" --sect-leadership-state "$SECT" $CS 2>&1 | grep -m1 '^sect_leadership_state='; }
  money() { "$DOTNET" "$CD" --shop-state "$CH" $CC 2>&1 | grep -oE 'balances=[^ ]*' | head -1; }
  resv() { "$DOTNET" "$CD" --reserves-of "$CH" $CC 2>&1 | grep -m1 '^reserves='; }
  mkchar() { local nm="$1" r; for _ in $(seq 1 20); do r="$(curl -sS --noproxy '*' -m 10 -X POST "$GATEWAY/character?token=$TOK" -H 'Content-Type: application/json' -d "{\"name\":\"$nm\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>/dev/null)"; local id; id="$(printf '%s' "$r" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")')"; [ -n "$id" ] && { printf '%s' "$id"; return 0; }; sleep 0.5; done; return 1; }
  MIG=1
  "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/account.sql" $CG >"$TMP/m1" 2>&1 || MIG=0
  : > "$TMP/m2"
  for sql in character.sql dungeon_entry.sql inventory.sql currency.sql transaction.sql exp_settlement.sql; do
    "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/$sql" $CC >>"$TMP/m2" 2>&1 || true; done
  "$DOTNET" "$SD" --migrate --sql "$ROOT/db/social/social.sql" $CS >"$TMP/m3" 2>&1 || MIG=0
  [ "$MIG" = "1" ] && grep -q '^migrate=ok' "$TMP/m1" "$TMP/m3" \
    && ok "真 gamedb 主迁移（账号/角色/经验流水/社交）" || bad "建表失败：$(head -3 "$TMP/m1" "$TMP/m3")"
  ACCOUNT="p21c_$$"; PASSWORD="P21c-$$-$(date +%s)"
  printf '%s\n' "$PASSWORD" | "$DOTNET" "$GD" --seed-account "$ACCOUNT" $CG >/dev/null 2>&1
  ok "自造临时账号（口令/令牌/角色号只在内存，不落盘不打印）"
  start_all && ok "真社交服 65000 + 角色服 62000 + 网关 61000 就绪" || bad "有服务没起来（见 harness 日志）"
  TOK="$TOKEN"; [ -z "$TOK" ] && TOK="$(harness_login 61000 "$ACCOUNT" "$PASSWORD")"
  [ -n "$TOK" ] && ok "登录拿到令牌" || bad "登录没拿到令牌"
  TS="$(date +%s | tail -c 6)"
  CH="$(mkchar "p21c_a$TS")"; B="$(mkchar "p21c_b$TS")"; D="$(mkchar "p21c_d$TS")"
  [ -n "$CH" ] && [ -n "$B" ] && [ -n "$D" ] && ok "造 3 个角色：CH=$CH B=$B D=$D" || bad "角色没建齐：CH=$CH B=$B D=$D"
  r="$(post "$GATEWAY/sect/create" "{\"character_id\":$CH,\"name\":\"p21c_sect_$TS\"}")"
  [ "$(expect_json "$(body_of "$r")" ok=true level=1 member_cap=10)" = "ok" ] && ok "CH 建宗门（创建者不进名册）" || bad "建宗门失败：$r"
  SECT="$(json_field "$(body_of "$r")" sect_id)"
  r="$(post "$GATEWAY/sect/join" "{\"character_id\":$B,\"sect_id\":$SECT}")"
  [ "$(expect_json "$(body_of "$r")" ok=true)" = "ok" ] && ok "B 加入同一宗门（进名册）" || bad "B 加入失败：$r"
  LB="$(lead)"; printf '%s' "$LB" | grep -q "master_character_id=$CH" && ok "掌门权威 = CH（$LB）" || bad "掌门不是 CH：$LB"
  MB="$(mem)"; printf '%s' "$MB" | grep -qE "ids=[0-9,]*\b$B\b" && ok "名册含 B：$MB" || bad "名册缺 B：$MB"
  printf '%s' "$MB" | grep -qE "ids=[0-9,]*\b$CH\b" && bad "创建者被塞进名册（破坏 a1 语义）：$MB" || ok "创建者 CH 不在名册（a1 语义不动）"
  echo "-- ① 同门两人 vs 混合门两人 --"
  E0="$(expstate)"; M0="$(money)"; R0="$(resv)"
  r1="$(settle "{\"character_id\":$CH,\"txn_id\":\"p21c_same_$$_1\",\"source\":\"dungeon\",\"party_size\":2,\"member_ids\":[$CH,$B]}")"
  [ "$(expect_json "$(body_of "$r1")" exp_gain=138 party_bonus=12 same_sect_bonus=6 duplicate=false)" = "ok" ] \
    && ok "同门两人：exp_gain=138 = 120+12+6（base+组队+同门）" || bad "同门结算不对：$r1"
  r2="$(settle "{\"character_id\":$CH,\"txn_id\":\"p21c_mix_$$_2\",\"source\":\"dungeon\",\"party_size\":2,\"member_ids\":[$CH,$D]}")"
  [ "$(expect_json "$(body_of "$r2")" exp_gain=132 party_bonus=12 same_sect_bonus=0)" = "ok" ] \
    && ok "混合门两人：exp_gain=132（P1-2d 原值不动、无同门加成）" || bad "混合门结算不对：$r2"
  G1="$(json_field "$(body_of "$r1")" exp_gain)"; G2="$(json_field "$(body_of "$r2")" exp_gain)"
  [ "${G1:-0}" -gt "${G2:-0}" ] && ok "同门 $G1 > 混合门 $G2（同门两人经验更多）" || bad "同门未更多：$G1 vs $G2"
  echo "-- ② 单人无加成 --"
  r3="$(settle "{\"character_id\":$CH,\"txn_id\":\"p21c_one_$$_3\",\"source\":\"dungeon\",\"party_size\":1,\"member_ids\":[$CH]}")"
  [ "$(expect_json "$(body_of "$r3")" exp_gain=120 party_bonus=0 same_sect_bonus=0)" = "ok" ] \
    && ok "单人：exp_gain=120（无组队、无同门）" || bad "单人结算不对：$r3"
  echo "-- ③ 只加经验：流水增 3 行、货币/疲劳/份额不动 --"
  E1="$(expstate)"; M1="$(money)"; R1="$(resv)"
  ROWS0="$(printf '%s' "$E0" | grep -oE 'rows=[0-9]+' | cut -d= -f2)"; ROWS1="$(printf '%s' "$E1" | grep -oE 'rows=[0-9]+' | cut -d= -f2)"
  [ $((ROWS1 - ROWS0)) -eq 3 ] && ok "exp_settlement 增 3 行（$ROWS0→$ROWS1）" || bad "流水行数不对：$ROWS0→$ROWS1"
  [ "$M1" = "$M0" ] && ok "货币余额一字未动：$M1" || bad "动了货币：$M0 → $M1"
  [ "$R1" = "$R0" ] && ok "疲劳/周份额不变：$R1" || bad "动了疲劳/份额：$R0 → $R1"
  echo "-- ④ 幂等重放 + 拒绝路径（每条前后显式读库）--"
  E2="$(expstate)"
  r4="$(settle "{\"character_id\":$CH,\"txn_id\":\"p21c_same_$$_1\",\"source\":\"dungeon\",\"party_size\":2,\"member_ids\":[$CH,$B]}")"
  [ "$(expect_json "$(body_of "$r4")" duplicate=true exp_gain=0 same_sect_bonus=0)" = "ok" ] \
    && ok "同一 txn 重放：duplicate=true、一笔没发" || bad "重放没挡住：$r4"
  [ "$(expstate)" = "$E2" ] && ok "重放前后 exp_state 没动（不重复入账）" || bad "重放写了库"
  E3="$(expstate)"
  r="$(curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$GATEWAY/exp/settle" -H 'Content-Type: application/json' -d "{\"character_id\":$CH,\"txn_id\":\"p21c_notok_$$\",\"party_size\":2}" 2>&1)"
  [ "$(code_of "$r")" = "401" ] && ok "无令牌：HTTP 401" || bad "无令牌码不对：$r"
  [ "$(expstate)" = "$E3" ] && ok "无令牌：显式读库没动" || bad "无令牌写了库"
  r="$(post "$GATEWAY/exp/settle" "{\"character_id\":$CH,\"txn_id\":\"p21c_nofield_$$\"}")"
  [ "$(code_of "$r")" = "400" ] && ok "缺 party_size：HTTP 400" || bad "缺字段码不对：$r"
  [ "$(expstate)" = "$E3" ] && ok "缺字段：显式读库没动" || bad "缺字段写了库"
  r="$(settle "{\"character_id\":999999999,\"txn_id\":\"p21c_other_$$\",\"source\":\"dungeon\",\"party_size\":2,\"member_ids\":[999999999,999999998]}")"
  [ "$(code_of "$r")" = "404" ] && ok "别人的角色：HTTP 404" || bad "别人的角色码不对：$r"
  [ "$(expstate)" = "$E3" ] && ok "别人的角色：显式读库没动" || bad "别人的角色写了库"
  harness_cleanup
fi
finish "P2-1c（server）"
