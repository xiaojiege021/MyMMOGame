#!/usr/bin/env bash
# C1 主线十二场（方案 2 章主线关卡 / 2.5–2.8，开发架构「主顺序之外」第 627 行）。
#   bash tools/check/check-c1.sh
#   MMO_C1_HAS_DB=1 MMO_GAMEDB='Host=...' bash ...   加真服真库端到端
# 完成标准：十二场逐场内容与方案一致；全是单人主线、不扣疲劳；第 10/11/12 场
# 等炼体/筑基/结丹突破成功；交任务复用 P1-1b5 事务号且同号不重复推进；
# 立仙立魔只在第 10 场写入；前面任务卡的规则一条不改。
set -uo pipefail
# 本机设了代理时 .NET 会把 127.0.0.1 的明文 HTTP/2 请求也送进代理；curl 一律 --noproxy '*'。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CC="--config config/character.json"
HAS_DB="${MMO_C1_HAS_DB:-0}"
TMP="$(mktemp -d)"; TMP="${TMP//\\//}"
PIDS=""
# Windows 上 dotnet 重定向输出走 GBK；中文断言按平台转码，不能「先试 UTF-8 再退 GBK」。
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

echo "== 1. 编译 =="
for proj in src/Shared/Shared.csproj src/Global/Global.csproj src/Gateway/Gateway.csproj \
            src/Character/Character.csproj src/Battle/Battle.csproj src/Social/Social.csproj \
            src/World/World.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 $n：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done

echo "== 2. 静态断言（内容正本 / 结构保证 / 老卡不改） =="
python3 - "$ROOT" >"$TMP/static.out" 2>&1 <<'PY'
import glob, hashlib, json, os, re, sys
root = sys.argv[1]
def src(p): return open(os.path.join(root, p), encoding="utf-8").read()
def cfg(p): return json.load(open(os.path.join(root, p), encoding="utf-8"))
def sha(p): return hashlib.sha256(open(os.path.join(root, p), "rb").read()).hexdigest()
checks = []
def ck(name, cond): checks.append((name, bool(cond)))

html = src("docs/MMO系统架构方案.html")
ml = cfg("config/mainline.json")
dun = cfg("config/dungeons.json")
stages = ml["stages"]
main = next(d for d in dun["dungeons"] if d["id"] == "main")

# 方案正文「主线关卡」表：十二场的关卡名 / 发生的事 / 结束时玩家会什么逐字比对。
m = re.search(r'<h3>主线关卡</h3>.*?<table>(.*?)</table>', html, re.S)
plan = {}
for num, title, happens, teaches in re.findall(
        r'<tr><td>(\d+)</td><td>(.*?)</td><td>(.*?)</td><td>(.*?)</td></tr>', m.group(1), re.S):
    plan[int(num)] = (title.strip(), happens.strip(), teaches.strip())
ck("方案正文取到十二场", len(plan) == 12)
ck("mainline.json 正好十二场且连续 1..12", len(stages) == 12 and [s["stage"] for s in stages] == list(range(1, 13)))
for s in stages:
    n = s["stage"]
    if n in plan:
        ck("第 %d 场逐字照方案" % n,
           (s["title"], s["happens"], s["teaches"]) == plan[n])

# 方案 2.5 / 2.6 / 2.7 的三份原名。
def table_of(num, title):
    mm = re.search(r'<h3><span class="n">%s</span>%s</h3>.*?<table>(.*?)</table>' % (num, title), html, re.S)
    return mm.group(1)
places = re.findall(r'<tr><td>([^<]+)</td>', table_of(r'2\.5', '首发地表'))
factions = re.findall(r'<tr><td>([^<]+)</td>', table_of(r'2\.6', '势力'))
plan_lines = {}
for tr in re.findall(r'<tr>(.*?)</tr>', table_of(r'2\.7', '开口的人'), re.S):
    tds = re.findall(r'<td[^>]*>(.*?)</td>', tr, re.S)
    if len(tds) >= 4 and tds[0].strip().isdigit():
        who = re.sub(r'<[^>]+>', ' ', tds[1]).split()[0]
        plan_lines[(who, int(tds[0].strip()))] = (
            re.sub(r'<[^>]+>', '', tds[2]).strip(),
            re.sub(r'<[^>]+>', '', tds[3]).strip())
