#!/usr/bin/env bash
# tools/check/check-k6.sh —— K6 地名 / NPC / 怪物 / 对话配置化（开发架构 14.7 顺序 5）。
#
# 完成标准（14.7 的 K6 行，逐条对应下面的断言）：
#   1. npc.json / monsters.json 落地；
#   2. NPC 带位置 / 所属 / 商店 / 对话引用；
#   3. 地名继续 region/*.json；
#   4. 客户端不硬编码人名 / 地名。
#
# 口径：
#   - 内容正本只有 config/：npc.json（实体）、monsters.json（实体）、region/*.json（地名）、
#     dialogue/*.json（台词）、world.json（地表顺序 / 势力 / 开口人顺序）。
#   - 客户端的人名 / 地名来自 tools/gen/gen-ui-content.py 生成的 UiContentTable.cs；
#     生成物逐字比对，手写代码里不许再出现 Name = "某人" / Id = "某地" 这类内容初始化。
#     本卡只核对开口人的元数据；定词由 C5 一起搬进生成表（SpeakerDialogue），
#     正本仍是 config/dialogue/*.json，C5 的 check-c5.sh 逐条比对。
#   - npc.json 的九位开口人必须与 world.json 的 speakers 一一对上；商店引用必须落在
#     npc_shop.json；怪物技能必须落在 skills.json、掉落必须落在 items.json。
#
#   bash tools/check/check-k6.sh
#   MMO_K6_SKIP_BUILD=1 bash tools/check/check-k6.sh   跳过编译
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
# Windows 上 dotnet 重定向输出走 GBK：NPC / 怪物行里带人名与地名，断言前先按平台转码。
utf8() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      python3 -c 'import sys; sys.stdout.buffer.write(
          sys.stdin.buffer.read().decode("gbk", "replace").encode("utf-8"))' ;;
    *) cat ;;
  esac
}

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CHARACTER_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
WORLD_DLL="$ROOT/src/World/bin/Release/net10.0/World.dll"
BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
TMP="$(mktemp -d)"
TITLE="K6（server + client）"
PIDS=""
trap 'for p in $PIDS; do kill "$p" 2>/dev/null; done; rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_K6_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_K6_SKIP_BUILD=1）\n'
else
  for proj in src/Character/Character.csproj src/World/World.csproj src/Battle/Battle.csproj; do
    if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
      ok "$(basename "$proj" .csproj) 编译通过"
    else
      bad "$(basename "$proj") 编译失败"; tail -14 "$TMP/build.log" | detail
    fi
  done
fi

echo "== 2. 静态断言：内容表、引用与客户端生成物 =="
ROOT="$ROOT" python3 - > "$TMP/static.txt" 2>&1 <<'PY'
import glob, json, os, re

root = os.environ["ROOT"]

def read(p):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server", "CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World", "SectRepository.cs": "Sect", "SocialGrpcService.cs": "Grpc", "WorldGrpcService.cs": "Grpc", "Program.cs": "Cli"}
    _name = __import__("os").path.basename(p)
    if _name in _sub:
        _os = __import__("os"); _g = __import__("glob")
        _b = p if _os.path.isabs(p) else _os.path.join(_os.getcwd(), p)
        if not _os.path.exists(_b):
            _d = _os.path.dirname(_os.getcwd())
            while _d and not _os.path.exists(_os.path.join(_d, p)):
                _nd = _os.path.dirname(_d)
                if _nd == _d: break
                _d = _nd
            _b = _os.path.join(_d, p)
        return "".join(open(f, encoding="utf-8").read() for f in
                       [_b] + sorted(_g.glob(_os.path.join(_os.path.dirname(_b), _sub[_name], "*.cs"))))
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def cfg(p):
    return json.loads(read(p))

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

world = cfg("config/world.json")
npc = cfg("config/npc.json")
monsters = cfg("config/monsters.json")
items = cfg("config/items.json")
skills = cfg("config/skills.json")
shops = cfg("config/npc_shop.json")

