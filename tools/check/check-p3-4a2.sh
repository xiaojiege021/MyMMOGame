#!/usr/bin/env bash
# P3-4a2 通行证免费轨和付费轨（方案 12.1、开发架构 11 节 P3-4 / 13 节 / 14 节第 68 行）。
#   bash tools/check/check-p3-4a2.sh
#   MMO_P3_4A2_HAS_DB=1 MMO_GAMEDB='Host=...' bash ...   加真服真库端到端
# 完成标准：免费轨只发称号与外观碎片；付费轨只发当季外观、洞府摆件与很少几张保护券；
#           奖励里没有金币、强化石、灵石、装备和副本次数；付费轨的券和商城是同一种，
#           到手后能按 P1-4b 挡住掉级。
set -uo pipefail
# 本机设了代理时 .NET 会把 127.0.0.1 的明文 HTTP/2 请求也送进代理；curl 一律 --noproxy '*'。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
GL="$ROOT/src/Global/bin/Release/net10.0/Global.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
CHARACTER_DLL="$CD"
HAS_DB="${MMO_P3_4A2_HAS_DB:-0}"
TMP="$(mktemp -d)"
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
CUR="$(python3 -c 'import json;print(json.load(open("config/season.json",encoding="utf-8"))["current"])')"

echo "== 1. 编译 =="
for proj in src/Shared/Shared.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj \
            src/Global/Global.csproj src/Battle/Battle.csproj src/Social/Social.csproj \
            src/World/World.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 ${n}：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done

echo "== 2. 静态断言 =="
python3 - "$ROOT" >"$TMP/static.out" 2>&1 <<'PY'
import hashlib, json, os, re, sys
root = sys.argv[1]
def src(p): return open(os.path.join(root, p), encoding="utf-8").read()
def cfg(p): return json.load(open(os.path.join(root, p), encoding="utf-8"))
def sha(p): return hashlib.sha256(open(os.path.join(root, p), "rb").read()).hexdigest()
checks = []
def ck(name, cond): checks.append((name, bool(cond)))
pass_cfg, season, items_cfg = cfg("config/season_pass.json"), cfg("config/season.json"), cfg("config/items.json")
shop, enhance = cfg("config/shop.json"), cfg("config/enhance.json")
items = {i["id"]: i for i in items_cfg["items"]}
seasons = [r["id"] for r in season["seasons"]]
groups = {g["season"]: g for g in pass_cfg["seasons"]}
free_kinds, paid_kinds = {"title", "appearance_fragment"}, {"season_outfit", "home_decor", "protect_ticket"}
item_kind = {"title": "title", "appearance_fragment": "appearance_fragment",
             "season_outfit": "outfit", "home_decor": "home_decor", "protect_ticket": "voucher"}
tracks = [(g["season"], t) for g in pass_cfg["seasons"] for t in ("free", "paid")]
rewards = [(s, t, r) for s, t in tracks for st in groups[s][t] for r in st["rewards"]]
ck("通行证表 schema/version/pending/product_id 齐全",
   pass_cfg["schema"] == "season-pass-table/1" and pass_cfg["version"] > 0
   and pass_cfg.get("pending") is True and pass_cfg["product_id"] == "shop_season_pass_1")
ck("季与 season.json 一一对应（不缺不多）", set(groups) == set(seasons))
ck("免费轨只有称号与外观碎片", all(r["kind"] in free_kinds for s, t, r in rewards if t == "free"))
ck("付费轨只有当季外观、洞府摆件与保护券",
   all(r["kind"] in paid_kinds for s, t, r in rewards if t == "paid"))
ck("两轨各自方案写定的那几样都出现过",
   all({"title", "appearance_fragment"} <= {r["kind"] for s, t, r in rewards if t == "free" and s == g}
       for g in groups)
   and all({k} <= {r["kind"] for s, t, r in rewards if t == "paid" and s == g}
           for g in groups for k in paid_kinds))
ck("奖励里没有金币 / 灵石 / 强化石 / 装备 / 副本次数",
   not [r for s, t, r in rewards
        if r["item_id"] in ("gold", "yuanbao", "stone_enhance_low", "stone_enhance_mid",
                            "stone_enhance_high", "stone_transfer") or r["kind"] in ("equip", "gold")])
