#!/usr/bin/env bash
# tools/check/check-c6.sh —— C6/D12 抽 Shared.Domain（开发架构 14.6 / 14.8）。
#
# 完成标准（14.6 的 C6 行、14.8 的 D12 行）：
#   DungeonTable / DungeonDropTable / WorldBossTable 一份代码；
#   角色服与战斗服引用同一份；Realm / Drill 不再只在一边；配置加载有单测。
#
# 本脚本核对四段：
#   1. 单一来源：三张表与 DungeonDef 只在 src/Shared.Domain 各有一份定义，
#      旧路径已删，三个服与 probe 都引用同一个工程；共享工程不反向引用服务。
#   2. 字段与调用：DungeonDef 带 Realm / Drill；两服都调同一个 Load；
#      世界服传已开放地表、战斗服只算窗口。
#   3. 领域单测：src/Shared.Domain.Tests 覆盖三张表，src/Battle.Tests（D6）覆盖
#      四段伤害与配置校验；两个工程都跑到全绿，都不连库。
#   4. 跨进程：probe / 战斗服 / 世界服读同一份正本，同一时刻算出同一把场次钥匙。
#
#   bash tools/check/check-c6.sh
#   MMO_C6_SKIP_BUILD=1 bash tools/check/check-c6.sh   只做静态断言与单测（增量）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
WD="$ROOT/src/World/bin/Release/net10.0/World.dll"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
# Windows 上 dotnet 重定向输出走 GBK：副本那行带境界名（元婴），断言前先按平台转码。
utf8() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      python3 -c 'import sys; sys.stdout.buffer.write(
          sys.stdin.buffer.read().decode("gbk", "replace").encode("utf-8"))' ;;
    *) cat ;;
  esac
}
AT=1763380800000   # 2025-11-17T12:00:00Z = 本地 20:00，落在 w2 窗口内；只验纯计算，不连库。
TMP="$(mktemp -d)"
TITLE="C6（Shared.Domain）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_C6_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_C6_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/MMOGame.sln -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "服务端 sln 编译通过（含 Shared.Domain 与单测工程）"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言 =="
ROOT="$ROOT" python3 - > "$TMP/static.txt" 2>&1 <<'PY'
import glob, os, re

root = os.environ["ROOT"]
def rd(p):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server", "CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World", "SectRepository.cs": "Sect", "SocialGrpcService.cs": "Grpc", "WorldGrpcService.cs": "Grpc", "Program.cs": "Cli"}
    _name = __import__("os").path.basename(p)
    if _name in _sub:
        # 14.8 拆分卡之后：主文件 + 分片目录一起读（断言文本不变，只改读法）。
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
    full = os.path.join(root, p)
    return open(full, encoding="utf-8").read() if os.path.exists(full) else ""
def sources():
    out = {}
    for d in ("src", "tools/probe"):
        for p in glob.glob(os.path.join(root, d, "**", "*.cs"), recursive=True):
            # Windows 上 glob 与 relpath 都给反斜杠：先归一成正斜杠再筛 obj/bin、再当键，
            # 否则过滤不生效、下面按 "src/Shared.Domain/xxx.cs" 比对也永远不等。
            rel = os.path.relpath(p, root).replace(os.sep, "/")
            if "/obj/" in rel or "/bin/" in rel:
                continue
            out[rel] = open(p, encoding="utf-8").read()
    return out
def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

src = sources()
def defs(pattern):
    hits = []
    for p, text in src.items():
        if re.search(pattern, text, re.M):
            hits.append(p)
    return hits

for key, pattern, expected in (
    ("DungeonTable", r"^public sealed class DungeonTable\b", "DungeonTable.cs"),
    ("DungeonDef", r"^public sealed record DungeonDef\b", "DungeonTable.cs"),
    ("DungeonDropTable", r"^public sealed class DungeonDropTable\b", "DungeonDropTable.cs"),
    ("DropLineDef", r"^public sealed record DropLineDef\b", "DungeonDropTable.cs"),
    ("WorldBossTable", r"^public sealed class WorldBossTable\b", "WorldBossTable.cs"),
):
    hits = defs(pattern)
    report("%s 只有一份定义" % key, hits == ["src/Shared.Domain/" + expected], str(hits))