regions = {}
for path in glob.glob(os.path.join(root, "config/region/*.json")):
    doc = json.load(open(path, encoding="utf-8"))
    regions[doc["id"]] = doc
region_ids = set(regions)
factions = set(world.get("factions") or [])
speakers = list(world.get("speakers") or [])
dialogue_ids = {os.path.basename(p)[:-5] for p in glob.glob(os.path.join(root, "config/dialogue/*.json"))}
item_ids = {x["id"] for x in items["items"]}
skill_ids = {x["id"] for x in skills["skills"]}
shop_ids = {s["id"] for s in shops["shops"]}

# ---------------------------------------------------------------- 1. NPC 表
npcs = npc.get("npcs") or []
ids = [x.get("id") for x in npcs]
names = [x.get("name") for x in npcs]
report("npc_schema", npc.get("schema") == "npc-table/1" and npc.get("version") == 1,
       "%s v%s" % (npc.get("schema"), npc.get("version")))
report("npc_ids", len(ids) == len(set(ids)) and all(re.fullmatch(r"npc_[a-z0-9_]+", i or "") for i in ids),
       "重复或形状不对")
report("npc_names_unique", len(names) == len(set(names)), "重名")
report("npc_count_floor", len(npcs) >= 9, "只有 %d 位" % len(npcs))

bad = []
for x in npcs:
    iid = x.get("id")
    if x.get("region") not in region_ids:
        bad.append(iid + ":region")
    pos = x.get("pos") or {}
    if not all(isinstance(pos.get(k), (int, float)) for k in ("x", "y", "z")):
        bad.append(iid + ":pos")
    facing = x.get("facing")
    if not isinstance(facing, (int, float)) or facing < 0 or facing > 360:
        bad.append(iid + ":facing")
    if x.get("faction") and x["faction"] not in factions:
        bad.append(iid + ":faction")
    if x.get("role") not in {"quest", "innkeeper", "merchant", "trainer", "flavor"}:
        bad.append(iid + ":role")
    d = x.get("dialogue_id") or ""
    if d and (d not in dialogue_ids or (cfg("config/dialogue/%s.json" % d).get("name") != x.get("name"))):
        bad.append(iid + ":dialogue")
    s = x.get("shop_id") or ""
    if s and s not in shop_ids:
        bad.append(iid + ":shop")
report("npc_refs_and_fields", not bad, ";".join(bad[:8]))

missing_dialogue = [s for s in speakers
                    if not any(x.get("dialogue_id") == s for x in npcs)]
report("every_speaker_has_npc", not missing_dialogue, ",".join(missing_dialogue))
extra_dialogue = [x.get("id") for x in npcs
                  if x.get("dialogue_id") and x["dialogue_id"] not in speakers]
report("npc_dialogue_in_speakers", not extra_dialogue, ",".join(extra_dialogue))
no_npc = [r for r in region_ids if not any(x.get("region") == r for x in npcs)]
report("every_region_has_npc", not no_npc, ",".join(sorted(no_npc)))

shop_npcs = {s.get("npc_id") for s in shops["shops"]}
report("shop_npc_entities", shop_npcs <= set(ids), "缺=" + ",".join(sorted(shop_npcs - set(ids))))

# ---------------------------------------------------------------- 2. 怪物表
mons = monsters.get("monsters") or []
mids = [x.get("id") for x in mons]
report("monster_schema", monsters.get("schema") == "monster-table/1"
       and (monsters.get("version") or 0) >= 2,
       "%s v%s（B15 起 count / 主线 Boss 进表，版本不低于 2）" % (
           monsters.get("schema"), monsters.get("version")))
report("monster_ids", len(mids) == len(set(mids))
       and all(re.fullmatch(r"(mob|boss)_[a-z0-9_]+", i or "") for i in mids), "重复或形状不对")
report("monster_count_floor", len(mons) >= 12, "只有 %d 只" % len(mons))