ck("每条奖励的物品都在 items.json 里、种类对得上、到账即绑",
   all(items.get(r["item_id"], {}).get("kind") == item_kind[r["kind"]]
       and items[r["item_id"]].get("bind") == "always" for s, t, r in rewards))
ck("保护券就是 enhance.json 的那一件，每阶不超过配置上限",
   all(r["item_id"] == enhance["protect_item_id"] == "ticket_protect"
       and r["count"] <= pass_cfg["protect_per_stage_max"]
       for s, t, r in rewards if r["kind"] == "protect_ticket"))
ck("通行证在商城里仍是状态发货：没有假发货物",
   (lambda r: r["delivery"].get("mode") == "season_pass"
    and not (r["delivery"].get("item_id") or "") and not (r["delivery"].get("count") or 0))(
       next(i for i in shop["items"] if i["category"] == "season_pass")))
def keys(o):
    if isinstance(o, dict):
        for k, v in o.items():
            yield k
            yield from keys(v)
    elif isinstance(o, list):
        for v in o:
            yield from keys(v)
ck("价格只有 shop.json 一处：通行证表里没有 price / 标价一类字段",
   not [k for k in keys(pass_cfg) if "price" in k.lower() or "yuanbao" in k.lower()])
allowed = {"schema", "version", "note", "pending", "product_id", "protect_per_stage_max", "seasons"}
ck("顶层与各层只有允许出现的键（金币/材料/能量一类字段长不出来）",
   set(pass_cfg) == allowed
   and all(set(g) == {"season", "free", "paid"} for g in pass_cfg["seasons"])
   and all(set(st) == {"stage", "rewards"} for s, t in tracks for st in groups[s][t])
   and all(set(r) == {"kind", "item_id", "count"} for s, t, r in rewards))
table, repo = src("src/Character/SeasonPassTable.cs"), src("src/Character/SeasonPassRepository.cs")
svc, api = src("src/Character/CharacterGrpcService.cs"), src("src/Gateway/PassApi.cs")
ck("加载期硬校验在（两轨种类 / 券编号 / 券张数 / 季存在 / 阶连续 / 键白名单）",
   all(w in table for w in ("方案 12.1 写定的两轨内容", "不许另立", "很少几张",
                            "不在 season.json 里", "阶号必须从 1 起连续", "不许出现")))
ck("产品代码里不写死季编号、物品编号或标价",
   not any(('"' + lit + '"') in table + repo + api
           for lit in seasons + [r["item_id"] for s, t, r in rewards] + ["700"]))
ck("买与领都先抢事务号（grant_txn）再判条件",
   r'insert into \"角色\".grant_txn' in repo
   and repo.count("ClaimTxnAsync(") == 3)
ck("领取流水表按季 + 轨 + 阶唯一，奖励物品与数量都从配置读",
   "PRIMARY KEY (character_id, season_id, track, stage)" in src("db/character/season_pass.sql")
   and "PRIMARY KEY (character_id, season_id, track, stage)" in src("db/character/character.sql")
   and "reward.ItemId" in repo and "reward.Count" in repo)
proto = src("proto/mmo/character.proto")
claim_req = proto.split("message ClaimSeasonPassRequest {")[1].split("}")[0]
ck("proto：领取请求只有账号 / 角色 / 轨 / 阶 / 事务号",
   len(re.findall(r"^\s*\w+\s+\w+\s*=\s*\d+;", claim_req, re.M)) == 5
   and not re.search(r"\b(item_id|count|season_id|price|gold|stone)\b", claim_req))
ck("proto：两个 RPC 都在，回包带季与保护券编号",
   "rpc DescribeSeasonPass(" in proto and "rpc ClaimSeasonPassReward(" in proto
   and "string protect_item_id = 8;" in proto)
ck("两端 Character.cs 生成物一致，CharacterGrpc.cs 只在服务端",
   sha("src/Shared/Generated/Character.cs") == sha("client/MMOGame/Assets/Proto/Generated/Character.cs")
   and os.path.exists(os.path.join(root, "src/Shared/Generated/CharacterGrpc.cs"))
   and not os.path.exists(os.path.join(root, "client/MMOGame/Assets/Proto/Generated/CharacterGrpc.cs")))
