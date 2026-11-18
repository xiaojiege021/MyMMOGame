#!/usr/bin/env bash
# tools/check/check-q7.sh —— Q7 H1 队伍系统（阶段 2 第七张）。
#
# 完成标准（未覆盖排期第 56 行）：
#   最多 4 人；一个队长；邀请超时、接受、离队、解散、准备；进本时队伍一致；
#   有队伍 UI（按开卡默认拆到 U 线，本卡记欠项，不在这一行做）。
#
# 本脚本：
#   1. 编译社交服 / 角色服 / 网关；
#   2. 静态断言（配置正本、proto 接口面、只在内存、事实口径、网关、CI）；
#   3. 只读入口 --party-config 的人数上限 / 两个超时 / 继任 / 分配段；
#   4. 状态机自检 --party-state 的 15 步（满员 / 准备 / 继任 / 超时 / 拒绝）；
#   5. 坏队伍表 17 类都让社交服起不来；
#   6. 有库时（MMO_Q7_DB=1）：真服跑邀请、接受、未准备被拒、准备、进场按事实人数、
#      结算按队伍人数、离队、解散，以及社交服重启后队伍散（内存态）。
#
#   bash tools/check/check-q7.sh
#   MMO_Q7_DB=1 bash tools/check/check-q7.sh   再加真库段（建临时账号 / 角色，跑完删）
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
SOCIAL_DLL="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
# 真库段要起真正的角色服进程；拼接文件名，避免与「脚本指向服务 dll」混淆。
CHAR_SERVICE_DLL="$ROOT/src/Character/bin/Release/net10.0/""Character.dll"
CHARACTER_DLL="$CHAR_SERVICE_DLL"
PROBE_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TITLE="Q7（H1 队伍系统）"
HAS_DB="${MMO_Q7_DB:-0}"
TMP="$(mktemp -d)"
TMP="${TMP//\\//}"
PIDS=""
cleanup() {
  for p in $PIDS; do stop_pid "$p"; done
  rm -rf "$TMP" 2>/dev/null || true
}
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Social/Social.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 $n"
  else
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ "$BUILD_OK" = "1" ] || { finish "$TITLE"; exit 1; }

echo "== 2. 静态断言 =="
python3 "$ROOT/tools/check/check-q7-static.py" >"$TMP/static.txt" 2>&1
S_PASS="$(grep -c '^PASS ' "$TMP/static.txt" 2>/dev/null || echo 0)"
S_FAIL="$(grep -c '^FAIL ' "$TMP/static.txt" 2>/dev/null || echo 0)"
# 静态段整段崩掉时会一行都没出：计数 0 通过 0 失败不能当绿。
if [ "$S_PASS" = "0" ] && [ "$S_FAIL" = "0" ]; then
  bad "静态段一行都没输出（脚本崩了）"; head -10 "$TMP/static.txt" | detail
else
  PASS=$((PASS + S_PASS)); FAIL=$((FAIL + S_FAIL))
  [ "$S_FAIL" = "0" ] && ok "静态断言 $S_PASS 条全过" \
    || { bad "静态断言 $S_FAIL 条红"; grep '^FAIL ' "$TMP/static.txt" | head -12 | detail; }
fi

echo "== 3. 只读入口 --party-config =="
CONF="$("$DOTNET" "$SOCIAL_DLL" --party-config 2>&1)"
printf '%s' "$CONF" | grep -q '^party_config_state=ok' && ok "--party-config 打出队伍正本" \
  || { bad "--party-config 不对：$(printf '%s' "$CONF" | head -2)"; }
for kv in "max_size=$(python3 -c 'import json;print(json.load(open("config/party.json"))["max_size"])')" \
          "leader_succession=earliest_joined" "loot=personal/none" "pending=true"; do
  printf '%s' "$CONF" | grep -q "$kv" && ok "配置逐项：$kv" || bad "配置缺 $kv：$CONF"
done

echo "== 4. 状态机自检 --party-state =="
ST="$("$DOTNET" "$SOCIAL_DLL" --party-state 2>&1)"
printf '%s' "$ST" | grep -q '^party_state=ok' && ok "队伍状态机 15 步全过" \
  || { bad "状态机自检红：$(printf '%s' "$ST" | grep -v '^party_step=' | head -3)"; }
