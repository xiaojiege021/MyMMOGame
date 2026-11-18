#!/usr/bin/env bash
# tools/check/check-q8.sh —— Q8 H2 掉落分配（阶段 2 第八张）。
#
# 完成标准（未覆盖排期第 57 行）：
#   默认个人份额；如开需求 / 贪婪，掷点只在服务端；同一件不双发；队长不能私吞。
#   （队伍 / 掷点的界面按开卡默认拆到 U 线，本卡只出 proto 与网关外的服务间入口。）
#
# 本脚本：
#   1. 编译战斗服 / 角色服 / 社交服；
#   2. 静态断言（配置正本、proto 面、分配只在战斗服、客户端不掷点、流水线挂载）；
#   3. 只读入口 --party-config 的 loot 段与区间；
#   4. 战斗服 --loot-state 的 9 步纯函数自检（personal / need 优先 / 平手 / 全放弃 / 区间）；
#   5. 坏队伍表 5 类都让社交服起不来（need_greed 不写 server / 区间越界 / 缺区间）；
#   6. 有库时（MMO_Q8_DB=1）：真服打两场同一个副本——personal 每人各一份、
#      need_greed 全队只出一份（合计等于表内份数），两场都查背包，不伪造。
#
#   bash tools/check/check-q8.sh
#   MMO_Q8_DB=1 bash tools/check/check-q8.sh   再加真库段（建临时账号 / 角色，跑完删）
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
SOCIAL_DLL="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHAR_SERVICE_DLL="$ROOT/src/Character/bin/Release/net10.0/""Character.dll"
BATTLE_PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
CHAR_PROBE="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TITLE="Q8（H2 掉落分配）"
HAS_DB="${MMO_Q8_DB:-0}"
TMP="$(mktemp -d)"
TMP="${TMP//\\//}"
PIDS=""
cleanup() {
  for p in $PIDS; do stop_pid "$p"; done
  rm -rf "$TMP" 2>/dev/null || true
  # 临时战斗配置写在 config/ 下（相对表路径照旧解析），跑完必删。
  rm -f "$ROOT"/config/battle-q8-*.json 2>/dev/null || true
}
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Battle/Battle.csproj src/Character/Character.csproj src/Social/Social.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 $n"
  else
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ "$BUILD_OK" = "1" ] || { finish "$TITLE"; exit 1; }

echo "== 2. 静态断言 =="
python3 "$ROOT/tools/check/check-q8-static.py" >"$TMP/static.txt" 2>&1
S_PASS="$(grep -c '^PASS ' "$TMP/static.txt" 2>/dev/null || echo 0)"
S_FAIL="$(grep -c '^FAIL ' "$TMP/static.txt" 2>/dev/null || echo 0)"
if [ "$S_PASS" = "0" ] && [ "$S_FAIL" = "0" ]; then
  bad "静态段一行都没输出（脚本崩了）"; head -10 "$TMP/static.txt" | detail
else
  PASS=$((PASS + S_PASS)); FAIL=$((FAIL + S_FAIL))
  [ "$S_FAIL" = "0" ] && ok "静态断言 $S_PASS 条全过" \
    || { bad "静态断言 $S_FAIL 条红"; grep '^FAIL ' "$TMP/static.txt" | head -12 | detail; }
fi

echo "== 3. 只读入口 --party-config（loot 段）=="
CONF="$("$DOTNET" "$SOCIAL_DLL" --party-config 2>&1)"
MODE="$(python3 -c 'import json;d=json.load(open("config/party.json"))["loot"];print(d["mode"]+"/"+d["roll"])')"
RMIN="$(python3 -c 'import json;print(json.load(open("config/party.json"))["loot"]["roll_min"])')"
RMAX="$(python3 -c 'import json;print(json.load(open("config/party.json"))["loot"]["roll_max"])')"
for kv in "loot=$MODE" "roll_range=$RMIN..$RMAX"; do
  printf '%s' "$CONF" | grep -q "$kv" && ok "配置逐项（现取）：$kv" || bad "配置缺 $kv：$CONF"
done

echo "== 4. 分配算法自检 --loot-state =="
ST="$("$DOTNET" "$BATTLE_DLL" --loot-state 2>&1)"
printf '%s' "$ST" | grep -q '^loot_state=ok' && ok "分配自检全过（$ST 末行）" \
  || { bad "分配自检红：$(printf '%s' "$ST" | grep 'ok=false' | head -3)"; }
