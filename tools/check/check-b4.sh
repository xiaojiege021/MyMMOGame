#!/usr/bin/env bash
# tools/check/check-b4.sh —— B4 坐骑 / 灵兽（开发架构 14.5 顺序 11）。
#
# 完成标准（14.5 的 B4 行，逐条对应下面的断言）：
#   1. 玩法坐骑只差速度：档位与 speed_bps 只在 config/mount.json，代码不写死；
#   2. 灵兽只做收集 / 外观 / 生活协助：不进伤害公式，与图鉴共用一处上限；
#   3. 商城坐骑只换外观、不叠速度（12.1）：玩法坐骑不进商城、不改 mount_skin 口径。
#
# 口径：
#   - 坐骑 / 灵兽都是 items.json 里的物品（kind=mount / spirit_beast、stack_max=1、到账即绑），
#     获得只走 config/dungeon_drops.json；灵兽收集 = 背包现在持有（与外观收藏同一口径）。
#   - 速度只由世界服算进地表水平位移上限，基线仍是 world.json 的 max_move_speed；
#     不飞行、不进战斗，src/Battle 里不许出现 mount。
#
#   bash tools/check/check-b4.sh
#   MMO_B4_SKIP_BUILD=1 bash tools/check/check-b4.sh   跳过编译
#   MMO_B4_DB=1 MMO_B4_CHARACTER=<角色号> bash tools/check/check-b4.sh   加真库段
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CHARACTER_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TMP="$(mktemp -d)"
TITLE="B4（坐骑 / 灵兽）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_B4_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_B4_SKIP_BUILD=1）\n'
else
  if "$DOTNET" build src/MMOGame.sln -c Release >"$TMP/build.log" 2>&1; then
    ok "src/MMOGame.sln 编译通过"
  else
    bad "编译失败：$(tail -3 "$TMP/build.log" | tr '\n' ' ')"
    finish "$TITLE"
    exit 1
  fi
  if "$DOTNET" build tools/probe/CliProbe/CliProbe.csproj -c Release >"$TMP/probe.log" 2>&1; then
    ok "CliProbe 编译通过"
  else
    bad "CliProbe 编译失败：$(tail -3 "$TMP/probe.log" | tr '\n' ' ')"
    finish "$TITLE"
    exit 1
  fi
fi

# 把 python 那边的 OK / FAIL 行转成计数。
feed() {
  while IFS= read -r line; do
    case "$line" in
      "OK "*) ok "${line#OK }" ;;
      "FAIL "*) bad "${line#FAIL }" ;;
      "") ;;
      *) printf '  ..   %s\n' "$line" ;;
    esac
  done
}

echo "== 2. 配置（坐骑 / 灵兽 / 物品 / 掉落） =="
python3 - <<'PY' | feed
import json

def load(path):
    return json.load(open(path, encoding="utf-8"))

out = []
def ok(m): out.append("OK " + m)
def bad(m): out.append("FAIL " + m)

items = load("config/items.json")
by_id = {i["id"]: i for i in items["items"]}
mounts = load("config/mount.json")
beasts = load("config/spirit_beast.json")
codex = load("config/hero_codex.json")
drops = load("config/dungeon_drops.json")

# 1 坐骑表形状
ok_shape = (mounts.get("schema") == "mount-table/1"
            and [t["id"] for t in mounts["tiers"]] == ["normal", "advanced"]
            and len(mounts["entries"]) == 4)
(ok if ok_shape else bad)("mount.json 形状：两档 normal/advanced + 4 只坐骑")

# 2 两档区间不交叉，高级下限高于普通上限
n, a = mounts["tiers"]
cross_ok = n["min_speed_bps"] <= n["max_speed_bps"] and a["min_speed_bps"] <= a["max_speed_bps"] \
    and a["min_speed_bps"] > n["max_speed_bps"]
(ok if cross_ok else bad)(
    "坐骑两档区间不交叉（普通 %d..%d，高级 %d..%d）"
    % (n["min_speed_bps"], n["max_speed_bps"], a["min_speed_bps"], a["max_speed_bps"]))

# 3 每只：档位认得、落在本档区间、物品 kind=mount、不可叠
tier_map = {t["id"]: t for t in mounts["tiers"]}
bad_rows = []
for e in mounts["entries"]:
    t = tier_map.get(e["tier"])
    goods = by_id.get(e["item_id"])
    if t is None or goods is None:
        bad_rows.append(e["item_id"] + ":missing")
    elif goods["kind"] != "mount":
        bad_rows.append(e["item_id"] + ":kind=" + goods["kind"])
    elif goods["stack_max"] != 1:
        bad_rows.append(e["item_id"] + ":stack")
    elif not (t["min_speed_bps"] <= e["speed_bps"] <= t["max_speed_bps"]):
        bad_rows.append(e["item_id"] + ":speed")
