#!/usr/bin/env bash
# tools/check/check-k7.sh —— K7 配置与库的边界（开发架构 14.7 顺序 4）。
#
# 完成标准（14.7 的 K7 行，逐条对应下面的断言）：
#   1. 技术架构 6.1 成文：静态定义 config、可变状态 DB、进程内临时态；
#   2. city.json 去掉 owner_sect / tax_pct，DB 默认 null / 0；
#   3. season.current 留 config 作运营开关；
#   4. 加一条「库里的状态不回写 config」检查。
#
# 口径：
#   - 运行期字段黑名单按「键名精确匹配」：owner_sect / tax_pct / sold_today /
#     stock_now / bought_today / claimed / current_owner。siege_territory.json 的
#     tax_pct_on_capture 是规则不是运行时事实，不误伤。
#   - 两份 DDL（db/social/city.sql 与主迁移里的同一段）都要有默认值；
#     ALTER ... SET DEFAULT 0 是给老库的幂等补丁（CREATE IF NOT EXISTS 不改既有列）。
#   - 「不回写 config」两层证据：静态上服务端没有向 config/ 写的语句；
#     运行上跑只读入口与整链启动前后，config/ 所有文件的哈希不变。
#
#   bash tools/check/check-k7.sh
#   MMO_K7_SKIP_BUILD=1 bash tools/check/check-k7.sh   跳过编译
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
# Windows 上 dotnet 重定向输出走 GBK：城池行中间带地名，`.*` 在 UTF-8 locale 下
# 跨不过非法字节，断言前先按平台转码（同 check-k1 的 LC_ALL=C 口径）。
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
TMP="$(mktemp -d)"
TITLE="K7（server）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_K7_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_K7_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/Character/Character.csproj -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "角色服编译通过"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言：成文、city.json、DDL 默认值 =="
ROOT="$ROOT" python3 - > "$TMP/static.txt" 2>&1 <<'PY'
import json, os, re

root = os.environ["ROOT"]

def read(p):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server"}
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
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def cfg(p):
    return json.loads(read(p))

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

# ---------------------------------------------------------------- 1. 技术架构 6.1 成文
arch = read("docs/技术架构.md")
report("arch_boundary_sentence",
       "静态定义在配置，可变状态在库，临时状态在进程内存" in arch,
       "6.1 缺一句话边界")
report("arch_city_rule",
       "city.json" in arch and "owner_sect" in arch and "tax_pct" in arch
       and "库默认" in arch,
       "6.1 没写 city.json 只留静态列与库默认值")
report("arch_season_current", "season.current" in arch and "运营开关" in arch,
       "6.1 没写 season.current 留配置")
report("arch_k7_landing", "K7 落地（v1.165）" in arch and "check-k7.sh" in arch,
       "6.1 没写 K7 的落地口径")

# ---------------------------------------------------------------- 2. 配置里没有运行期字段
RUNTIME_KEYS = {"owner_sect", "tax_pct", "sold_today", "stock_now",
                "bought_today", "claimed", "current_owner"}
hits = []
for base, _, files in os.walk(os.path.join(root, "config")):
    for name in sorted(files):
        if not name.endswith(".json"):
            continue
        path = os.path.join(base, name)
        rel = os.path.relpath(path, root)
        try:
            doc = json.load(open(path, encoding="utf-8"))
        except Exception as ex:
            report("config_json_%s" % rel, False, str(ex))
            continue

        def walk(node, where):
            if isinstance(node, dict):
                for key, value in node.items():
                    if key in RUNTIME_KEYS:
                        hits.append("%s:%s" % (rel, where + "." + key))
                    walk(value, where + "." + key)
            elif isinstance(node, list):
                for index, value in enumerate(node):
                    walk(value, where + "[%d]" % index)

        walk(doc, "")
report("config_no_runtime_fields", not hits, ";".join(hits[:8]))

city = cfg("config/city.json")
cities = city.get("cities") or []
report("city_schema_v2", city.get("schema") == "city-table/1" and city.get("version") == 2,
       "schema=%s version=%s" % (city.get("schema"), city.get("version")))
report("city_static_keys_only",
       bool(cities) and all(set(c) == {"city_id", "name", "vein_id"} for c in cities),
       "cities 键=%s" % [sorted(c) for c in cities[:2]])
report("city_note_documents_boundary",
       "K7" in (city.get("note") or "") and "owner_sect" in (city.get("note") or ""),
       "city.json 的 note 没写归属 / 税率在库")