ck("网关只读口与领取口都注册了，季不读 query",
   "PassApi.Map(" in src("src/Gateway/GatewayProcess.cs")
   and 'MapGet("/season/pass"' in api and 'MapPost("/season/pass/claim"' in api
   and set(re.findall(r'Request\.Query\["([^"]+)"\]', api)) == {"token", "character_id"})
ck("角色服当前季只问全局服务，不从配置自己认定",
   "rpc DescribeSeason" not in src("src/Character/GlobalSeasonClient.cs")
   and "DescribeSeasonAsync" in src("src/Character/GlobalSeasonClient.cs")
   and "season.json" not in src("src/Character/SeasonPassRepository.cs"))
ck("老发货规则没动：保护券发货与券编号一字未改",
   next(i for i in shop["items"] if i["category"] == "protect_ticket")["delivery"]["item_id"]
   == enhance["protect_item_id"])
for name, cond in checks: print(("OK " if cond else "BAD ") + name)
PY
while IFS= read -r line; do
  case "$line" in "OK "*) ok "${line#OK }" ;; "BAD "*) bad "${line#BAD }" ;; *) bad "静态检查输出异常：$line" ;; esac
done <"$TMP/static.out"

echo "== 3. 只读入口与坏配置 =="
SO="$("$DOTNET" "$CD" --season-pass-list 2>&1)"; SRC=$?
PASS_SEASONS="$(printf '%s' "$SO" | grep -c '^season_pass_season=')"
[ "$SRC" = 0 ] && printf '%s' "$SO" | grep -q "^season_pass_list_state=ok .*product=shop_season_pass_1 price=700 protect=ticket_protect" \
  && ok "只读入口从配置现取：商品、标价、券编号、每阶上限" \
  || bad "只读入口不对（rc=${SRC}）：$(printf '%s' "$SO" | utf8 | head -2)"
[ "$PASS_SEASONS" = "$(printf '%s' "$(python3 -c 'import json;print(len(json.load(open("config/season.json",encoding="utf-8"))["seasons"]))')")" ] \
  && ok "只读入口按 season.json 的季数逐季打出（$PASS_SEASONS 季，脚本不写死）" \
  || bad "通行证表的季数与 season.json 不一致：$PASS_SEASONS"
printf '%s' "$SO" | grep -q "track=free stage=1 kind=title" \
  && printf '%s' "$SO" | grep -q "kind=protect_ticket item=ticket_protect" \
  && ok "只读入口打出每条奖励的轨、阶、种类、物品、数量" \
  || bad "只读入口没打出奖励明细"