badm = []
for x in mons:
    mid = x.get("id")
    tier = x.get("tier")
    if tier not in {"normal", "elite", "boss", "world_boss"}:
        badm.append(mid + ":tier")
    stats = x.get("stats") or {}
    if not (isinstance(stats.get("hp"), int) and stats["hp"] > 0
            and isinstance(stats.get("attack"), int) and stats["attack"] > 0
            and isinstance(stats.get("defense"), int) and stats["defense"] >= 0):
        badm.append(mid + ":stats")
    for sk in x.get("skills") or []:
        if sk not in skill_ids:
            badm.append(mid + ":skill")
    for d in x.get("drops") or []:
        if d.get("item_id") not in item_ids:
            badm.append(mid + ":drop")
        if not (isinstance(d.get("count"), int) and d["count"] > 0):
            badm.append(mid + ":count")
        if not (isinstance(d.get("chance_pct"), int) and 0 <= d["chance_pct"] <= 100):
            badm.append(mid + ":chance")
    spawn = x.get("spawn")
    if spawn:
        if spawn.get("region") not in region_ids:
            badm.append(mid + ":spawn_region")
        if not (isinstance(spawn.get("respawn_s"), int) and 1 <= spawn["respawn_s"] <= 86400):
            badm.append(mid + ":respawn")
        # B15：一个刷怪点就是一组怪，count 必填、1..20（与 MonsterTable.Load 同口径）。
        cnt = spawn.get("count")
        if not (isinstance(cnt, int) and 1 <= cnt <= 20):
            badm.append(mid + ":spawn_count")
    elif tier in ("normal", "elite"):
        badm.append(mid + ":spawn_missing")
report("monster_refs_and_fields", not badm, ";".join(sorted(set(badm))[:8]))

no_monster = [r for r in region_ids
              if not any((x.get("spawn") or {}).get("region") == r for x in mons)]
report("every_region_has_monster", not no_monster, ",".join(sorted(no_monster)))
bosses = [x.get("id") for x in mons if x.get("tier") == "boss"]
# 四个副本 Boss 是 K6 的交付；B15 之后会加内容 Boss，所以只钉「这四条在 + 至少四条」，
# 不把数量钉成等号（D5 口径）。
required_bosses = ("boss_abyss_warden", "boss_lair_matriarch",
                   "boss_sect_guardian", "boss_dragon_warden")
missing_boss = [b for b in required_bosses if b not in bosses]
report("dungeon_bosses", len(bosses) >= 4 and not missing_boss
       and len(bosses) == len(set(bosses)),
       "boss=%s missing=%s" % (",".join(bosses), ",".join(missing_boss)))
report("world_boss_not_duplicated", all(not (x.get("tier") == "world_boss") for x in mons),
       "世界 Boss 的数值只在 world_boss.json，不在怪物表写第二份")

# ------------------------------------------------- 2b. 副本 ↔ Boss 绑定（B15 批次一）
# 口径：dungeons.json 的 boss_id 只存引用，指向本表 tier=boss 的那一条；不搬数值。
# 五个副本（主线 / 深渊 / 巢穴 / 宗门秘境 / 龙巢）各一个，同一只 Boss 不许被两条副本引用。
# 元婴深渊是等境界开关的副本，首领留到那一段，不写 boss_id，这里不要求。
dungeons = cfg("config/dungeons.json")
by_id = {x.get("id"): x for x in mons}
bound = {d.get("id"): d.get("boss_id") for d in (dungeons.get("dungeons") or [])
         if d.get("boss_id")}
missing_bind = [d for d in ("main", "abyss", "lair", "sect", "dragon") if d not in bound]
report("dungeon_boss_binding", not missing_bind, "缺=" + ",".join(missing_bind))
bad_bind = [k for k, v in bound.items()
            if v not in by_id or by_id[v].get("tier") != "boss"]
report("dungeon_boss_targets", not bad_bind, ",".join(bad_bind))
values = list(bound.values())
dupes = sorted({v for v in values if values.count(v) > 1})
report("dungeon_boss_unique", not dupes, ",".join(dupes))