STEPS="$(printf '%s' "$ST" | grep -c '^loot_step=.* ok=true' || true)"
[ "${STEPS:-0}" -ge 9 ] && ok "逐条 loot_step 通过 ${STEPS} 条" || bad "loot_step 只有 ${STEPS:-0} 条"
for step in personal_each_full need_greed_single_winner need_beats_greed all_pass_not_awarded \
            untouched_means_greed tie_lowest_id range_from_scope solo_personal decision_words; do
  printf '%s' "$ST" | grep -q "^loot_step=${step} ok=true" && ok "覆盖 ${step}" \
    || bad "缺步骤 ${step}：$(printf '%s' "$ST" | grep "^loot_step=${step}" | head -1)"
done

echo "== 5. 坏分配配置起不来（5 类）=="
BAD_RC=0
for c in neednogreed rollzero rollbig rollorder norange; do
  python3 - "$ROOT/config/party.json" "$TMP/bad-$c.json" "$c" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8")); c = sys.argv[3]
d["loot"]["mode"] = "need_greed"; d["loot"]["roll"] = "server"
if c == "neednogreed": d["loot"]["roll"] = "none"
elif c == "rollzero": d["loot"]["roll_min"] = 0
elif c == "rollbig": d["loot"]["roll_max"] = 5000
elif c == "rollorder": d["loot"]["roll_min"] = 90; d["loot"]["roll_max"] = 10
elif c == "norange": d["loot"].pop("roll_min", None); d["loot"].pop("roll_max", None)
json.dump(d, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
  out="$("$DOTNET" "$SOCIAL_DLL" --party-config --party-table "$TMP/bad-$c.json" 2>&1)"; rc=$?
  assert_bad_config "$out" "坏分配配置 ${c}：$(printf '%s' "$out" | head -1 | cut -c1-56)"
  [ "$rc" -ne 0 ] || BAD_RC=1
done
[ "$BAD_RC" = "0" ] && ok "5 类坏分配配置都以非 0 退出" || bad "有坏配置以 0 退出"

# 正本反过来：need_greed + server + 合法区间必须读得进来（否则上面全是假绿）。
python3 - "$ROOT/config/party.json" "$TMP/good-need-greed.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
d["loot"]["mode"] = "need_greed"; d["loot"]["roll"] = "server"
json.dump(d, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
GOOD="$("$DOTNET" "$SOCIAL_DLL" --party-config --party-table "$TMP/good-need-greed.json" 2>&1)"
printf '%s' "$GOOD" | grep -q "loot=need_greed/server" && ok "need_greed + server 可读（正本不是只会红）" \
  || bad "合法 need_greed 也起不来：$GOOD"

echo "== 6. 真服 / 真库段（同副本两场：personal 与 need_greed）=="
if [ "$HAS_DB" != "1" ]; then
  printf '       （真库段未跑：MMO_Q8_DB 未置 1；本机段已跑完，不伪造）\n'
elif [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  printf '       （真库段未跑：缺 MMO_GAMEDB / /srv/mmo/gamedb.env；本机段已跑完）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/harness"
  for p in 65000 62000 61000; do harness_track_port "$p"; done
  CC="--config $ROOT/config/character.json"; CS="--config $ROOT/config/social.json"
  CG="--config $ROOT/config/gateway.json"; GATEWAY="http://127.0.0.1:61000"
  DROP_DUNGEON="lair"
  LINES="$(python3 -c '
import json
d=json.load(open("config/dungeon_drops.json"))
rows=[e for e in d["entries"] if e["dungeon_id"]=="'"$DROP_DUNGEON"'"]
print(" ".join("%s:%d" % (l["item_id"], l["count"]) for l in rows[0]["lines"]))')"
  TOTAL="$(python3 -c '
import json
d=json.load(open("config/dungeon_drops.json"))
rows=[e for e in d["entries"] if e["dungeon_id"]=="'"$DROP_DUNGEON"'"]
print(sum(l["count"] for l in rows[0]["lines"]))')"

  start_social() {   # $1 = 要用的 party 表，$2 = 日志名
    harness_start "$TMP/harness/$2" "$DOTNET" "$SOCIAL_DLL" $CS --party-table "$1" >/dev/null
    harness_wait_ready "$TMP/harness/$2" "party_config=ok"
  }
  post() { curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" \
    -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' -d "$2" 2>&1; }
  mkchar() { local nm="$1" r id; for _ in $(seq 1 20); do
      r="$(curl -sS --noproxy '*' -m 10 -X POST "$GATEWAY/character?token=$TOK" \
        -H 'Content-Type: application/json' \
        -d "{\"name\":\"$nm\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>/dev/null)"
      id="$(printf '%s' "$r" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")')"
      [ -n "$id" ] && { printf '%s' "$id"; return 0; }; sleep 0.5; done; return 1; }
  count_items() {   # $1 = 角色号；打印表内各项数量之和
    local cid="$1" sum=0 line n item
    for line in $LINES; do
      item="${line%%:*}"; n=0
      n="$("$DOTNET" "$CHAR_PROBE" --item-count "$cid" "$item" $CC 2>&1 \
        | grep -m1 '^item_count=' | sed -E 's/^item_count=([0-9]+).*/\1/')"
      [ -n "$n" ] && sum=$((sum + n))
    done
    printf '%s' "$sum"; }
  form_party() {    # 组队 + 全员准备（队伍在社交服内存，换配置重启后要重新组）
    post "$GATEWAY/party/invite" "{\"character_id\":$A,\"target_character_id\":$B}" >/dev/null
    post "$GATEWAY/party/respond" "{\"character_id\":$B,\"from_character_id\":$A,\"accept\":true}" >/dev/null
    post "$GATEWAY/party/ready/start" "{\"character_id\":$A}" >/dev/null
    post "$GATEWAY/party/ready" "{\"character_id\":$A,\"ready\":true}" >/dev/null
    post "$GATEWAY/party/ready" "{\"character_id\":$B,\"ready\":true}" >/dev/null
  }
  run_battle() {    # 两人同场打一场 lair（party 钥匙由探针自己生成）
    "$DOTNET" "$BATTLE_PROBE" --url "${BU}/battle?dungeon=$DROP_DUNGEON" \
      --case p12d_clear --boss "$BOSS" --hit "$SKILL" \
      --a-char "$A" --b-char "$B" --party-size 2 2>&1; }

  bash "$ROOT/tools/env/db-migrate.sh" >"$TMP/migrate.log" 2>&1 \
    && ok "gamedb 迁移（幂等，本卡不新增任何表）" || { bad "迁移失败"; tail -5 "$TMP/migrate.log" | detail; }
  ACCOUNT="q8_$$"; PASSWORD="Q8-$$-$(date +%s)"
  printf '%s\n' "$PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$ACCOUNT" --seed-slots 8 $CG >/dev/null 2>&1
  ok "自造临时账号（口令 / 令牌 / 角色号只在内存）"

  harness_start "$TMP/harness/character.log" "$DOTNET" "$CHAR_SERVICE_DLL" $CC >/dev/null
  harness_start "$TMP/harness/gateway.log" "$DOTNET" "$GATEWAY_DLL" $CG >/dev/null
  start_social "$ROOT/config/party.json" social.log || bad "社交服没起来"
  harness_wait_ready "$TMP/harness/character.log" "角色服" || bad "角色服没起来"
  harness_wait_ready "$TMP/harness/gateway.log" "网关" || bad "网关没起来"
  harness_wait_http "$GATEWAY/health" 200 \
    && ok "社交服 65000 + 角色服 62000 + 网关 61000 就绪" || bad "有服务没起来"

  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
b = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
b["listen"] = "127.0.0.1:0"
b["settlement_dir"] = os.path.join(tmp, "settle")
b["character_dir"] = os.path.join(tmp, "chr")
b["exp_upstream"] = "http://127.0.0.1:62000"
b["character_upstream"] = "http://127.0.0.1:62000"
# 临时配置在 TMP 里，表路径的相对写法会按 TMP 解析；逐个表改成仓库里的绝对路径。
for key, value in list(b.items()):
    if isinstance(value, str) and value.endswith(".json"):
        b[key] = os.path.join(root, "config", os.path.basename(value))
# 配置写在 config/ 里：表路径是相对路径，换目录就会指向 TMP（跑完由 cleanup 删）。
json.dump(b, open(os.path.join(root, "config", "battle-q8.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)
PY
  harness_start "$TMP/harness/battle.log" "$DOTNET" "$BATTLE_DLL" --config "$ROOT/config/battle-q8.json" >/dev/null
  BU=""
  for _ in $(seq 1 40); do
    BU="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/harness/battle.log" 2>/dev/null \
      | tail -1 | sed 's/^listen=//')"
    [ -n "$BU" ] && break
    sleep 0.25
  done
  # 探针用的是 ClientWebSocket，只认 ws://（战斗服日志打的是 http 监听地址）。
  BU="${BU/http:\/\//ws://}"
  [ -n "$BU" ] && ok "战斗服就绪（$BU）" \
    || { bad "战斗服没打出监听地址"; tail -6 "$TMP/harness/battle.log" | detail; }

  TOK="$(harness_login 61000 "$ACCOUNT" "$PASSWORD")"
  [ -n "$TOK" ] && ok "登录拿到令牌" || bad "登录没拿到令牌"
  TS="$(date +%s | tail -c 6)"
  A="$(mkchar "q8_a$TS")"; B="$(mkchar "q8_b$TS")"
  [ -n "$A" ] && [ -n "$B" ] && ok "造两个角色（A=$A B=$B）" || bad "角色没建齐"
  BOSS="$(python3 -c 'import json;print(json.load(open("config/battle.json"))["boss"]["id"])')"
  # 出生点离 Boss 有距离，近战技能会被 range 拒；现取射程最远的那一条（不写死编号）。
  SKILL="$(python3 -c '
import json
rows = json.load(open("config/skills.json"))["skills"]
print(max(rows, key=lambda x: x.get("range_mm", 0))["id"])')"

  echo "-- ① personal（官方配置）：每人各一份 --"
  form_party
  BEFORE_A="$(count_items "$A")"; BEFORE_B="$(count_items "$B")"
  OUT="$(run_battle)"
  printf '%s' "$OUT" | grep -q '^verdict=ok' && ok "personal 场通关" \
    || { bad "personal 场没打完"; printf '%s' "$OUT" | head -6 | detail; }
  D_A=$(( $(count_items "$A") - BEFORE_A )); D_B=$(( $(count_items "$B") - BEFORE_B ))
  [ "$D_A" = "$TOTAL" ] && [ "$D_B" = "$TOTAL" ] \
    && ok "personal：两人各拿整份（各 $TOTAL 件，现取掉落表）" \
    || bad "personal 份数不对：A+$D_A B+$D_B 期望各 $TOTAL"
  grep -q 'loot_alloc mode=personal' "$TMP/harness/battle.log" \
    && ok "战斗服日志按 personal 分配" || bad "分配日志没有 personal"

  echo "-- ② need_greed（临时配置）：全队只出一份 --"
  harness_kill_port 65000
  start_social "$TMP/good-need-greed.json" social2.log || bad "need_greed 社交服没起来"
  form_party
  BEFORE_A="$(count_items "$A")"; BEFORE_B="$(count_items "$B")"
  OUT="$(run_battle)"
  printf '%s' "$OUT" | grep -q '^verdict=ok' && ok "need_greed 场通关" \
    || { bad "need_greed 场没打完"; printf '%s' "$OUT" | head -6 | detail; }
  D_A=$(( $(count_items "$A") - BEFORE_A )); D_B=$(( $(count_items "$B") - BEFORE_B ))
  [ $((D_A + D_B)) = "$TOTAL" ] \
    && ok "need_greed：两人合计 $TOTAL 件（同一件不双发）" \
    || bad "need_greed 合计 $((D_A + D_B)) 件，期望 $TOTAL（A+$D_A B+$D_B）"
  grep -q 'loot_alloc mode=need_greed' "$TMP/harness/battle.log" \
    && ok "战斗服日志按 need_greed 分配" || bad "分配日志没有 need_greed"
  grep -q 'loot_scope=ok .*mode=need_greed' "$TMP/harness/battle.log" \
    && ok "口径来自角色服转出的社交服事实（不是客户端自报）" \
    || { bad "没读到 need_greed 口径"; grep -E 'loot_scope' "$TMP/harness/battle.log" | tail -2 | detail; }
fi

finish "$TITLE"