for old in ("src/Battle/DungeonTable.cs", "src/Battle/DungeonDropTable.cs",
            "src/Battle/WorldBossTable.cs", "src/Character/DungeonTable.cs",
            "src/Character/DungeonDropTable.cs", "src/World/WorldBossTable.cs"):
    report("%s 已删" % old, not os.path.exists(os.path.join(root, old)))

shared = rd("src/Shared.Domain/Shared.Domain.csproj")
report("Shared.Domain 不反向引用服务工程",
       "ProjectReference" not in shared and "PackageReference" not in shared)
for proj in ("src/Battle/Battle.csproj", "src/Character/Character.csproj",
             "src/World/World.csproj", "tools/probe/CliProbe/CliProbe.csproj"):
    text = rd(proj)
    report("%s 引用 Shared.Domain" % proj, "Shared.Domain/Shared.Domain.csproj" in text)

dt = rd("src/Shared.Domain/DungeonTable.cs")
report("DungeonDef 带 Realm", "string? Realm = null" in dt)
report("DungeonDef 带 Drill", "long Drill = 0" in dt)
report("两服共用 DungeonTable.Load",
       "DungeonTable.Load(config.DungeonTable)" in rd("src/Character/CharacterProcess.cs")
       and "DungeonTable.Load(dungeonPath)" in rd("src/Battle/Config.cs"))

dd = rd("src/Shared.Domain/DungeonDropTable.cs")
report("掉落表一份加载器、两服共用",
       "DungeonDropTable.LoadIfPresent" in rd("src/Battle/Config.cs")
       and "DungeonDropTable.LoadIfPresent(" in rd("src/Character/CharacterProcess.cs"))
report("角色服校验物品 / 币种、战斗服只读结构",
       "items.All.Select" in rd("src/Character/CharacterProcess.cs")
       and "DungeonDropTable.LoadIfPresent(dungeonDropPath)," in rd("src/Battle/Config.cs"))

wt = rd("src/Shared.Domain/WorldBossTable.cs")
report("世界 Boss 表一份解析、开放地表按参数校验",
       "Load(string path, IReadOnlySet<string>? openRegions = null)" in wt
       and "WorldBossTable.Load(path, open)" in rd("src/World/WorldProcess.cs")
       and "WorldBossTable.Load(worldBossPath)" in rd("src/Battle/Config.cs"))

tests = rd("src/Shared.Domain.Tests/ConfigLoadingTests.cs")
report("单测覆盖三张表", all(k in tests for k in ("DungeonTable", "DungeonDropTable", "WorldBossTable")))
report("单测用例 >= 10", tests.count("[Fact]") >= 10, str(tests.count("[Fact]")))
report("单测引用 Shared.Domain",
       "Shared.Domain/Shared.Domain.csproj" in rd("src/Shared.Domain.Tests/Shared.Domain.Tests.csproj"))

# D6 起并进本卡的同一段：伤害（DamagePipeline）与校验（EffectTable / BattleConfig）的单测。
d6dmg = rd("src/Battle.Tests/DamagePipelineTests.cs")
d6cfg = rd("src/Battle.Tests/ConfigValidationTests.cs")
d6proj = rd("src/Battle.Tests/Battle.Tests.csproj")
report("伤害单测覆盖四段 / 恒等回退 / 减免与夹取 / 取整",
       all(k in d6dmg for k in ("DamagePipeline.Compute", "EffectTable.Missing",
                                "DefenseMitigation", "PenetrationCapPct", "Rounded")))
report("校验单测覆盖效果表与战斗配置",
       all(k in d6cfg for k in ("EffectTable.Load", "BattleConfig.Load", "ConfigException")))
report("领域单测用例 >= 20", d6dmg.count("[Fact]") + d6cfg.count("[Fact]") >= 20,
       str(d6dmg.count("[Fact]") + d6cfg.count("[Fact]")))
report("Battle.Tests 只引 Battle、不带库驱动",
       "../Battle/Battle.csproj" in d6proj
       and "Npgsql" not in d6proj and "GAMEDB" not in d6dmg + d6cfg)
report("单测工程在 sln 里（--no-build 才跑得到）", "Battle.Tests" in rd("src/MMOGame.sln"))
_chk = [l for l in rd("tools/check/ci.sh").splitlines() if l.startswith("CHECKS=")]
report("ci.sh 的 CHECKS 名单里有 c6（单测随 CI 跑）",
       bool(_chk) and " c6 " in _chk[0] + " ")