# 主线就是 battle.json 那只试炼石像：名字与血量必须一致（结构保证，不各写一套）。
main_boss = by_id.get(bound.get("main") or "") or {}
battle_boss = (cfg("config/battle.json").get("boss") or {})
report("main_boss_matches_battle",
       main_boss.get("name") == battle_boss.get("name")
       and (main_boss.get("stats") or {}).get("hp") == battle_boss.get("hp_max"),
       "怪物表 %s hp=%s / battle.json %s hp_max=%s" % (
           main_boss.get("name"), (main_boss.get("stats") or {}).get("hp"),
           battle_boss.get("name"), battle_boss.get("hp_max")))

# B15 批次一先铺的三块地表：每块至少一只精英，且至少有一个点是一组（count >= 2）。
group_regions = ("枯岚道", "承露门", "外环隙")
bad_group = []
for region in group_regions:
    got = [x for x in mons if (x.get("spawn") or {}).get("region") == region]
    if not any(x.get("tier") == "elite" for x in got):
        bad_group.append(region + ":elite")
    if not any((x.get("spawn") or {}).get("count", 1) >= 2 for x in got):
        bad_group.append(region + ":group")
report("region_content_groups", not bad_group, ",".join(bad_group))

# ---------------------------------------------------------------- 3. 地名与坐标
layout = cfg("tools/gen/ui_map_layout.json")
points = {row.get("id"): (row.get("x"), row.get("y")) for row in (layout.get("regions") or [])}
bad_region = [r for r in region_ids
              if r not in points
              or not (isinstance(points[r][0], (int, float)) and 0 <= points[r][0] <= 100
                      and isinstance(points[r][1], (int, float)) and 0 <= points[r][1] <= 100)]
report("region_map_points", not bad_region, ",".join(sorted(bad_region)))
report("region_files_cover_world", set(world.get("regions") or []) == region_ids,
       "world=%d files=%d" % (len(world.get("regions") or []), len(region_ids)))

# ---------------------------------------------------------------- 4. 客户端：生成物与手写代码
content = read("client/MMOGame/Assets/HotUpdate/Ui/UiContentTable.cs")
codex = read("client/MMOGame/Assets/HotUpdate/Ui/UiWorldCodex.cs")
report("generated_regions", content.count("new UiWorldCodex.Region") == len(regions),
       "生成物地表数不对")
report("generated_speakers", content.count("new UiWorldCodex.SpeakerMeta") == len(speakers),
       "生成物开口人数不对")
report("generated_npcs", content.count("new UiWorldCodex.NpcMeta") == len(npcs),
       "生成物 NPC 数不对")
report("codex_uses_generated",
       "UiContentTable.Regions" in codex and "UiContentTable.Speakers" in codex
       and "UiContentTable.Realms" in codex and "UiContentTable.FlightRealm" in codex,
       "UiWorldCodex 没有引用生成表")
meta = read("client/MMOGame/Assets/HotUpdate/Ui/UiContentTable.cs.meta")
report("generated_meta", re.search(r"^guid: [0-9a-f]{32}$", meta, re.M) is not None,
       "生成物缺 .meta / guid")

# 手写代码不许再出现内容初始化：Name = "某人" / Id = "某地" / Region = "某地"。
content_names = set(names) | region_ids
field_pat = re.compile(r'\b(Name|Id|Region|FirstRegion)\s*=\s*"([^"]*)"')
hand_hits = []
for base in ("client/MMOGame/Assets/HotUpdate", "client/MMOGame/Assets/Scripts"):
    for dirpath, _, files in os.walk(os.path.join(root, base)):
        for name in files:
            if not name.endswith(".cs") or name == "UiContentTable.cs" or name.endswith("SelfCheck.cs"):
                continue
            rel = os.path.relpath(os.path.join(dirpath, name), root)
            for index, line in enumerate(read(rel).splitlines(), 1):
                code = line.split("//")[0]
                for match in field_pat.finditer(code):
                    if match.group(2) in content_names:
                        hand_hits.append("%s:%d:%s" % (rel, index, match.group(0)))