# ---------------------------------------------------------------- 3. DDL 默认值
DDL = ["db/social/city.sql", "db/character/character.sql"]
for rel in DDL:
    text = read(rel)
    report("ddl_owner_default_null_%s" % rel,
           re.search(r"owner_sect\s+text\s+DEFAULT NULL", text) is not None,
           "owner_sect 没写 DEFAULT NULL")
    report("ddl_tax_default_zero_%s" % rel,
           re.search(r"tax_pct\s+int\s+NOT NULL DEFAULT 0", text) is not None,
           "tax_pct 没写 NOT NULL DEFAULT 0")
    report("ddl_alter_default_%s" % rel,
           "ALTER TABLE \"社交\".city ALTER COLUMN tax_pct SET DEFAULT 0;" in text,
           "老库补默认值的 ALTER 不在")

# ---------------------------------------------------------------- 4. 代码：只读配置、只写库
city_cs = read("src/Character/CityTable.cs")
report("citytable_rejects_runtime",
       "RuntimeKeys" in city_cs and "运行期字段" in city_cs
       and '"owner_sect"' in city_cs and '"tax_pct"' in city_cs,
       "CityTable 没拒绝运行期字段")
report("citytable_no_runtime_def",
       ".TaxPct" not in city_cs and ".OwnerSect" not in city_cs,
       "CityDef 还带着税率 / 归属")

# C# 源文件里的 SQL 写作 \"社交\"；比对前把转义还原成普通引号。
proc = read("src/Character/CharacterProcess.cs").replace('\\"', '"')
report("seed_static_columns_only",
       'insert into "社交".city (city_id, name, vein_id)' in proc
       and "city.TaxPct" not in proc and "city.OwnerSect" not in proc,
       "启动补城池还在从配置写归属 / 税率")
report("startup_marks_db_owner",
       "owner_sect=db" in proc and "tax_pct=db" in proc,
       "启动输出没标出归属 / 税率来自库")
report("probe_marks_db_owner",
       "owner_sect=db" in read("tools/probe/CliProbe/ConfigTableCli.cs"),
       "--city-table 输出没标出归属 / 税率来自库")

tax_cs = read("src/Character/CityTaxRepository.cs").replace('\\"', '"')
report("tax_read_from_db",
       'select tax_pct from "社交".city' in tax_cs,
       "任务发放没从库读税率")
siege_cs = read("src/Character/SiegeTerritoryRepository.cs").replace('\\"', '"')
report("owner_write_db_only",
       'update "社交".city set owner_sect = $2, tax_pct = $3' in siege_cs,
       "城战没把归属 / 税率写进库")

# 服务端不许向 config/ 写：写语句同一行出现 config 就算可疑。
write_suspects = []
for base, _, files in os.walk(os.path.join(root, "src")):
    if "/bin" in base or "/obj" in base or ".Tests" in base:
        continue
    for name in files:
        if not name.endswith(".cs"):
            continue
        rel = os.path.relpath(os.path.join(base, name), root)
        for line in read(rel).splitlines():
            if line.lstrip().startswith("//"):
                continue
            if any(api in line for api in ("File.WriteAll", "File.AppendAll", "StreamWriter(")) \
                    and "config" in line.lower():
                write_suspects.append("%s:%s" % (rel, line.strip()[:80]))
report("code_no_config_writeback", not write_suspects, ";".join(write_suspects[:6]))
report("selfcheck_writes_temp_only",
       "Path.GetTempPath()" in read("src/Global/ConfigSelfCheck.cs"),
       "ConfigSelfCheck 没写到临时目录")

# ---------------------------------------------------------------- 5. season.current 留 config
season = cfg("config/season.json")
season_ids = [s.get("id") for s in season.get("seasons") or []]
report("season_current_in_config",
       season.get("current") in season_ids,
       "season.json 的 current 对不上 seasons")
report("season_current_documented",
       "season.current" in read("docs/配置设计.md") or "season.current" in arch,
       "season.current 的口径没写进文档")

ci = read("tools/check/ci.sh")
report("ci_registers_k7", re.search(r'^CHECKS="[\s\S]*\bk7\b', ci, re.M) is not None,
       "ci.sh 的 CHECKS 里没有 k7")
allsh = read("tools/check/check-all.sh")
report("check_all_registers_k7", re.search(r"^\s*k7\)", allsh, re.M) is not None,
       "check-all.sh 没挂 k7")

print("INFO runtime_keys=%d cities=%d ddl_files=%d" % (len(RUNTIME_KEYS), len(cities), len(DDL)))
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