PY
while IFS= read -r line; do
  case "$line" in
    PASS\ *) ok "${line#PASS }" ;;
    FAIL\ *) bad "${line#FAIL }" ;;
    *) printf '%s\n' "$line" | detail ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 领域单测（配置加载 / 伤害 / 校验）=="
# D6：两个工程都跑，任一个红都算这张卡红。条数不钉，只打印实际通过数。
run_tests() {
  local proj="$1" label="$2" log="$TMP/test-$2.log" count
  if "$DOTNET" test "$proj" -c Release --no-build --nologo -v q >"$log" 2>&1; then
    count="$(grep -oE '(通过|Passed): *[0-9]+' "$log" | tail -1 | grep -oE '[0-9]+' || true)"
    ok "${label} 单测全绿（${count:-?} 项）"
  else
    bad "${label} 单测有红项"; grep -E '失败|Failed|error' "$log" | head -8 | detail
  fi
}
run_tests src/Shared.Domain.Tests/Shared.Domain.Tests.csproj "Shared.Domain"
run_tests src/Battle.Tests/Battle.Tests.csproj "Battle"

echo "== 4. 跨进程同源 =="
DTU="$("$DOTNET" "$CD" --dungeon-table --config config/character.json 2>&1 | utf8)"
printf '%s' "$DTU" | grep -q '^dungeon_table_state=ok' \
  && printf '%s' "$DTU" | grep -q '^dungeon=abyss_yuanying .*realm=元婴 drill=200' \
  && ok "probe 用共享副本表：元婴副本 realm=元婴 drill=200" \
  || bad "共享副本表读不出来：$(printf '%s' "$DTU" | head -3)"

# B2 起掉落表按品阶补了装备行，行数现取，不钉 2。
WB_LINES="$(python3 -c 'import json
d=json.load(open("config/dungeon_drops.json",encoding="utf-8"))
e=next(x for x in d["entries"] if x["dungeon_id"]=="world_boss")
print(len(e["lines"]))' | tr -d '\r')"
DD="$("$DOTNET" "$CD" --dungeon-drop-table-path config/dungeon_drops.json \
      --config config/character.json 2>&1)"
printf '%s' "$DD" | grep -q "^dungeon_drop=world_boss lines=${WB_LINES}" \
  && ok "probe 用共享掉落表：world_boss ${WB_LINES} 行" \
  || bad "共享掉落表读不出来：$(printf '%s' "$DD" | head -3)"

BOUT="$("$DOTNET" "$BD" --world-boss-table --config config/battle.json --at "$AT" 2>&1)"
WOUT="$("$DOTNET" "$WD" --world-boss-state --config config/world.json --at "$AT" 2>&1)"
BKEY="$(printf '%s' "$BOUT" | grep -oE 'key=[^ ]+' | head -1)"
WKEY="$(printf '%s' "$WOUT" | grep -oE 'key=[^ ]+' | head -1)"
BV="$(printf '%s' "$BOUT" | grep -oE 'world_boss_table_version=[0-9]+' | head -1)"
WV="$(printf '%s' "$WOUT" | grep -oE 'world_boss_table=ok version=[0-9]+' | head -1 | grep -oE '[0-9]+$')"
# Q11 起窗口按日历时区解释，这场落在哪个窗口不写死：只要两端现算的钥匙一样、
# 形状对（入口-本地日期-窗口编号）就算同源。
if [ -n "$BKEY" ] && [ "$BKEY" = "$WKEY" ] \
   && printf '%s' "$BKEY" | grep -qE '^key=world_boss_1-[0-9]{8}-w[0-9]+$'; then
  ok "战斗服 / 世界服同一时刻算出同一把钥匙：$BKEY"
else
  bad "两进程钥匙不一致：battle=$BKEY world=$WKEY"
fi
if [ -n "$BV" ] && [ "${BV##*=}" = "$WV" ]; then
  ok "战斗服 / 世界服读到同一个表版本：${BV##*=}"
else
  bad "两进程表版本不一致：battle=$BV world=$WV"
fi
printf '%s' "$BOUT" | grep -q 'cap=20' && printf '%s' "$WOUT" | grep -q 'cap=20' \
  && ok "战斗服 / 世界服人数上限都只来自配置（cap=20）" \
  || bad "人数上限没有从同一份配置读出来"

finish "$TITLE"