report("client_no_hardcoded_names", not hand_hits, ";".join(hand_hits[:8]))

# 服务端运行时代码同样不许把 NPC / 地名写成字面量。
svc_hits = []
for base in ("src",):
    for dirpath, _, files in os.walk(os.path.join(root, base)):
        if "/bin" in dirpath or "/obj" in dirpath or ".Tests" in dirpath or "Generated" in dirpath:
            continue
        for name in files:
            if not name.endswith(".cs"):
                continue
            rel = os.path.relpath(os.path.join(dirpath, name), root)
            for index, line in enumerate(read(rel).splitlines(), 1):
                code = line.split("//")[0]
                for value in content_names:
                    if '"%s"' % value in code:
                        svc_hits.append("%s:%d:%s" % (rel, index, value))
report("server_no_hardcoded_names", not svc_hits, ";".join(svc_hits[:8]))

report("mainline_no_written_lists", "WrittenPlaces" not in read("src/Character/MainlineTable.cs")
       and "WrittenSpeakers" not in read("src/Character/MainlineTable.cs"),
       "MainlineTable 还写着地名 / 开口人名单")
report("world_loads_npc", "NpcTable.Load" in read("src/World/WorldProcess.cs"),
       "世界服没读 npc.json")
report("battle_loads_monster", "MonsterTable.Load" in read("src/Battle/Config.cs"),
       "战斗服没读 monsters.json")
report("probe_content_list", 'case "--content-list"' in read("tools/probe/CliProbe/CliParser.cs"),
       "probe 没有 --content-list")

ci = read("tools/check/ci.sh")
report("ci_registers_k6", re.search(r'^CHECKS="[\s\S]*\bk6\b', ci, re.M) is not None,
       "ci.sh 的 CHECKS 里没有 k6")
allsh = read("tools/check/check-all.sh")
report("check_all_registers_k6", re.search(r"^\s*k6\)", allsh, re.M) is not None,
       "check-all.sh 没挂 k6")

print("INFO npcs=%d monsters=%d regions=%d speakers=%d" %
      (len(npcs), len(mons), len(regions), len(speakers)))
print("PASS static_end")
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
    INFO) printf '  ..   %s\n' "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static.txt"
if ! grep -q '^PASS static_end' "$TMP/static.txt"; then
  bad "静态断言没跑完"; grep -vE '^(PASS|FAIL|INFO) ' "$TMP/static.txt" | tail -6 | detail
fi

echo "== 3. 客户端生成物与配置逐字一致 =="
GEN="$TMP/UiContentTable.cs"
if python3 tools/gen/gen-ui-content.py --out "$GEN" >"$TMP/gen.log" 2>&1 \
   && diff -q client/MMOGame/Assets/HotUpdate/Ui/UiContentTable.cs "$GEN" >/dev/null; then
  ok "生成器重跑与提交的 UiContentTable.cs 逐字一致"
else
  bad "客户端生成物过期，先跑 python3 tools/gen/gen-ui-content.py"
  diff -u client/MMOGame/Assets/HotUpdate/Ui/UiContentTable.cs "$GEN" 2>/dev/null | head -8 | detail
  tail -3 "$TMP/gen.log" | detail
fi

echo "== 4. 只读入口：两张内容表 =="
# 条数现取配置，不写死「当时的 11 / 12、版本 1」：B15 起怪物表会随内容分批长（v2 + 精英 + 主线 Boss）。
NPCS="$(python3 -c 'import json;print(len(json.load(open("config/npc.json"))["npcs"]))')"
MONS="$(python3 -c 'import json;print(len(json.load(open("config/monsters.json"))["monsters"]))')"
MVER="$(python3 -c 'import json;print(json.load(open("config/monsters.json"))["version"])')"
CL="$("$DOTNET" "$CHARACTER_DLL" --content-list 2>&1)"; clrc=$?
[ "$clrc" = "0" ] && grep -q "^content_table=ok npcs=$NPCS monsters=$MONS npc_version=1 monster_version=$MVER\$" <<< "$CL" \
  && ok "内容表读出 $NPCS 位 NPC / $MONS 只怪物（不连库）" \
  || bad "内容表只读入口不对（rc=${clrc}）：$(head -2 <<< "$CL" | tr '\n' ' ')"