(ok if not bad_rows else bad)("四只坐骑都落在本档区间且 kind=mount、stack_max=1"
                              + ("" if not bad_rows else "：" + ",".join(bad_rows)))

# 4 高级档的每一只都真的比普通档快（方案 9.4「只差移动速度」）
normal_max = max(e["speed_bps"] for e in mounts["entries"] if e["tier"] == "normal")
advanced_min = min(e["speed_bps"] for e in mounts["entries"] if e["tier"] == "advanced")
(ok if advanced_min > normal_max else bad)(
    "高级坐骑比普通快（普通最高 %d < 高级最低 %d）" % (normal_max, advanced_min))

# 5 灵兽表形状：三档 + 六只
ok_beast = (beasts.get("schema") == "spirit-beast/1"
            and [t["id"] for t in beasts["tiers"]] == ["normal", "advanced", "divine"]
            and len(beasts["entries"]) == 6
            and beasts.get("passive_kind") == "life_assist")
(ok if ok_beast else bad)("spirit_beast.json 形状：三档 + 6 只，passive_kind=life_assist")

# 6 每只灵兽：物品 kind=spirit_beast、不可叠
beast_bad = []
btiers = {t["id"] for t in beasts["tiers"]}
for e in beasts["entries"]:
    goods = by_id.get(e["item_id"])
    if e["tier"] not in btiers:
        beast_bad.append(e["item_id"] + ":tier")
    elif goods is None or goods["kind"] != "spirit_beast":
        beast_bad.append(e["item_id"] + ":kind")
    elif goods["stack_max"] != 1:
        beast_bad.append(e["item_id"] + ":stack")
(ok if not beast_bad else bad)("六只灵兽都 kind=spirit_beast、stack_max=1、档位认得"
                              + ("" if not beast_bad else "：" + ",".join(beast_bad)))

# 7 一处上限：灵兽合计 + 图鉴合计 <= hero_codex.json 的 max_total_passive_pct_bps
cap = codex["max_total_passive_pct_bps"]
codex_total = sum(e["passive_pct_bps"] for e in codex["entries"])
beast_total = sum(e["life_assist_pct_bps"] for e in beasts["entries"])
(ok if codex_total + beast_total <= cap else bad)(
    "生活协助一处上限：图鉴 %d + 灵兽 %d <= %d" % (codex_total, beast_total, cap))

# 8 物品表版本与十件新物品
want = {e["item_id"] for e in mounts["entries"]} | {e["item_id"] for e in beasts["entries"]}
missing = sorted(i for i in want if i not in by_id)
bind_bad = sorted(i for i in want if i in by_id and by_id[i].get("bind") != "always")
(ok if items.get("version") == 8 and not missing and not bind_bad else bad)(
    "items.json v8 收录十件坐骑 / 灵兽且到账即绑"
    + ("" if not missing else "：缺 " + ",".join(missing))
    + ("" if not bind_bad else "：bind 不是 always " + ",".join(bind_bad)))

# 9 掉落表：五个副本行各带两行坐骑 / 灵兽，且都在物品表里
if drops.get("version") != 4:
    bad("dungeon_drops.json 版本不是 4（现 %s）" % drops.get("version"))
else:
    covered = {}
    for entry in drops["entries"]:
        hit = [l["item_id"] for l in entry["lines"] if l["item_id"] in want]
        if hit:
            covered[entry["dungeon_id"]] = hit
    absent = [k for k, v in covered.items() if len(v) != 2]
    unknown = sorted(x for v in covered.values() for x in v if x not in by_id)
    (ok if len(covered) == 5 and not absent and not unknown else bad)(
        "五个副本行各 2 行坐骑 / 灵兽掉落（覆盖 %d 个副本）" % len(covered)
        + ("" if not absent else "：行数不对 " + ",".join(absent))
        + ("" if not unknown else "：物品表没有 " + ",".join(unknown)))

print("\n".join(out))
PY

echo "== 3. 静态：协议 / 迁移 / 世界服 / 网关 =="
grep -q 'rpc MountUp(MountUpRequest)' proto/mmo/character.proto \
  && grep -q 'rpc Dismount(DismountRequest)' proto/mmo/character.proto \
  && grep -q 'rpc DescribeMount(DescribeMountRequest)' proto/mmo/character.proto \
  && grep -q 'rpc DescribeSpiritBeast(DescribeSpiritBeastRequest)' proto/mmo/character.proto \
  && ok "proto 四条 rpc 齐全（骑上 / 下 / 读坐骑 / 读灵兽）" \
  || bad "proto 缺 rpc"