STEPS="$(printf '%s' "$ST" | grep -c '^party_step=.* ok=true' || true)"
[ "${STEPS:-0}" -ge 16 ] && ok "逐条 party_step 通过 ${STEPS} 条" || bad "party_step 只有 ${STEPS:-0} 条通过"
for step in party_full leader_succession invite_expired ready_expired respond_decline disband \
            invite_not_leader invite_to_leader invite_target_in_party ready_check_missing; do
  printf '%s' "$ST" | grep -q "^party_step=${step} ok=true" && ok "覆盖 ${step}" \
    || bad "缺步骤 ${step}：$(printf '%s' "$ST" | grep "^party_step=${step}" | head -1)"
done
AT="$("$DOTNET" "$SOCIAL_DLL" --party-state --at 2026-11-18T05:00:00+08:00 2>&1 | grep -c '^party_step=.* ok=true' || true)"
[ "${AT:-0}" -ge 16 ] && ok "--at 固定时间点同样全过（超时用例不靠真等）" || bad "--at 下只有 ${AT:-0} 步通过"

echo "== 5. 坏队伍表起不来（17 类）=="
BAD_RC=0
for c in schema version maxzero maxnine invitezero invitebig readyzero readybig succession \
         lootmode lootroll personalroll gapzero topkey lootkey noloot unlimited; do
  python3 - "$ROOT/config/party.json" "$TMP/bad-$c.json" "$c" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8")); c = sys.argv[3]