CLU="$(printf '%s' "$CL" | utf8)"
grep -q '^npc=npc_qiao_xiaoman name=乔小满 region=枯岚道 role=quest faction=- dialogue=乔小满 shop=- pos=12,0,8 facing=180' <<< "$CLU" \
  && ok "NPC 行带位置 / 所属 / 对话引用" || bad "NPC 行不对：$(grep '^npc=npc_qiao' <<< "$CLU")"
# 这一行中间带中文（执事名与地名）：`.*` 在 UTF-8 locale 下跨不过 GBK 的非法字节，
# 必须用转码后的 CLU 再匹配（同 check-k1 的 LC_ALL=C 口径）。
grep -q '^npc=npc_shop_qiyun .* role=merchant .* shop=npc_general_qiyun ' <<< "$CLU" \
  && ok "NPC 商店引用读得到" || bad "商店 NPC 行不对：$(grep '^npc=npc_shop_qiyun' <<< "$CLU")"
grep -q '^monster=mob_kulan_beast name=枯岚凶兽 tier=normal level=5 .* spawn=枯岚道@20,0,30+60s' <<< "$CLU" \
  && ok "普通怪带刷怪点与掉落" || bad "怪物行不对：$(grep '^monster=mob_kulan_beast' <<< "$CLU")"
if [ "$(grep -c '^npc=' <<< "$CL")" = "$NPCS" ] && [ "$(grep -c '^monster=' <<< "$CL")" = "$MONS" ]; then
  ok "清单条数与表一致（${NPCS} / ${MONS}）"
else
  bad "清单条数不对：$(grep -c '^npc=' <<< "$CL") / $(grep -c '^monster=' <<< "$CL")"
fi

echo "== 5. 两个服务真启动 =="
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1:3]
w = json.load(open(os.path.join(root, "config/world.json"), encoding="utf-8"))
w["listen"] = "http://127.0.0.1:0"
json.dump(w, open(os.path.join(tmp, "world.json"), "w", encoding="utf-8"), ensure_ascii=False)
bdir = os.path.join(tmp, "battle-config")
os.makedirs(bdir)
for name in os.listdir(os.path.join(root, "config")):
    src = os.path.join(root, "config", name)
    if name.endswith(".json") and os.path.isfile(src):
        open(os.path.join(bdir, name), "w", encoding="utf-8").write(
            open(src, encoding="utf-8").read())