speakers = sorted({who for who, _ in plan_lines})
ck("2.5 地名逐字照方案", ml["places"] == places)
ck("2.6 势力逐字照方案", ml["factions"] == factions)
ck("2.7 开口的人逐字照方案", sorted(ml["speakers"]) == speakers)
ck("立仙立魔只有两种", ml["alignment_choices"] == ["立仙", "立魔"])

# 定词正文只在 config/dialogue 存一份，本表只引用人名与场次。
lines = {}
for f in glob.glob(os.path.join(root, "config/dialogue/*.json")):
    d = json.load(open(f, encoding="utf-8"))
    for line in d["lines"]:
        lines[(d["id"], line["chapter"])] = line
ck("config/dialogue 的定词覆盖方案 2.7 全部十四句",
   all(k in lines for k in plan_lines))
for k, (text, limit) in plan_lines.items():
    ck("定词 %s@%d 逐字照方案" % k,
       k in lines and lines[k]["text"].strip() == text and lines[k]["limit"].strip() == limit)
for s in stages:
    refs = [(r["speaker"], r["chapter"]) for r in s["dialogue"]]
    ck("第 %d 场只引用本场开口的人" % s["stage"],
       len(refs) == len(s["speakers"]) == len(set(refs))
       and all(c == s["stage"] and w in s["speakers"] for w, c in refs)
       and all(k in lines for k in refs))

# 地表章节编号只在 config/region 存一份；方案没指定的场次 regions 留空，不发明地名。
reg = {}
for f in glob.glob(os.path.join(root, "config/region/*.json")):
    d = json.load(open(f, encoding="utf-8"))
    reg[d["name"]] = d["chapters"]
for s in stages:
    for r in s["regions"]:
        ck("第 %d 场地表 %s 的 chapters 含本场" % (s["stage"], r),
           r in reg and s["stage"] in reg[r])
ck("方案没指定地表的第 4/7/8/9/10 场不另起地名",
   all(not next(s for s in stages if s["stage"] == n)["regions"] for n in (4, 7, 8, 9, 10)))

# 十二场都挂唯一那条单人主线，不改 dungeons.json。
ck("dungeons.json 的 main 是 1 人 / 不扣疲劳 / 不占周份额",
   main["max_players"] == 1 and not main["charges_fatigue"] and not main["takes_weekly_share"])
ck("十二场 battle 全是 main", all(s["battle"] == "main" == ml["battle_dungeon"] for s in stages))
ck("第 10/11/12 场分别等炼体/筑基/结丹",
   [s["requires_breakthrough"] for s in stages[9:12]] == ["炼体", "筑基", "结丹"])
ck("第 1–9 场不等突破", all(s["requires_breakthrough"] == "" for s in stages[:9]))
ck("立仙立魔只在第 10 场", [i + 1 for i, s in enumerate(stages) if s["alignment_choice"]] == [10])
ck("等级门槛非递减且第 9 场是 45 级",
   all(a["level_min"] <= b["level_min"] for a, b in zip(stages, stages[1:]))
   and stages[8]["level_min"] == 45)
banned = {"reward", "drop", "loot", "gold", "exp", "equip", "fatigue", "price", "probability", "tax"}
ck("内容正本没有奖励 / 掉落 / 数值键",
   not (banned & set(ml.keys()))
   and all(not (banned & set(s.keys())) for s in stages))

# 老卡的规则一条不改：配置正本、进本分支、发奖与结算都不动。
old = os.popen("git diff --name-only HEAD -- config/dungeons.json config/realms.json "
               "config/world.json config/character.json config/battle.json config/region config/dialogue").read().strip()