python3 - "$ROOT" "$TMP" <<'PY'
import copy, json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
base = json.load(open(os.path.join(root, "config/season_pass.json"), encoding="utf-8"))
def w(name, o): json.dump(o, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
def mut(name, f):
    o = copy.deepcopy(base); f(o); w(name, o)
mut("schema", lambda o: o.update(schema="season-pass-table/9"))
mut("version", lambda o: o.update(version=0))
mut("gold", lambda o: o.update(gold=100))
mut("season9", lambda o: o["seasons"][0].update(season="s9"))
mut("missing", lambda o: o["seasons"].pop())
mut("free_ticket", lambda o: o["seasons"][0]["free"][0]["rewards"].__setitem__(0, {"kind": "protect_ticket", "item_id": "ticket_protect", "count": 1}))
mut("free_equip", lambda o: o["seasons"][0]["free"][0]["rewards"].__setitem__(0, {"kind": "season_outfit", "item_id": "outfit_s1", "count": 1}))
mut("paid_stone", lambda o: o["seasons"][0]["paid"][0]["rewards"].__setitem__(0, {"kind": "season_outfit", "item_id": "stone_enhance_low", "count": 1}))
mut("ticket_over", lambda o: o["seasons"][0]["paid"][2]["rewards"][0].update(count=4))
mut("two_rewards", lambda o: o["seasons"][0]["paid"][0]["rewards"].append({"kind": "home_decor", "item_id": "decor_s1", "count": 1}))
mut("stage_gap", lambda o: o["seasons"][0]["paid"][1].update(stage=3))
mut("product", lambda o: o.update(product_id="shop_outfit_1"))
PY
for c in schema version gold season9 missing free_ticket free_equip paid_stone ticket_over two_rewards stage_gap product; do
  out="$("$DOTNET" "$CD" --season-pass-list --season-pass-path "$TMP/bad-$c.json" 2>&1)"; rc=$?
  [ "$rc" -ne 0 ] && printf '%s' "$out" | utf8 | grep -q '^config_error=' \
    && ok "坏配置 ${c}：config_error= 且非 0 退出" \
    || bad "坏配置 $c 没挡住（rc=${rc}）：$(printf '%s' "$out" | utf8 | head -2)"
done

echo "== 4. 启动日志（不连库也先打出通行证事实值）=="
python3 - "$ROOT" "$TMP" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1] + "/config/character.json", encoding="utf-8"))
cfg["listen"] = "http://127.0.0.1:62092"
json.dump(cfg, open(sys.argv[2] + "/char-log.json", "w", encoding="utf-8"), ensure_ascii=False)
PY
"$DOTNET" "$CD" --config "$TMP/char-log.json" >"$TMP/char-log.log" 2>&1 & PIDS="$PIDS $!"
if wait_line "$TMP/char-log.log" '^season_pass=version='; then
  ok "启动日志打出通行证版本、商品、标价、券编号与每阶上限"
  [ "$(grep -c '^season_pass_season=' "$TMP/char-log.log")" = "$PASS_SEASONS" ] \
    && ok "启动日志逐季打出两轨阶数（$PASS_SEASONS 季）" \
    || bad "启动日志的季数不对：$(grep -c '^season_pass_season=' "$TMP/char-log.log")"
  grep -q '^shop_table_version=' "$TMP/char-log.log" && grep -q '^global_upstream=' "$TMP/char-log.log" \
    && ok "老启动日志（商城清单版本）与新上游地址都在，顺序没被打乱" \
    || bad "老启动日志被改：$(head -8 "$TMP/char-log.log" | utf8 | detail)"
else
  bad "启动没有打出 season_pass=：$(tail -5 "$TMP/char-log.log" | utf8 | detail)"
fi
stop_pid "$(printf '%s' "$PIDS" | awk '{print $NF}')"
PIDS=""

echo "== 5. 真服真库（买付费轨、领两轨、券挡掉级）=="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真服真库段（MMO_P3_4A2_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
e = json.load(open(os.path.join(root, "config", "enhance.json"), encoding="utf-8"))
for t in e["tiers"]:
    t["success_rate"] = 0.0
json.dump(e, open(os.path.join(tmp, "enhance-zero.json"), "w", encoding="utf-8"), ensure_ascii=False)
# Q6 起领取要过点数门槛：这张临时进度表把每阶压到 1 点、一次副本给 100 点，
# 让本卡原来的「买 → 领三阶」断言仍在同一套规则下成立（不改真表、不改断言的意思）。
json.dump({"schema": "season-pass-progress/1", "version": 1, "pending": True,
           "points_per_stage": 1,
           "sources": [{"id": "check_daily", "kind": "dungeon_clear", "dungeon_id": "abyss",
                        "points": 100, "period": "daily", "max_per_period": 1}]},
          open(os.path.join(tmp, "pass-progress.json"), "w", encoding="utf-8"), ensure_ascii=False)