b = json.load(open(os.path.join(root, "config/battle.json"), encoding="utf-8"))
b["listen"] = "127.0.0.1:0"
b["settlement_dir"] = os.path.join(tmp, "set")
b["character_dir"] = os.path.join(tmp, "chr")
json.dump(b, open(os.path.join(bdir, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY

"$DOTNET" "$WORLD_DLL" --config "$TMP/world.json" > "$TMP/world.log" 2>&1 &
PIDS="$PIDS $!"
if wait_line "$TMP/world.log" 'npc_table='; then
  grep -q '^npc_table=ok version=1 npcs=11 with_dialogue=9 with_shop=2$' "$TMP/world.log" \
    && ok "世界服启动读到 11 位 NPC（9 位开口 + 2 位商店执事）" \
    || bad "世界服 NPC 行不对：$(grep '^npc_table=' "$TMP/world.log" | head -1)"
else
  bad "世界服没起来"; tail -6 "$TMP/world.log" | detail
fi

"$DOTNET" "$BATTLE_DLL" --config "$TMP/battle-config/battle.json" > "$TMP/battle.log" 2>&1 &
PIDS="$PIDS $!"
if wait_line "$TMP/battle.log" 'monster_table='; then
  grep -q "^monster_table=ok version=$MVER monsters=$MONS\$" "$TMP/battle.log" \
    && ok "战斗服启动读到 $MONS 只怪物（B15 批次一：8 块地表 + 3 只精英 + 5 个副本 Boss）" \
    || bad "战斗服怪物行不对：$(grep '^monster_table=' "$TMP/battle.log" | head -1)"
  # B15：启动日志逐条打出副本 → 内容 Boss 的绑定，绑定读不到就是起服期校验漏了。
  grep -q '^dungeon_boss=main boss_id=boss_main_trial$' "$TMP/battle.log" \
    && grep -q '^dungeon_boss=abyss boss_id=boss_abyss_warden$' "$TMP/battle.log" \
    && ok "副本 → Boss 绑定进启动日志（主线 / 深渊）" \
    || bad "副本 → Boss 绑定行缺失：$(grep '^dungeon_boss=' "$TMP/battle.log" | head -3 | tr '\n' ' ')"
else
  bad "战斗服没起来"; tail -6 "$TMP/battle.log" | detail
fi

echo "== 6. 坏配置都起不来 =="
newcase() {
  local name="$1"
  mkdir -p "$TMP/bad/$name"
  cp -R config "$TMP/bad/$name/config"
  printf '%s' "$TMP/bad/$name"
}
run_content() {
  ( cd "$1" && "$DOTNET" "$CHARACTER_DLL" --content-list 2>&1 )
}
# 战斗服的坏配置要真起一次进程：校验在监听之前跑，配错了立刻退（不监听就不等到超时）。
run_battle_bad() {
  local dir="$1" log="$1/battle.log" pid i=0
  ( cd "$dir" && "$DOTNET" "$BATTLE_DLL" --config "$dir/config/battle.json" > "$log" 2>&1 ) &
  pid=$!
  while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 40 ]; do sleep 0.25; i=$((i + 1)); done
  if kill -0 "$pid" 2>/dev/null; then
    kill -TERM "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
    echo "TIMEOUT 起服没有被配置校验挡住"
    return 0
  fi
  cat "$log"
}

mutate() {
  local case_dir="$1" file="$2" py="$3"
  ROOT="$ROOT" CASE="$case_dir" FILE="$file" CODE="$py" python3 - <<'PY'
import json, os
path = os.path.join(os.environ["CASE"], "config", os.environ["FILE"])
doc = json.load(open(path, encoding="utf-8"))
scope = {"d": doc, "npc": doc.get("npcs"), "mon": doc.get("monsters"),
         "dg": doc.get("dungeons")}
exec(os.environ["CODE"], scope)
json.dump(doc, open(path, "w", encoding="utf-8"), ensure_ascii=False)
PY
}

CASE="$(newcase npc_region)"
mutate "$CASE" npc.json 'npc[0]["region"] = "无名地"'
assert_bad_config "$(run_content "$CASE")" "NPC 所在地表不存在：起不来"

CASE="$(newcase npc_dialogue)"
mutate "$CASE" npc.json 'npc[0]["dialogue_id"] = "无此人"'
assert_bad_config "$(run_content "$CASE")" "NPC 对话引用不存在：起不来"

CASE="$(newcase npc_dup)"
mutate "$CASE" npc.json 'npc.append(dict(npc[0]))'
assert_bad_config "$(run_content "$CASE")" "NPC 编号重复：起不来"

CASE="$(newcase npc_shop)"
mutate "$CASE" npc.json 'npc[0]["shop_id"] = "无此店"'
assert_bad_config "$(run_content "$CASE")" "NPC 商店引用不存在：起不来"

CASE="$(newcase monster_skill)"
mutate "$CASE" monsters.json 'mon[0]["skills"] = [9999]'
assert_bad_config "$(run_content "$CASE")" "怪物技能不在技能表：起不来"

CASE="$(newcase monster_drop)"
mutate "$CASE" monsters.json 'mon[0]["drops"][0]["item_id"] = "no_such_item"'
assert_bad_config "$(run_content "$CASE")" "怪物掉落不在物品表：起不来"

CASE="$(newcase monster_spawn)"
mutate "$CASE" monsters.json 'mon[0]["spawn"]["region"] = "无名地"'
assert_bad_config "$(run_content "$CASE")" "怪物刷怪点地表不存在：起不来"

CASE="$(newcase monster_no_spawn)"
mutate "$CASE" monsters.json 'mon[0].pop("spawn")'
assert_bad_config "$(run_content "$CASE")" "普通怪没刷怪点：起不来"

# B15：副本 ↔ Boss 绑定写在战斗服启动期校验（角色服不读怪物表，不能在那里判）。
CASE="$(newcase boss_missing)"
mutate "$CASE" dungeons.json 'dg[0]["boss_id"] = "boss_nope"'
assert_bad_config "$(run_battle_bad "$CASE")" "boss_id 指向不存在的怪：战斗服起不来"

CASE="$(newcase boss_not_boss)"
mutate "$CASE" dungeons.json 'dg[0]["boss_id"] = "mob_kulan_beast"'
assert_bad_config "$(run_battle_bad "$CASE")" "boss_id 指向普通怪：战斗服起不来"

CASE="$(newcase boss_shared)"
mutate "$CASE" dungeons.json 'dg[1]["boss_id"] = dg[0]["boss_id"]'
assert_bad_config "$(run_battle_bad "$CASE")" "两条副本绑同一只 Boss：战斗服起不来"

CASE="$(newcase spawn_count_bad)"
mutate "$CASE" monsters.json 'mon[0]["spawn"]["count"] = 99'
assert_bad_config "$(run_content "$CASE")" "刷怪点一次 99 只：起不来"

echo "== 7. 新增 NPC / 怪物只改配置 =="
CASE="$(newcase add_content)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
base = os.path.join(os.environ["CASE"], "config")
npc = json.load(open(os.path.join(base, "npc.json"), encoding="utf-8"))
npc["npcs"].append({
    "id": "npc_k6_probe", "name": "核对用路人", "region": "枯岚道",
    "pos": {"x": 1, "y": 0, "z": 1}, "facing": 0, "faction": "",
    "role": "flavor", "dialogue_id": "", "shop_id": "", "note": "核对用，不进仓库"})
json.dump(npc, open(os.path.join(base, "npc.json"), "w", encoding="utf-8"), ensure_ascii=False)
mon = json.load(open(os.path.join(base, "monsters.json"), encoding="utf-8"))
mon["monsters"].append({
    "id": "mob_k6_probe", "name": "核对用怪", "level": 1, "tier": "normal",
    "stats": {"hp": 1, "attack": 1, "defense": 0}, "skills": [], "drops": [],
    "spawn": {"region": "枯岚道", "x": 0, "y": 0, "z": 0, "respawn_s": 60, "count": 1},
    "note": "核对用，不进仓库"})
json.dump(mon, open(os.path.join(base, "monsters.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
ADDED="$(run_content "$CASE")"
ADDEDU="$(printf '%s' "$ADDED" | utf8)"
if head -1 <<< "$ADDED" | grep -q "npcs=$((NPCS + 1)) monsters=$((MONS + 1))" \
   && grep -q '^npc=npc_k6_probe name=核对用路人 ' <<< "$ADDEDU" \
   && grep -q '^monster=mob_k6_probe name=核对用怪 ' <<< "$ADDEDU"; then
  ok "加一位 NPC + 一只怪只改配置：$NPCS/$MONS → $((NPCS + 1))/$((MONS + 1))"
else
  bad "新增内容没生效：$(head -2 <<< "$ADDEDU" | tr '\n' ' ')"
fi

finish "$TITLE"