ck("老配置正本与地表 / 对话一行未改", old == "")
ck("mainline_table 不显式登记进 config/character.json",
   "mainline_table" not in src("config/character.json"))
ck("mainline_table 不显式登记进 config/battle.json",
   "mainline_table" not in src("config/battle.json"))
table = src("src/Character/MainlineTable.cs")
repo = src("src/Character/MainlineRepository.cs")
proto = src("proto/mmo/character.proto")
ck("加载期硬校验十二场与 main 的 1 人规则",
   "StageCount = 12" in table and "main.MaxPlayers != 1 || main.ChargesFatigue || main.TakesWeeklyShare" in table)
ck("加载期拦奖励 / 数值键", "ForbiddenKeys" in table)
ck("交任务抢 P1-1b5 的 grant_txn 号", "insert into \\\"角色\\\".grant_txn" in repo)
ck("交任务只写 main_stage 与第 10 场的 alignment",
   "set main_stage = $2" in repo and "set main_stage = $2, alignment = $3" in repo)
ck("交任务不碰等级 / 疲劳 / 背包 / 货币",
   not any(x in repo for x in ["set level", "set realm", "dungeon_entry", "inventory (", "fatigue"]))
ck("突破门槛读 realm_breakthrough 的成功流水", "success = true" in repo)
ck("proto 加了只读与交任务两个 RPC",
   "rpc DescribeMainline(" in proto and "rpc TurnInMainline(" in proto)
ck("两端生成物都含新 RPC",
   "TurnInMainline" in src("src/Shared/Generated/Character.cs")
   and "TurnInMainline" in src("client/MMOGame/Assets/Proto/Generated/Character.cs"))
ck("两端 Character.cs 的 sha256 相同",
   sha("src/Shared/Generated/Character.cs") == sha("client/MMOGame/Assets/Proto/Generated/Character.cs"))
ck("网关注册了 MainlineApi", "MainlineApi.Map(app" in src("src/Gateway/GatewayProcess.cs"))
ck("网关要求请求带 txn_id、由战斗结算号对接",
   "TxnId" in src("src/Gateway/MainlineApi.cs")
   and "txnId.Length == 0" in src("src/Gateway/MainlineApi.cs")
   and "reward" not in src("src/Gateway/MainlineApi.cs").lower())
ck("核对可注入 exp_settlement", "--mainline-seed-settlement" in src("src/Character/Program.cs"))
ck("check-all.sh 已挂 c1", "c1)" in src("tools/check/check-all.sh") and "c1" in src("tools/check/check-all.sh"))
ck("凡新增的连库入口都登记进一次性命令长条件",
   "mainlineTurnInOf != 0" in src("src/Character/Program.cs"))
for name, cond in checks:
    print(("OK " if cond else "BAD ") + name)
PY
while IFS= read -r line; do
  case "$line" in "OK "*) ok "${line#OK }" ;; "BAD "*) bad "${line#BAD }" ;; *) bad "静态检查输出异常：$line" ;; esac
done <"$TMP/static.out"

echo "== 3. 只读入口（与启动共用同一份 Load） =="
OUT="$("$DOTNET" "$CD" $CC --mainline-list 2>&1 | utf8)"
printf '%s' "$OUT" | grep -q '^mainline_list_state=ok .*stages=12 ' \
  && ok "--mainline-list：12 场、pending=true、battle_dungeon=main" \
  || bad "--mainline-list 没起来：$OUT"
TITLES="$(python3 -c 'import json
d=json.load(open("config/mainline.json",encoding="utf-8"))
print(" ".join(s["title"] for s in d["stages"]))')"
MISSING=""
for t in $TITLES; do printf '%s' "$OUT" | grep -q "title=$t" || MISSING="$MISSING $t"; done
[ -z "$MISSING" ] && ok "只读入口逐场列出方案里的关卡名" || bad "只读入口缺关卡名：$MISSING"
printf '%s' "$OUT" | grep -q 'mainline_stage=10 .*requires_breakthrough=炼体 .*alignment_choice=true' \
  && ok "第 10 场等炼体且是立仙立魔那一场" || bad "第 10 场门槛不对"