c = json.load(open(os.path.join(root, "config", "character.json"), encoding="utf-8"))
c["listen"] = "http://127.0.0.1:62000"
c["enhance_table"] = os.path.join(tmp, "enhance-zero.json")
c["global_upstream"] = "http://127.0.0.1:64000"
c["season_pass_progress_table"] = os.path.join(tmp, "pass-progress.json")
json.dump(c, open(os.path.join(tmp, "char-real.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
  "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/character.sql" \
      --config "$ROOT/config/character.json" >"$TMP/mig.log" 2>&1 || true
  "$DOTNET" "$CD" --config "$TMP/char-real.json" >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"
  "$DOTNET" "$GL" --config "$ROOT/config/global.json" >"$TMP/global.log" 2>&1 & PIDS="$PIDS $!"
  "$DOTNET" "$GD" --config "$ROOT/config/gateway.json" >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  wait_ready "$TMP/global.log" '^ready' "全局服务" || true
  wait_ready "$TMP/gw.log" '^ready' "网关" || true
  grep -q "^season=$CUR" "$TMP/global.log" \
    && ok "真服：全局服务确认当前季 ${CUR}（通行证按它取）" \
    || bad "真服：全局服务的当前季不对：$(grep '^season=' "$TMP/global.log" | utf8 | head -1)"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST \
      http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
      -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p342_$$"; PW="P342-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 \
      --config "$ROOT/config/gateway.json" >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login \
    -H 'Content-Type: application/json' -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" | jget token)"
  [ -n "$TOK" ] && ok "真服：登录拿到令牌（账号/口令只在内存）" || bad "真服：登录没拿到令牌"
  CID=""
  for _ in $(seq 1 10); do
    CID="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"name\":\"p342_$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}" | jget character_id)"
    [ -n "$CID" ] && break; sleep 0.3
  done
  [ -n "$CID" ] && ok "真服：造一个角色（#${CID}）" || { bad "真服：造角色失败"; finish "P3-4a2（通行证）"; }
  # Q6 起领取要过点数门槛：先按同一份仓储加一次点（100 点，远超本表的三阶门槛）。
  AWARD="$("$DOTNET" "$CD" --config "$TMP/char-real.json" --pass-season "$CUR" \
    --pass-point-award "$CID" check_daily --pass-point-ref abyss 2>&1 | tail -1)"
  printf '%s' "$AWARD" | grep -q 'pass_award=ok .*after=100' \
    && ok "真服：先给通行证加点（Q6 门槛），check_daily +100" \
    || bad "真服：通行证加点失败：$(printf '%s' "$AWARD" | utf8)"
  pass_get() { curl -s --noproxy '*' "http://127.0.0.1:61000/season/pass?token=$TOK&character_id=$CID"; }
  claim() {
    curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/season/pass/claim?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"track\":\"$1\",\"stage\":$2}"
  }
  BASE="$(pass_get)"
  [ "$(printf '%s' "$BASE" | jget found)" = "true" ] \
    && [ "$(printf '%s' "$BASE" | jget season_id)" = "$CUR" ] \
    && [ "$(printf '%s' "$BASE" | jget paid)" = "false" ] \
    && [ "$(printf '%s' "$BASE" | jget protect_item_id)" = "ticket_protect" ] \
    && ok "真服：只读口给出当季 ${CUR}、没买付费轨、券编号 ticket_protect" \
    || bad "真服：只读口回包不对：$BASE"
  # 回包与配置逐条比对：一阶不多、一阶不少，物品与数量完全一致——回包不可能是别的来源。
  printf '%s' "$BASE" | python3 -c 'import json,sys
d = json.load(sys.stdin)
cfg = json.load(open("config/season_pass.json", encoding="utf-8"))
g = next(x for x in cfg["seasons"] if x["season"] == d["season_id"])
want = {(t, st["stage"], st["rewards"][0]["item_id"], st["rewards"][0]["count"])
        for t in ("free", "paid") for st in g[t]}
got = {(r["track"], r["stage"], r["item_id"], r["count"]) for r in d["rewards"]}
print("ok" if want == got and len(got) == len(d["rewards"]) else "bad")' | grep -qx ok \
    && ok "真服：两轨回包与配置逐阶一致（没有材料/金币/装备混进来）" \
    || bad "真服：两轨回包与配置不一致：$BASE"
  read -r FREE1 FREE2 PAID3 < <(python3 -c 'import json, sys
cfg=json.load(open("config/season_pass.json",encoding="utf-8")); s=sys.argv[1]
g=next(x for x in cfg["seasons"] if x["season"]==s)
print(g["free"][0]["rewards"][0]["item_id"], g["free"][1]["rewards"][0]["item_id"],
      g["paid"][-1]["rewards"][0]["item_id"])' "$CUR")
  # Windows 上 python3 的输出行尾是 \r\n，read -r 只吃掉 \n，末位字段会拖一个 \r；
  # 不剥掉的话后面所有字符串比较/模式匹配都会假失败（P3-4a2 踩过）。
  FREE1="${FREE1%$'\r'}"; FREE2="${FREE2%$'\r'}"; PAID3="${PAID3%$'\r'}"
  SPOOF="$(curl -s --noproxy '*' \
    "http://127.0.0.1:61000/season/pass?token=$TOK&character_id=$CID&season_id=s1&paid=true&item_id=gold&count=99")"
  [ "$SPOOF" = "$BASE" ] \
    && ok "真服：query 里塞季/付费/金币/数量，回包一字不变（客户端带不进来）" \
    || bad "真服：请求参数影响了回包：$SPOOF"
  R="$(claim paid 3)"; B="$(body_of "$R")"
  [ "$(printf '%s' "$B" | jget reason)" = "pass_not_owned" ] \
    && ok "真服：没买付费轨时领付费奖励被拒（pass_not_owned）" \
    || bad "真服：付费轨未买却领到了：$B"
  R="$(claim free 1)"; B="$(body_of "$R")"
  [ "$(printf '%s' "$B" | jget ok)" = "true" ] && [ "$(printf '%s' "$B" | jget item_id)" = "$FREE1" ] \
    && [ "$(printf '%s' "$B" | jget bound)" = "true" ] \
    && ok "真服：免费轨第 1 阶领到 ${FREE1}（称号），到账即绑" \
    || bad "真服：免费轨领取不对：$B"
  R="$(claim free 1)"; B="$(body_of "$R")"
  [ "$(printf '%s' "$B" | jget reason)" = "already_claimed" ] \
    && ok "真服：同一阶再领被拒（already_claimed）" \
    || bad "真服：同一阶被重复领：$B"
  curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/currency/add?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$CID,\"kind\":\"yuanbao\",\"delta\":1000}" >/dev/null
  R="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/shop/buy?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$CID,\"item_id\":\"shop_season_pass_1\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] \
    && [ "$(printf '%s' "$R" | jget state)" = "pass_activated" ] \
    && [ "$(printf '%s' "$R" | jget charged_yuanbao)" = "700" ] \
    && [ "$(printf '%s' "$R" | jget balance)" = "300" ] \
    && [ "$(printf '%s' "$R" | jget season_id)" = "$CUR" ] \
    && [ "$(printf '%s' "$R" | jget pass_owned)" = "true" ] \
    && [ "$(printf '%s' "$R" | jget delivered)" = "false" ] \
    && ok "真服：商城买通行证扣 700 元宝、开 $CUR 付费轨、不往背包放东西" \
    || bad "真服：买通行证不对：$R"
  R="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/shop/buy?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$CID,\"item_id\":\"shop_season_pass_1\"}")"
  [ "$(printf '%s' "$R" | jget reason)" = "already_owned" ] && [ "$(printf '%s' "$R" | jget balance)" = "300" ] \
    && ok "真服：同一季再买被拒（already_owned），元宝不再扣" \
    || bad "真服：重复购买没挡住：$R"
  R="$(claim paid 3)"; B="$(body_of "$R")"
  SLOT="$(printf '%s' "$B" | jget bag_slot)"
  [ "$(printf '%s' "$B" | jget ok)" = "true" ] && [ "$(printf '%s' "$B" | jget item_id)" = "$PAID3" ] \
    && [ "$(printf '%s' "$B" | jget bound)" = "true" ] && [ "${SLOT:-0}" != "0" ] \
    && ok "真服：付费轨第 3 阶领到保护券（与商城同一种）落第 $SLOT 格、到账即绑" \
    || bad "真服：付费券领取不对：$B"
  R="$(claim free 2)"; B="$(body_of "$R")"
  [ "$(printf '%s' "$B" | jget item_id)" = "$FREE2" ] \
    && ok "真服：买了付费轨之后免费轨照旧发 ${FREE2}（外观碎片，两轨互不影响）" \
    || bad "真服：免费轨被付费轨影响：$B"
  # dotnet 在 Windows 上按 \r\n 出工；统一去掉 \r，并剔掉 db_env= 那行，断言只看状态本体。
  STATE="$("$DOTNET" "$CD" --season-pass-state "$CID" --pass-season "$CUR" \
      --config "$TMP/char-real.json" 2>&1 | tr -d '\r')"
  SBODY="$(printf '%s\n' "$STATE" | grep -v '^db_env=')"
  printf '%s\n' "$SBODY" | grep -q "^season_pass_state=ok .*paid=true .*claims=3" \
    && printf '%s\n' "$SBODY" | grep -q "free:1:$FREE1" \
    && printf '%s\n' "$SBODY" | grep -q "paid:3:$PAID3" \
    && ok "真服：库里独立读回付费轨已开、三阶（免费 1/2、付费 3）都领过" \
    || bad "真服：库里读回不对：$(printf '%s' "$SBODY" | utf8)"
  printf '%s\n' "$SBODY" | grep -q "^season_pass_bag=.*${FREE1}x1@bound" \
    && printf '%s\n' "$SBODY" | grep -q "^season_pass_bag=.*${PAID3}x1@bound" \
    && ok "真服：背包里的称号与券都是绑定态（不能上架换金币）" \
    || bad "真服：奖励没落成绑定物：$(printf '%s' "$SBODY" | utf8)"
  TXN="p342-replay-$$"
  A="$("$DOTNET" "$CD" --claim-season-pass "$CID" --pass-season "$CUR" \
      --pass-track paid --pass-stage 1 --pass-claim-txn "$TXN" \
      --config "$TMP/char-real.json" 2>&1 | tail -1)"
  B2="$("$DOTNET" "$CD" --claim-season-pass "$CID" --pass-season "$CUR" \
      --pass-track paid --pass-stage 1 --pass-claim-txn "$TXN" \
      --config "$TMP/char-real.json" 2>&1 | tail -1)"
  printf '%s' "$A" | grep -q "ok=true .*item=outfit_" && printf '%s' "$B2" | grep -q "reason=duplicate .*duplicate=true" \
    && ok "真服：同一事务号重放只回 duplicate，不二次发放" \
    || bad "真服：重放没挡住：$(printf '%s' "$A" | utf8) / $(printf '%s' "$B2" | utf8)"
  curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/inventory/add?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$CID,\"slot\":20,\"item_id\":\"sword_trial\",\"count\":1}" >/dev/null
  curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/currency/add?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$CID,\"kind\":\"gold\",\"delta\":100000}" >/dev/null
  curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/inventory/add?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$CID,\"slot\":9,\"item_id\":\"stone_enhance_low\",\"count\":5}" >/dev/null
  read -r before_level before_coupon < <(
    printf '%s %s\n' \
      "$("$DOTNET" "$CD" --enhance-level "$CID" 20 --config "$TMP/char-real.json" 2>&1 \
        | sed -n 's/^enhance_state=ok .* level=\([0-9-]*\) .*/\1/p')" \
      "$("$DOTNET" "$CD" --coupon-count "$CID" --config "$TMP/char-real.json" 2>&1 \
        | sed -n 's/^coupon_count=\([0-9]*\) .*/\1/p')")
  R="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character/enhance?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$CID,\"slot\":20,\"stone_id\":\"stone_enhance_low\",\"use_coupon\":true}")"
  read -r after_level after_coupon < <(
    printf '%s %s\n' \
      "$("$DOTNET" "$CD" --enhance-level "$CID" 20 --config "$TMP/char-real.json" 2>&1 \
        | sed -n 's/^enhance_state=ok .* level=\([0-9-]*\) .*/\1/p')" \
      "$("$DOTNET" "$CD" --coupon-count "$CID" --config "$TMP/char-real.json" 2>&1 \
        | sed -n 's/^coupon_count=\([0-9]*\) .*/\1/p')")
  [ "$(printf '%s' "$R" | jget success)" = "false" ] \
    && [ "$(printf '%s' "$R" | jget protected_level)" = "true" ] \
    && [ "$(printf '%s' "$R" | jget coupon_used)" = "1" ] \
    && [ "$before_level" = "0" ] && [ "$before_coupon" = "1" ] \
    && [ "$after_level" = "0" ] && [ "$after_coupon" = "0" ] \
    && ok "真服：付费轨领到的券按 P1-4b 挡住掉级（等级 0 不变、券 1→0）" \
    || bad "真服：券没挡住掉级：$(body_of "$R") 前=$before_level/$before_coupon 后=$after_level/$after_coupon"
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

finish "P3-4a2（通行证免费轨和付费轨）"