grep -q 'int32 mount_speed_bps = 7;' proto/mmo/character.proto \
  && ok "ListInventoryResponse 追加 mount_speed_bps = 7（既有编号不动）" \
  || bad "ListInventoryResponse 没有 mount_speed_bps"

# 请求里带不进速度：MountUpRequest 只有 account / character_id / bag_slot。
REQ_LINES="$(python3 - <<'PY'
import re
s = open("proto/mmo/character.proto", encoding="utf-8").read()
m = re.search(r"message MountUpRequest \{(.*?)\n\}", s, re.S)
print("|".join(l.strip() for l in m.group(1).strip().splitlines()
                if l.strip() and not l.strip().startswith("//")))
PY
)"
if [ "$REQ_LINES" = "string account = 1;|uint64 character_id = 2;|uint32 bag_slot = 3;" ]; then
  ok "骑上请求只带账号 / 角色 / 格号，速度与档位带不进"
else
  bad "骑上请求形状变了：$REQ_LINES"
fi

grep -q 'CREATE TABLE IF NOT EXISTS "角色".character_mount' db/character/mount.sql \
  && grep -q 'character_mount' db/character/character.sql \
  && python3 -c 'import json,sys;d=json.load(open("db/migrations.json",encoding="utf-8"));e=[x for s in d["schemas"] for x in s["entries"] if x["file"]=="db/character/mount.sql"];sys.exit(0 if e and e[0]["version"]==34 else 1)' \
  && ok "character_mount 迁移 v34（单独一份 + 主迁移同段幂等 DDL）" \
  || bad "character_mount 迁移不全"

grep -q 'mountFactor' src/World/LineRegistry.cs \
  && grep -q 'SetMountSpeed' src/World/LineRegistry.cs \
  && grep -q 'SyncMountSpeedAsync' src/World/Grpc/Line.cs \
  && grep -c 'SetMountSpeed' src/World/FlightGate.cs | grep -qv '^0$' \
  && ok "世界服：进线 / 飞行 / 跨区三处写加成，Report 按加成放大位移上限" \
  || bad "世界服缺坐骑加成那一段"

grep -q 'max_move_speed' src/World/WorldConfig.cs \
  && grep -q 'config.MaxMoveSpeed \* mountFactor' src/World/LineRegistry.cs \
  && ok "基线仍是 world.json 的 max_move_speed，加成只做乘区" \
  || bad "位移上限基线被改写"

# 坐骑不碰飞行：飞着的时候不吃加成（方案 9.4 只差移动速度，12.1 飞行仍由境界开放）。
grep -q 'previous.Flying ? 1.0 : 1.0 + member.MountSpeedBps' src/World/LineRegistry.cs \
  && ok "坐骑加成只在落地时生效，飞行速度仍只由 max_move_speed 决定" \
  || bad "坐骑加成会放大飞行速度"

# \b 是必须的：amount 里也含 mount，按词边界只认真正的坐骑字段。
if grep -rqiE '\bmount' src/Battle --include='*.cs' 2>/dev/null; then
  bad "战斗服里出现了 mount：坐骑不许进伤害公式"
else
  ok "战斗服零 mount 引用：不进伤害公式"
fi

grep -q 'MapPost("/character/mount"' src/Gateway/Api/Character/Companion.cs \
  && grep -q 'MapPost("/character/dismount"' src/Gateway/Api/Character/Companion.cs \
  && grep -q 'MapGet("/character/mounts"' src/Gateway/Api/Character/Companion.cs \
  && grep -q 'MapGet("/character/spirit-beasts"' src/Gateway/Api/Character/Companion.cs \
  && grep -q 'RegisterCompanion' src/Gateway/CharacterApi.cs \
  && ok "网关四条路由 + 注册" \
  || bad "网关路由不全"

grep -q 'MountSpeedBps = await mounts.SpeedBpsAsync' src/Character/Grpc/Inventory.cs \
  && ok "ListInventory 按当前坐骑回 speed_bps（世界服只认这一个来源）" \
  || bad "ListInventory 没回坐骑加成"

echo "== 4. 无库：配置现值与坏配置 =="
CFG="$("$DOTNET" "$CHARACTER_DLL" --companion-config 2>&1)"
printf '%s' "$CFG" | grep -q 'mount_entries=4' \
  && printf '%s' "$CFG" | grep -q 'entries=6' \
  && printf '%s' "$CFG" | grep -q 'cap_bps=300' \
  && ok "probe 读两张表：4 只坐骑 / 6 只灵兽 / 上限 300" \
  || bad "probe 读表不对：$(printf '%s' "$CFG" | head -2 | tr '\n' ' ')"