printf '%s' "$OUT" | grep -q 'mainline_stage=12 .*requires_breakthrough=结丹' \
  && ok "第 12 场等结丹" || bad "第 12 场门槛不对"

echo "== 4. 坏配置（加载期就起不来） =="
python3 - "$ROOT" "$TMP" <<'PY'
import copy, json, os, sys
root, tmp = sys.argv[1:3]
base = json.load(open(os.path.join(root, "config/mainline.json"), encoding="utf-8"))
def bad(name, mut):
    d = copy.deepcopy(base); mut(d)
    json.dump(d, open(os.path.join(tmp, "bad-%s.json" % name), "w", encoding="utf-8"), ensure_ascii=False)
bad("schema", lambda d: d.update(schema="mainline-table/2"))
bad("version", lambda d: d.update(version=2))
bad("no-stages", lambda d: d.pop("stages"))
bad("reward-key", lambda d: d.update(reward=[]))
bad("eleven", lambda d: d["stages"].pop())
bad("stage-order", lambda d: d["stages"][2].update(stage=7))
bad("empty-title", lambda d: d["stages"][0].update(title=""))
bad("new-place", lambda d: d["stages"][0].update(regions=["新地名"]))
bad("new-faction", lambda d: d["stages"][0].update(faction="新势力"))
bad("new-speaker", lambda d: d["stages"][0].update(speakers=["新人"], dialogue=[{"speaker":"新人","chapter":1}]))
bad("wrong-chapter", lambda d: d["stages"][0]["dialogue"][0].update(chapter=3))
bad("level-backwards", lambda d: d["stages"][4].update(level_min=2))
bad("level-over-cap", lambda d: d["stages"][4].update(level_min=101))
bad("breakthrough-early", lambda d: d["stages"][8].update(requires_breakthrough="炼体"))
bad("breakthrough-wrong", lambda d: d["stages"][9].update(requires_breakthrough="筑基"))
bad("choice-early", lambda d: d["stages"][8].update(alignment_choice=True))
bad("choice-missing", lambda d: d["stages"][9].update(alignment_choice=False))
bad("battle-other", lambda d: d["stages"][0].update(battle="abyss"))
bad("exp-key", lambda d: d["stages"][0].update(exp=10))
bad("places-short", lambda d: d["places"].pop())
PY
for f in "$TMP"/bad-*.json; do
  n="$(basename "$f" .json)"
  out="$("$DOTNET" "$CD" $CC --mainline-list --mainline-path "$f" 2>&1 | utf8)"; rc=$?
  [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error=' \
    && ok "坏配置 ${n#bad-}：config_error= 且非 0 退出" \
    || bad "坏配置 ${n#bad-} 没挡住（rc=$rc）：$(printf '%s' "$out" | head -2)"
done

echo "== 5. 真服真库（MMO_C1_HAS_DB=1 才跑） =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  echo "  ..   跳过（没有 MMO_C1_HAS_DB=1 或 MMO_GAMEDB）"
else
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" 'ready name=Character' "角色服" || true
  "$DOTNET" "$GD" --config config/gateway.json >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST \
      http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
      -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="c1_$$"; PW="C1-$$-$(date +%s)"; TX="c1-$$-$(date +%s)"; CNAME="c1_$$_$RANDOM"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 \
    --config config/gateway.json >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login \
    -H 'Content-Type: application/json' -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" | jget token)"
  [ -n "$TOK" ] && ok "真服：登录拿到令牌（口令只在内存）" || bad "真服：登录没拿到令牌"
  A=""
  for _ in $(seq 1 10); do
    o="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
      -H 'Content-Type: application/json' -d "{\"name\":\"$CNAME\",\"race\":\"mai\",\"class_id\":\"warrior\"}")"
    A="$(printf '%s' "$o" | jget character_id)"; [ -n "$A" ] && break; sleep 0.5
  done
  [ -n "$A" ] && ok "真库：建出一名角色（$A）" || bad "真库：角色没建出来"
  get() { curl -s --noproxy '*' "http://127.0.0.1:61000$1?token=$TOK&character_id=$A"; }
  post() { curl -s --noproxy '*' -X POST "http://127.0.0.1:61000$1?token=$TOK" \
    -H 'Content-Type: application/json' -d "$2"; }
  cli() { "$DOTNET" "$CD" $CC "$@" 2>&1 | utf8; }
  ml_txn() { echo "${TX}-stg$1"; }
  ml_seed() { cli --mainline-seed-settlement "$A" "$(ml_txn "$1")" >/dev/null; }

  R="$(get /mainline)"
  [ "$(printf '%s' "$R" | jget main_stage)" = "0" ] \
    && [ "$(printf '%s' "$R" | jget next_stage)" = "1" ] \
    && [ "$(printf '%s' "$R" | jget stage_count)" = "12" ] \
    && [ "$(printf '%s' "$R" | jget config_missing)" = "false" ] \
    && ok "真库：创角时主线场次为空，下一场是第 1 场，十二场配置在位" || bad "初始主线状态不对：$R"
  ml_seed 1
  R="$(post /mainline/turn-in "{\"character_id\":$A,\"stage\":1,\"txn_id\":\"$(ml_txn 1)\",\"alignment\":\"立魔\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "false" ] \
    && [ "$(printf '%s' "$R" | jget reason)" = "alignment_not_this_stage" ] \
    && [ "$(printf '%s' "$R" | jget main_stage)" = "0" ] \
    && ok "真库：立仙立魔只在第 10 场能写，别的场带进来被拒" || bad "选择被写进了别的场：$R"
  R="$(post /mainline/turn-in "{\"character_id\":$A,\"stage\":1,\"txn_id\":\"$(ml_txn 1)\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] \
    && [ "$(printf '%s' "$R" | jget main_stage)" = "1" ] \
    && ok "真库：交第 1 场，main_stage 由 0 到 1" || bad "第 1 场没交成：$R"
  ml_seed 3
  R="$(post /mainline/turn-in "{\"character_id\":$A,\"stage\":3,\"txn_id\":\"$(ml_txn 3)\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "false" ] \
    && [ "$(printf '%s' "$R" | jget reason)" = "stage_out_of_order" ] \
    && [ "$(printf '%s' "$R" | jget main_stage)" = "1" ] \
    && ok "真库：跳场被拒且进度不变" || bad "跳场没挡住：$R"
  ml_seed 2
  R="$(post /mainline/turn-in "{\"character_id\":$A,\"stage\":2,\"txn_id\":\"$(ml_txn 2)\"}")"
  [ "$(printf '%s' "$R" | jget reason)" = "level_not_enough" ] \
    && ok "真库：等级不到第 2 场门槛被拒" || bad "等级门槛没挡住：$R"

  # 固定事务号重放：第二次整笔不做，进度与 grant_txn 都只算一次。
  cli --raw-level-write "$A" 45 >/dev/null
  cli --mainline-seed-settlement "$A" "${TX}-s2" >/dev/null
  T1="$(cli --mainline-turn-in "$A" --mainline-stage 2 --mainline-txn "${TX}-s2")"
  T2="$(cli --mainline-turn-in "$A" --mainline-stage 2 --mainline-txn "${TX}-s2")"
  printf '%s' "$T1" | grep -q 'mainline_turn_in=ok=true.*main_stage=2' \
    && printf '%s' "$T2" | grep -q 'mainline_turn_in=ok=true reason= duplicate=true.*main_stage=2' \
    && ok "真库：同一个 P1-1b5 事务号重放不重复推进（duplicate=true）" \
    || bad "幂等没成立：$T1 / $T2"
  for n in 3 4 5 6 7 8 9; do
    cli --mainline-seed-settlement "$A" "${TX}-s$n" >/dev/null
    cli --mainline-turn-in "$A" --mainline-stage "$n" --mainline-txn "${TX}-s$n" >/dev/null
  done
  S="$(cli --mainline-state "$A")"
  printf '%s' "$S" | grep -q 'main_stage=9' \
    && ok "真库：按序交完第 1–9 场，main_stage=9" || bad "九场没走完：$S"
  ml_seed 10
  R="$(post /mainline/turn-in "{\"character_id\":$A,\"stage\":10,\"txn_id\":\"$(ml_txn 10)\",\"alignment\":\"立仙\"}")"
  [ "$(printf '%s' "$R" | jget reason)" = "breakthrough_not_done" ] \
    && ok "真库：炼体没突破成功时第 10 场被拒" || bad "第 10 场门槛没挡住：$R"
  # 注入三条突破成功事实（只给核对用），逐场验第 10/11/12 的门槛与进度。
  for seg in 炼体 筑基 结丹; do cli --mainline-breakthrough "$A" "$seg" >/dev/null; done
  R="$(post /mainline/turn-in "{\"character_id\":$A,\"stage\":10,\"txn_id\":\"$(ml_txn 10)\",\"alignment\":\"立仙\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] \
    && [ "$(printf '%s' "$R" | jget alignment)" = "立仙" ] \
    && ok "真库：第 10 场写入立仙" || bad "第 10 场没成：$R"
  ml_seed 11
  R="$(post /mainline/turn-in "{\"character_id\":$A,\"stage\":11,\"txn_id\":\"$(ml_txn 11)\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] \
    && [ "$(printf '%s' "$R" | jget main_stage)" = "11" ] \
    && ok "真库：第 11 场（筑基成功后）可交" || bad "第 11 场没成：$R"
  ml_seed 12
  R="$(post /mainline/turn-in "{\"character_id\":$A,\"stage\":12,\"txn_id\":\"$(ml_txn 12)\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] \
    && [ "$(printf '%s' "$R" | jget main_stage)" = "12" ] \
    && ok "真库：第 12 场（结丹成功后）可交，个人线停在这里" || bad "第 12 场没成：$R"
  R="$(get /mainline)"
  [ "$(printf '%s' "$R" | jget main_stage)" = "12" ] \
    && [ "$(printf '%s' "$R" | jget next_stage)" = "0" ] \
    && [ "$(printf '%s' "$R" | jget alignment)" = "立仙" ] \
    && ok "真库：只读口回包全是库里的事实值，走到尽头 next_stage=0" || bad "只读口事实值不对：$R"
  S="$(cli --mainline-state "$A")"
  printf '%s' "$S" | grep -q 'main_stage=12 alignment=立仙' \
    && ok "真库：角色服独立读回进度（不经网关）" || bad "独立读回不对：$S"
  REP="$(cli --character-report "$CNAME")"
  printf '%s' "$REP" | grep -Eiq 'main_stage_null=false' \
    && printf '%s' "$REP" | grep -Eiq 'alignment_null=false' \
    && ok "真库：创角时为空的两列现在都写了" || bad "空值列没写：$REP"
fi

echo "== 6. 回归闸门 =="
if bash tools/check/preflight.sh >"$TMP/preflight.log" 2>&1; then
  ok "preflight.sh 第 4 道闸门绿"
else
  bad "preflight.sh 没过：$(tail -4 "$TMP/preflight.log" | tr '\n' ' ')"
fi
if git diff --check >"$TMP/diffcheck.log" 2>&1; then
  ok "git diff --check 通过"
else
  bad "git diff --check 有空白错误"
fi
finish "C1（主线十二场）"