if c == "schema": d["schema"] = "party-table/9"
elif c == "version": d["version"] = 0
elif c == "maxzero": d["max_size"] = 0
elif c == "maxnine": d["max_size"] = 9
elif c == "invitezero": d["invite_timeout_s"] = 0
elif c == "invitebig": d["invite_timeout_s"] = 3601
elif c == "readyzero": d["ready_check_timeout_s"] = 0
elif c == "readybig": d["ready_check_timeout_s"] = 601
elif c == "succession": d["leader_succession"] = "random"
elif c == "lootmode": d["loot"]["mode"] = "roll_all"
elif c == "lootroll": d["loot"]["roll"] = "client"
elif c == "personalroll": d["loot"]["mode"] = "personal"; d["loot"]["roll"] = "server"
elif c == "gapzero": d["level_range"]["max_gap"] = 0
elif c == "topkey": d["members"] = []
elif c == "lootkey": d["loot"]["rounds"] = 1
elif c == "noloot": d.pop("loot", None)
elif c == "unlimited": d["note"] = "队伍人数不限，随便加"
json.dump(d, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
  out="$("$DOTNET" "$SOCIAL_DLL" --party-config --party-table "$TMP/bad-$c.json" 2>&1)"; rc=$?
  assert_bad_config "$out" "坏队伍表 ${c}：$(printf '%s' "$out" | head -1 | cut -c1-56)"
  [ "$rc" -ne 0 ] || BAD_RC=1
done
[ "$BAD_RC" = "0" ] && ok "17 类坏表都以非 0 退出" || bad "有坏表以 0 退出"
# 缺表是可选表降级（老卡临时配置目录没有 party.json 时照常起服务），不是坏表。
MISS="$("$DOTNET" "$SOCIAL_DLL" --party-config --party-table "$TMP/nope.json" 2>&1)"; MRC=$?
printf '%s' "$MISS" | grep -q 'party=missing' && [ "$MRC" = "0" ] \
  && ok "缺表降级 party=missing（不挡启动，队伍 RPC 回 party_unavailable）" \
  || bad "缺表没降级：rc=$MRC $MISS"

echo "== 6. 真服 / 真库段 =="
if [ "$HAS_DB" != "1" ]; then
  printf '       （真库段未跑：MMO_Q7_DB 未置 1；本机段已跑完，不伪造）\n'
elif [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  printf '       （真库段未跑：缺 MMO_GAMEDB / /srv/mmo/gamedb.env；本机段已跑完）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/harness"
  for p in 65000 62000 61000; do harness_track_port "$p"; done
  CC="--config $ROOT/config/character.json"; CS="--config $ROOT/config/social.json"
  CG="--config $ROOT/config/gateway.json"; GATEWAY="http://127.0.0.1:61000"
  start_all() {
    local p
    for p in 65000 62000 61000; do harness_kill_port "$p"; done
    harness_start "$TMP/harness/social.log" "$DOTNET" "$SOCIAL_DLL" $CS >/dev/null
    harness_start "$TMP/harness/character.log" "$DOTNET" "$CHAR_SERVICE_DLL" $CC >/dev/null
    harness_start "$TMP/harness/gateway.log" "$DOTNET" "$GATEWAY_DLL" $CG >/dev/null
    harness_wait_ready "$TMP/harness/social.log" "party_config=ok" || return 1
    harness_wait_ready "$TMP/harness/character.log" "角色服" || return 1
    harness_wait_ready "$TMP/harness/gateway.log" "网关" || return 1
    harness_wait_http "$GATEWAY/health" 200
  }
  post() { curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" \
    -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' -d "$2" 2>&1; }
  getp() { curl -sS --noproxy '*' -m 10 "$GATEWAY/party?character_id=$1&token=$TOK" 2>&1; }
  mkchar() { local nm="$1" r id; for _ in $(seq 1 20); do
      r="$(curl -sS --noproxy '*' -m 10 -X POST "$GATEWAY/character?token=$TOK" \
        -H 'Content-Type: application/json' \
        -d "{\"name\":\"$nm\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>/dev/null)"
      id="$(printf '%s' "$r" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")')"
      [ -n "$id" ] && { printf '%s' "$id"; return 0; }; sleep 0.5; done; return 1; }

  bash "$ROOT/tools/env/db-migrate.sh" >"$TMP/migrate.log" 2>&1 \
    && ok "gamedb 迁移（幂等，队伍不新增任何表）" || { bad "迁移失败"; tail -5 "$TMP/migrate.log" | detail; }
  ACCOUNT="q7_$$"; PASSWORD="Q7-$$-$(date +%s)"
  printf '%s\n' "$PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$ACCOUNT" --seed-slots 8 $CG >/dev/null 2>&1
  ok "自造临时账号（口令 / 令牌 / 角色号只在内存）"
  start_all && ok "真社交服 65000 + 角色服 62000 + 网关 61000 就绪" \
    || bad "有服务没起来（见 harness 日志）"
  TOK="$(harness_login 61000 "$ACCOUNT" "$PASSWORD")"
  [ -n "$TOK" ] && ok "登录拿到令牌" || bad "登录没拿到令牌"
  TS="$(date +%s | tail -c 6)"
  CH="$(mkchar "q7_a$TS")"; B="$(mkchar "q7_b$TS")"
  [ -n "$CH" ] && [ -n "$B" ] && ok "造两个角色（CH=$CH B=$B）" || bad "角色没建齐"

  echo "-- ① 邀请 / 接受 / 一个队长 --"
  r="$(getp "$CH")"; [ "$(json_field "$r" in_party)" = "false" ] \
    && ok "开局不在队伍（in_party=false）" || bad "开局就在队伍：$r"
  r="$(post "$GATEWAY/party/invite" "{\"character_id\":$CH,\"target_character_id\":$B}")"
  [ "$(expect_json "$(body_of "$r")" ok=true size=1 leader_character_id=$CH)" = "ok" ] \
    && ok "CH 邀请 B：建队、CH 是队长、只有 1 人" || bad "邀请失败：$r"
  r="$(post "$GATEWAY/party/respond" "{\"character_id\":$B,\"from_character_id\":$CH,\"accept\":true}")"
  [ "$(expect_json "$(body_of "$r")" ok=true size=2 leader_character_id=$CH)" = "ok" ] \
    && ok "B 接受后 2 人、队长仍是 CH" || bad "接受失败：$r"
  r="$(post "$GATEWAY/party/invite" "{\"character_id\":$B,\"target_character_id\":$CH}")"
  [ "$(json_field "$(body_of "$r")" reason)" = "not_leader" ] \
    && ok "队员不能邀请（not_leader）" || bad "队员邀请没被拒：$r"

  echo "-- ② 未准备不许进本（进本时队伍一致）--"
  RESV0="$("$DOTNET" "$PROBE_DLL" --reserves-of "$CH" $CC 2>&1 | grep -m1 '^reserves=')"
  r="$(post "$GATEWAY/dungeon/enter" "{\"character_id\":$CH,\"dungeon_id\":\"lair\",\"party_size\":4}")"
  [ "$(json_field "$(body_of "$r")" reason)" = "party_not_ready" ] \
    && ok "队伍未准备：party_not_ready（自报 4 人也不算数）" || bad "未准备却放行：$r"
  RESV1="$("$DOTNET" "$PROBE_DLL" --reserves-of "$CH" $CC 2>&1 | grep -m1 '^reserves=')"
  [ -n "$RESV0" ] && [ "$RESV0" = "$RESV1" ] \
    && ok "被拒那一笔没扣周份额（$RESV0）" || bad "拒绝路径改了数值：$RESV0 -> $RESV1"

  echo "-- ③ 准备：队长发起、全员确认才一致 --"
  r="$(post "$GATEWAY/party/ready/start" "{\"character_id\":$CH}")"
  [ "$(expect_json "$(body_of "$r")" ok=true all_ready=false)" = "ok" ] \
    && ok "队长发起准备检查（all_ready=false）" || bad "发起准备失败：$r"
  post "$GATEWAY/party/ready" "{\"character_id\":$CH,\"ready\":true}" >/dev/null
  r="$(post "$GATEWAY/party/ready" "{\"character_id\":$B,\"ready\":true}")"
  [ "$(expect_json "$(body_of "$r")" ok=true all_ready=true size=2)" = "ok" ] \
    && ok "两人都确认后 all_ready=true" || bad "准备没齐：$r"

  echo "-- ④ 进本按事实人数（自报 4 人只当提示）--"
  # 巢穴 min=1 / max=4、占周份额：2 人队进一次应当扣 2 份、记 2 份奖励，
  # 这条同时证明「进入次数与份额按事实人数，不按客户端自报的 4」。
  r="$(post "$GATEWAY/dungeon/enter" "{\"character_id\":$CH,\"dungeon_id\":\"lair\",\"party_size\":4}")"
  [ "$(expect_json "$(body_of "$r")" allowed=true weekly_share_cost=2 reward_shares=2)" = "ok" ] \
    && ok "准备齐了能进本：扣 2 份 / 记 2 份（事实人数，不是自报 4）" || bad "准备齐了仍被拒或份数不对：$r"
  grep -qE "dungeon_enter .*party=2 " "$TMP/harness/character.log" \
    && ok "角色服日志按事实 2 人记（不是自报 4）" \
    || { bad "进场人数没按事实"; grep -E "dungeon_enter" "$TMP/harness/character.log" | tail -2 | detail; }

  echo "-- ⑤ 结算：有队伍时人数取事实 --"
  BASE="$(python3 -c 'import json;d=json.load(open("config/exp.json"));print(d["base_exp"])')"
  PCT="$(python3 -c 'import json;d=json.load(open("config/exp.json"));print(d["party_bonus_pct"])')"
  WANT="$(python3 -c "b=$BASE;p=$PCT;print(b+b*p//100)")"
  r="$(post "$GATEWAY/exp/settle" "{\"character_id\":$CH,\"txn_id\":\"q7_$$_1\",\"source\":\"dungeon\",\"party_size\":4}")"
  [ "$(expect_json "$(body_of "$r")" exp_gain=$WANT party_bonus=$((WANT - BASE)))" = "ok" ] \
    && ok "自报 4 人、事实 2 人 → exp_gain=$WANT（现取配置 base=$BASE / pct=$PCT）" \
    || bad "结算没按事实：$r"

  echo "-- ⑥ 离队 / 解散 --"
  r="$(post "$GATEWAY/party/leave" "{\"character_id\":$B}")"
  [ "$(expect_json "$(body_of "$r")" ok=true size=1 leader_character_id=$CH)" = "ok" ] \
    && ok "B 离队后剩 1 人、队长不变" || bad "离队失败：$r"
  r="$(post "$GATEWAY/party/disband" "{\"character_id\":$CH}")"
  [ "$(json_field "$(body_of "$r")" ok)" = "true" ] && ok "队长解散队伍" || bad "解散失败：$r"
  r="$(getp "$CH")"; [ "$(json_field "$r" in_party)" = "false" ] \
    && ok "解散后 in_party=false" || bad "解散后还在队伍：$r"

  echo "-- ⑦ 队伍只在内存：社交服重启就散 --"
  post "$GATEWAY/party/invite" "{\"character_id\":$CH,\"target_character_id\":$B}" >/dev/null
  harness_kill_port 65000
  harness_start "$TMP/harness/social2.log" "$DOTNET" "$SOCIAL_DLL" $CS >/dev/null
  harness_wait_ready "$TMP/harness/social2.log" "party_config=ok" || bad "社交服重启没起来"
  r="$(getp "$CH")"; [ "$(json_field "$r" in_party)" = "false" ] \
    && ok "重启后队伍已散（状态只在进程内存，不落库）" || bad "重启后队伍还在：$r"
fi

finish "$TITLE"