echo "== 3. 只读入口与整链启动 =="
CITY_OUT="$("$DOTNET" "$CHARACTER_DLL" --city-table 2>&1)"; crc=$?
[ "$crc" = "0" ] && grep -q 'city_table_state=ok version=2 ' <<< "$CITY_OUT" \
  && ok "城池表 v2 读出（不连库）" \
  || bad "城池表只读入口不对（rc=${crc}）：$(head -2 <<< "$CITY_OUT" | tr '\n' ' ')"
CITY_U="$(printf '%s' "$CITY_OUT" | utf8)"
grep -q '^city=qiyun .*vein=spirit_qiyun_1 owner_sect=db tax_pct=db' <<< "$CITY_U" \
  && ok "配置只回静态三列；归属 / 税率标 db" || bad "城池行不对：$(grep '^city=' <<< "$CITY_U")"
if grep -qE 'tax_pct=[0-9]' <<< "$CITY_OUT"; then
  bad "配置输出里还带着税率数字"
else
  ok "配置输出里没有税率数字（只在库）"
fi

# 「不回写 config」：只读入口与整链启动前后，config/ 所有文件哈希不变。
# 用 python 算哈希：CI（ubuntu）与 macOS 都有 python3，不依赖 shasum / sha256sum 的差异。
config_hash() {
  python3 - "$ROOT" <<'PY'
import hashlib, os, sys
root = os.path.join(sys.argv[1], "config")
for dirpath, dirnames, filenames in os.walk(root):
    dirnames.sort()
    for name in sorted(filenames):
        path = os.path.join(dirpath, name)
        print(hashlib.sha256(open(path, "rb").read()).hexdigest(),
              os.path.relpath(path, root))
PY
}
BEFORE="$(config_hash)"
HASHED="$(printf '%s\n' "$BEFORE" | grep -c . || true)"
[ "$HASHED" -gt 40 ] && ok "config/ 下 ${HASHED} 个文件参与哈希" \
  || bad "config/ 哈希文件太少（${HASHED}）"

STARTUP="$TMP/startup.log"
"$DOTNET" "$CHARACTER_DLL" --config config/character.json > "$STARTUP" 2>&1 &
START_PID=$!
for _ in $(seq 1 300); do
  grep -qE '^config_error=|^db_missing=1|Now listening on' "$STARTUP" 2>/dev/null && break
  sleep 0.1
done
stop_pid "$START_PID"

AFTER="$(config_hash)"
if [ "$BEFORE" = "$AFTER" ]; then
  ok "启动与只读入口没改 config/ 的任何一个文件（哈希不变）"
else
  bad "config/ 被写回了：$(diff <(printf '%s' "$BEFORE") <(printf '%s' "$AFTER") | head -4 | tr '\n' ' ')"
fi
if grep -q '^config_error=' "$STARTUP"; then
  bad "真配置整链加载失败：$(head -1 "$STARTUP")"
elif grep -q '^city=qiyun .*owner_sect=db tax_pct=db' "$STARTUP"; then
  ok "整链启动通过（城池只输出静态列）"
else
  bad "启动没读到城池：$(grep '^city=' "$STARTUP" | head -1)"
fi

echo "== 4. 库状态不许回写配置：坏配置都起不来 =="
newcase() {
  local name="$1"
  mkdir -p "$TMP/bad/$name"
  cp -R config "$TMP/bad/$name/config"
  printf '%s' "$TMP/bad/$name"
}
run_city() {
  local case_dir="$1"
  ( cd "$case_dir" && "$DOTNET" "$CHARACTER_DLL" --city-table 2>&1 )
}

CASE="$(newcase city_tax)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/city.json")
d = json.load(open(p, encoding="utf-8"))
d["cities"][0]["tax_pct"] = 0
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
assert_bad_config "$(run_city "$CASE")" "city.json 写 tax_pct（哪怕 0）：起不来"

CASE="$(newcase city_owner)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/city.json")
d = json.load(open(p, encoding="utf-8"))
d["cities"][0]["owner_sect"] = "sect-1"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
assert_bad_config "$(run_city "$CASE")" "city.json 写 owner_sect：起不来"

CASE="$(newcase top_owner)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/city.json")
d = json.load(open(p, encoding="utf-8"))
d["owner_sect"] = "sect-1"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
assert_bad_config "$(run_city "$CASE")" "city.json 顶层写 owner_sect：起不来"

CASE="$(newcase stock_now)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/city.json")
d = json.load(open(p, encoding="utf-8"))
d["cities"][0]["stock_now"] = 3
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
assert_bad_config "$(run_city "$CASE")" "city.json 写 stock_now：起不来"

finish "$TITLE"