for pair in "mount_luyun:normal:2000" "mount_qingjiao:normal:2500" \
            "mount_liuyan:advanced:4000" "mount_jiuxiao:advanced:4500"; do
  IFS=: read -r id tier bps <<< "$pair"
  OUT="$("$DOTNET" "$CHARACTER_DLL" --mount-speed "$id" 2>&1)"
  printf '%s' "$OUT" | grep -q "tier=$tier speed_bps=$bps" \
    && ok "坐骑 $id → $tier / $bps" \
    || bad "坐骑 $id 的档位或速度不对：$OUT"
done

# 坏配置：每一条都要求「起不来」（config_error 且非 0 退出）。
mkcopy() {
  python3 - "$1" "$2" "$3" <<'PY'
import json, sys
src, dst, mode = sys.argv[1], sys.argv[2], sys.argv[3]
d = json.load(open(src, encoding="utf-8"))
if mode == "tier_bad":
    d["entries"][0]["tier"] = "epic"
elif mode == "speed_out":
    d["entries"][0]["speed_bps"] = 9999
elif mode == "not_mount":
    d["entries"][0]["item_id"] = "beast_tuanyuan"
elif mode == "cross":
    d["tiers"][1]["min_speed_bps"] = 2000
elif mode == "beast_over":
    d["entries"][0]["life_assist_pct_bps"] = 100
elif mode == "beast_tier":
    d["entries"][0]["tier"] = "ancient"
json.dump(d, open(dst, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
}

bad_case() {
  local mode="$1" file="$2" flag="$3" label="$4"
  local path="$TMP/$mode.json"
  cp "$file" "$path"
  mkcopy "$file" "$path" "$mode"
  local out code
  out="$("$DOTNET" "$CHARACTER_DLL" "$flag" "$path" 2>&1)"
  code=$?
  if [ "$code" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error='; then
    ok "坏配置起不来：$label"
  else
    bad "坏配置没被拦住：$label（exit=$code $out）"
  fi
}

bad_case tier_bad config/mount.json --mount-table-path "坐骑档位不认识"
bad_case speed_out config/mount.json --mount-table-path "坐骑速度越档"
bad_case not_mount config/mount.json --mount-table-path "拿灵兽当坐骑"
bad_case cross config/mount.json --mount-table-path "两档区间交叉"
bad_case beast_over config/spirit_beast.json --beast-table-path "灵兽合计超上限"
bad_case beast_tier config/spirit_beast.json --beast-table-path "灵兽档位不认识"

echo "== 5. 真库段（可选） =="
if [ "${MMO_B4_DB:-0}" = "1" ] && [ -n "${MMO_B4_CHARACTER:-}" ]; then
  CID="${MMO_B4_CHARACTER}"
  UP="$("$DOTNET" "$CHARACTER_DLL" --mount "$CID" 1 2>&1)"
  ROW="$("$DOTNET" "$CHARACTER_DLL" --mount-row "$CID" 2>&1)"
  DOWN="$("$DOTNET" "$CHARACTER_DLL" --dismount "$CID" 2>&1)"
  AGAIN="$("$DOTNET" "$CHARACTER_DLL" --dismount "$CID" 2>&1)"
  BEAST="$("$DOTNET" "$CHARACTER_DLL" --beast-row "$CID" 2>&1)"

  printf '%s' "$UP" | grep -q '^mount=ok' \
    && ok "真库：第 1 格骑上" \
    || bad "真库骑不上：$UP"
  printf '%s' "$ROW" | grep -q 'equipped=mount_' \
    && ok "真库：读回当前坐骑" \
    || bad "真库读不回坐骑：$ROW"
  printf '%s' "$DOWN" | grep -q '^dismount=ok' \
    && ok "真库：下坐骑" \
    || bad "真库下不来：$DOWN"
  printf '%s' "$AGAIN" | grep -q 'reason=not_mounted' \
    && ok "真库：没骑时下坐骑回 not_mounted" \
    || bad "真库重复下坐骑没按约定拒绝：$AGAIN"
  printf '%s' "$BEAST" | grep -q '^beast_row=ok' \
    && ok "真库：灵兽收集度读得出来" \
    || bad "真库读不出灵兽：$BEAST"
else
  printf '  ..   跳过真库段（MMO_B4_DB=1 且 MMO_B4_CHARACTER=<角色号> 时跑）\n'
fi

finish "$TITLE"
