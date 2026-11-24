#!/usr/bin/env bash
# tools/check/check-d8.sh —— D8 迁移版本表（开发架构 14.6 顺序 4）。
#
# 完成标准（14.6 的 D8 行）：
#   schema_version + 按序迁移；新库一条命令建全；老库可重复执行。
#
# 本脚本分两段：
#   1. 无库段：db/migrations.json 与 35 份 SQL 一一对应、版本连续、主迁移自包含；
#      运行器有 schema_version / 校验和 / 建议锁；CLI 入口在；文档与 CI 挂上。
#   2. 真库段（MMO_D8_HAS_DB=1 且 MMO_GAMEDB 才跑）：
#      - MMO_D8_NEW_DB=1：库里三个 schema 都是空的，验「一条命令建全」；
#      - MMO_D8_OLD_DB=1：库里有表但没有 schema_version，验「老库补建版本表 + 重复跑」；
#      - 都不给：对现成的库验第二遍只 skip。
#      逐条核：第一次 applied / reapplied 数、第二次 applied=0、版本表行数与最新版、
#      表的数量下限、--schema-status 只读、--sql 老路径不回退、坏清单与 checksum 漂移被挡。
#
#   bash tools/check/check-d8.sh
#   MMO_D8_HAS_DB=1 MMO_D8_NEW_DB=1 MMO_GAMEDB='...' bash tools/check/check-d8.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/tools/check/lib-common.sh"

TITLE="D8（迁移版本表）"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

field() { printf '%s' "$1" | sed -n "s/.* $2=\([^ ]*\).*/\1/p"; }
line_of() { printf '%s\n' "$1" | grep -F "$2" | head -1; }

echo "== 1. 编译 =="
if [ "${MMO_D8_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_D8_SKIP_BUILD=1）\n'
elif "$DOTNET" build tools/probe/CliProbe/CliProbe.csproj -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "probe 编译通过"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 无库段：清单、主迁移、运行器、入口、文档、护栏 =="
python3 - >"$TMP/static.txt" 2>&1 <<'PY'
import glob
import json
import os
import re
import sys


def read(path):
    try:
        return open(path, encoding="utf-8").read()
    except Exception:
        return ""


def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + str(info)))


def names(text, schema):
    return set(re.findall(r'CREATE TABLE IF NOT EXISTS\s+"' + schema + r'"\.([a-z_]+)', text))


try:
    man = json.loads(read("db/migrations.json"))
except Exception as ex:
    print("FAIL manifest_json  " + str(ex))
    sys.exit(0)

report("manifest_schema", man.get("schema") == "migration-manifest/1", man.get("schema"))
report("manifest_version", man.get("version") == 1, man.get("version"))
schemas = man.get("schemas") or []
keys = [s.get("key") for s in schemas]
report("schema_order", keys == ["character", "social", "trade"], keys)
report("schema_names", all(s.get("name") for s in schemas), keys)

entries = {}
versions_ok = True
ids = set()
files = []
for schema in schemas:
    key = schema.get("key", "")
    rows = schema.get("entries") or []
    entries[key] = rows
    for index, entry in enumerate(rows):
        if entry.get("version") != index + 1:
            versions_ok = False
        ids.add(key + "/" + str(entry.get("version")))
        files.append(entry.get("file", ""))

report("versions_contiguous", versions_ok)
total_entries = sum(len(rows) for rows in entries.values())
report("ids_unique", len(ids) == total_entries, "%d/%d" % (len(ids), total_entries))
missing = [f for f in files if not os.path.exists(f)]
report("files_exist", not missing, missing[:3])
report("files_unique", len(files) == len(set(files)), len(files) - len(set(files)))
report("files_under_db", all(re.match(r"^db/(character|social|trade)/[^/]+\.sql$", f) for f in files))

# Windows 上 glob 把目录分隔符给成反斜杠，跟迁移清单里的正斜杠永远对不上，先归一。
on_disk = sorted(p.replace(os.sep, "/") for p in
                 glob.glob("db/character/*.sql") + glob.glob("db/social/*.sql") + glob.glob("db/trade/*.sql"))
report("every_sql_registered", set(on_disk) == set(files), sorted(set(on_disk) ^ set(files))[:5])

reapply = [entry["file"] for rows in entries.values() for entry in rows if entry.get("reapply")]


def is_main(path):
    parts = path.split("/")
    return parts[-1] == "schema.sql" or parts[-1] == parts[1] + ".sql"


report("reapply_only_main_migrations", all(is_main(f) for f in reapply), reapply)
report("character_main_reapply", "db/character/character.sql" in reapply)
report("social_main_reapply", "db/social/social.sql" in reapply)

char_agg = read("db/character/character.sql")
soc_agg = read("db/social/social.sql")
all_char, all_soc = set(), set()
for path in on_disk:
    text = read(path)
    all_char |= names(text, "角色")
    all_soc |= names(text, "社交")
report("character_aggregate_complete", all_char <= names(char_agg, "角色"),
       sorted(all_char - names(char_agg, "角色"))[:5])
report("social_aggregate_complete", all_soc <= names(soc_agg, "社交"),
       sorted(all_soc - names(soc_agg, "社交"))[:5])
report("character_aggregate_not_empty", len(names(char_agg, "角色")) >= 20, len(names(char_agg, "角色")))

flags = read("tools/probe/CliProbe/CliFlags.cs")
report("flag_migration_knobs",
       all(x in flags for x in ("sqlGiven", "migrationsPath", "schemaFilter", "schemaStatus")))
parser = read("tools/probe/CliProbe/CliParser.cs")
report("parser_migration_flags",
       all(x in parser for x in ('"--migrations"', '"--schema"', '"--schema-status"', "sqlGiven = true")))
core = read("tools/probe/CliProbe/CoreCli.cs")
report("core_versioned_and_legacy",
       "MigrationCli.RunAsync" in core and "sqlGiven" in core and "schemaStatus" in core)
runner = read("tools/probe/CliProbe/MigrationCli.cs")
report("version_table_columns",
       all(x in runner for x in ("schema_version", "checksum", "reapply", "applied_at", "updated_at")))
report("migration_guards",
       "pg_advisory_lock" in runner and "drift" in runner and "on conflict (id) do update" in runner)
report("migration_summary_line", "migrate=ok schema=" in runner)

arch = read("docs/技术架构.md")
report("doc_technical_migration",
       "db/migrations.json" in arch and "schema_version" in arch and "db-migrate.sh" in arch)
dev = read("docs/开发架构.md")
report("doc_dev_d8_row", "db/migrations.json" in dev and "schema_version" in dev)
queue = read("docs/任务队列.md")
report("queue_row_done",
       re.search(r"\| 18 \| 1 \| D8 \| 迁移版本表 \|[^\n]*\| 完成 \|", queue) is not None)
checklist = read("docs/跑偏与缺失核对表.md")
report("checklist_d8_row", re.search(r"\| D8 \| 没有迁移版本表", checklist) is not None)
log = read("docs/开发日志.md")
report("log_d8_entry", re.search(r"^### D8 · v1\.177", log, re.M) is not None)
report("wrapper_exists", os.path.exists("tools/env/db-migrate.sh"))

ci = read("tools/check/ci.sh")
report("ci_registered", re.search(r"^CHECKS=.*\bd8\b", ci, re.M) is not None)
checkall = read("tools/check/check-all.sh")
report("checkall_registered",
       re.search(r"d8\)\s*echo", checkall) is not None
       and re.search(r'ALL_CARDS="[^"]*\bd8\b', checkall, re.S) is not None)
PY

if grep -q '^PASS\|^FAIL' "$TMP/static.txt"; then
  while IFS= read -r line; do
    case "$line" in
      PASS\ *) ok "${line#PASS }" ;;
      FAIL\ *) bad "${line#FAIL }" ;;
    esac
  done < <(grep '^PASS\|^FAIL' "$TMP/static.txt")
else
  bad "静态段没有输出结果"
  grep -i 'error\|traceback' "$TMP/static.txt" | head -5 | detail
fi

echo "== 3. 真库段 =="
if [ "${MMO_D8_HAS_DB:-0}" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  echo "（跳过真库段：MMO_D8_HAS_DB=1 且配置 MMO_GAMEDB 才跑）"
  finish "$TITLE"
fi

read -r TOTAL ONCE REAPPLY CHAR_TABLES SOCIAL_TABLES TRADE_TABLES <<EOF
$(python3 - <<'PY'
import glob
import json
import re

man = json.loads(open("db/migrations.json", encoding="utf-8").read())
total = once = reapply = 0
for schema in man["schemas"]:
    for entry in schema["entries"]:
        total += 1
        if entry.get("reapply"):
            reapply += 1
        else:
            once += 1


def tables(path, schema_name):
    text = open(path, encoding="utf-8").read()
    return len(set(re.findall(r'CREATE TABLE IF NOT EXISTS\s+"' + schema_name + r'"\.([a-z_]+)', text)))


print(total, once, reapply,
      tables("db/character/character.sql", "角色"),
      tables("db/social/social.sql", "社交"),
      len(set(re.findall(r'CREATE TABLE IF NOT EXISTS\s+"交易"\.([a-z_]+)',
                         "".join(open(p, encoding="utf-8").read()
                                 for p in glob.glob("db/trade/*.sql"))))))
PY
)
EOF

echo "       清单：$TOTAL 条（一次 $ONCE / 重跑 $REAPPLY）；表下限 角色 $CHAR_TABLES / 社交 $SOCIAL_TABLES / 交易 $TRADE_TABLES"

run() { OUT="$("$DOTNET" "$PROBE_DLL" "$@" 2>&1)"; CODE=$?; }

MODE="reuse"
[ "${MMO_D8_NEW_DB:-0}" = "1" ] && MODE="new"
[ "${MMO_D8_OLD_DB:-0}" = "1" ] && MODE="old"

echo "-- 3.1 迁移前 --schema-status（只读）"
run --schema-status
[ "$CODE" = "0" ] && ok "迁移前 --schema-status 退出 0" || { bad "迁移前 --schema-status 退出 $CODE"; printf '%s\n' "$OUT" | tail -5 | detail; }
for one in "角色:$CHAR_TABLES" "社交:$SOCIAL_TABLES" "交易:$TRADE_TABLES"; do
  name="${one%%:*}"; floor="${one##*:}"
  status_line="$(printf '%s\n' "$OUT" | grep -F "schema=" | grep -F "$name" | head -1)"
  case "$MODE" in
    new)
      printf '%s' "$status_line" | grep -q 'schema_status=missing' \
        && ok "新库: $name 还没有 schema_version" \
        || bad "新库: $name 迁移前就有版本表：$status_line"
      tables="$(field "$status_line" tables)"
      [ "${tables:-1}" = "0" ] && ok "新库: $name 里没有表" || bad "新库: $name 迁移前有 $tables 张表"
      ;;
    old)
      printf '%s' "$status_line" | grep -q 'schema_status=missing' \
        && ok "老库: $name 还没有 schema_version" \
        || bad "老库: $name 迁移前已有版本表：$status_line"
      tables="$(field "$status_line" tables)"
      if [ "$name" = "角色" ]; then
        # 老库只保证「有表、缺版本表」：它可能停在只跑过主迁移的那一版，
        # 缺的 inventory / currency / enhance 等正是这次迁移要补上的。
        if [ "${tables:-0}" -gt 0 ] 2>/dev/null; then
          ok "老库: 角色 schema 里已有 ${tables} 张表、缺的只是版本表"
        else
          bad "老库: 角色 schema 里一张表都没有，不像老库"
        fi
      fi
      ;;
    *)
      ;;
  esac
done

echo "-- 3.2 第一次 --migrate（按清单建全）"
run --migrate
[ "$CODE" = "0" ] && ok "第一次迁移退出 0" || { bad "第一次迁移退出 $CODE"; printf '%s\n' "$OUT" | tail -8 | detail; }
printf '%s\n' "$OUT" | grep -q '^migrate=ok schema=all' && ok "打印 migrate=ok schema=all" || bad "第一次迁移没有 migrate=ok 行"
printf '%s\n' "$OUT" | grep -q 'migration_manifest=ok' && ok "清单读进来" || bad "清单没有 migration_manifest=ok 行"
[ "$(field "$(line_of "$OUT" 'migrate=ok')" total)" = "$TOTAL" ] && ok "清单条数 $TOTAL 全走到" \
  || bad "清单条数不对：$(line_of "$OUT" 'migrate=ok')"
if [ "$MODE" = "new" ] || [ "$MODE" = "old" ]; then
  # 首跑没有旧的版本行：35 条都算「第一次记录」，主迁移也先记一次；
  # 「重跑」从第二遍起才计（第二遍 applied=0、skipped=$ONCE、reapplied=$REAPPLY）。
  FIRST="$(line_of "$OUT" 'migrate=ok')"
  [ "$(field "$FIRST" applied)" = "$TOTAL" ] && ok "首跑把 $TOTAL 条版本各记一次" \
    || bad "首跑 applied 不对：$FIRST"
  [ "$(field "$FIRST" reapplied)" = "0" ] && ok "首跑没有重跑（版本表还空）" \
    || bad "首跑 reapplied 不对：$FIRST"
fi

echo "-- 3.3 第二次 --migrate（老库可重复执行）"
run --migrate
[ "$CODE" = "0" ] && ok "第二次迁移退出 0" || { bad "第二次迁移退出 $CODE"; printf '%s\n' "$OUT" | tail -8 | detail; }
SECOND="$(line_of "$OUT" 'migrate=ok')"
[ "$(field "$SECOND" applied)" = "0" ] && ok "第二次没有新增版本（applied=0）" || bad "第二次仍在 apply：$SECOND"
[ "$(field "$SECOND" skipped)" = "$ONCE" ] && ok "第二次跳过 $ONCE 条已应用版本" || bad "skipped 不对：$SECOND"
[ "$(field "$SECOND" reapplied)" = "$REAPPLY" ] && ok "第二次重跑主迁移 $REAPPLY 条（幂等）" || bad "reapplied 不对：$SECOND"
printf '%s\n' "$OUT" | grep -q '^migrate_error=' && bad "第二次出现 migrate_error" || ok "第二次没有 migrate_error"

echo "-- 3.4 --schema-status：版本表行数与表下限"
run --schema-status
[ "$CODE" = "0" ] && ok "--schema-status 退出 0" || bad "--schema-status 退出 $CODE"
status_line_of() { printf '%s\n' "$OUT" | grep "^schema_status=ok schema=$1 " | head -1; }
for one in "角色:$CHAR_TABLES" "社交:$SOCIAL_TABLES" "交易:$TRADE_TABLES"; do
  name="${one%%:*}"; floor="${one##*:}"
  expected="$(python3 -c 'import json,sys
man=json.load(open("db/migrations.json"))
for s in man["schemas"]:
    if s["name"]==sys.argv[1]: print(len(s["entries"]))' "$name")"
  line="$(status_line_of "$name")"
  [ -n "$line" ] && ok "$name 有 schema_status=ok 行" || { bad "$name 没有 schema_status=ok 行"; continue; }
  [ "$(field "$line" rows)" = "$expected" ] && ok "$name 版本表 $expected 行" || bad "$name 版本行数不对：$line"
  [ "$(field "$line" latest_version)" = "$expected" ] && ok "$name 最新版本 $expected" || bad "$name 最新版本不对：$line"
  assert_floor "$(field "$line" tables)" "$floor" "$name 表数量够了"
done

echo "-- 3.5 --sql 老路径与按 schema 迁移"
run --migrate --sql db/character/account.sql
[ "$CODE" = "0" ] && printf '%s\n' "$OUT" | grep -q '^migrate=ok sql=db/character/account.sql' \
  && ok "--sql 单文件路径仍是 migrate=ok sql=..." || bad "--sql 单文件路径回退：$OUT"
run --migrate --schema social
[ "$CODE" = "0" ] && printf '%s\n' "$OUT" | grep -q '^migrate=ok schema=social' \
  && ok "--schema social 只跑社交 schema" || bad "--schema social 不对：$(printf '%s\n' "$OUT" | tail -4)"
run --migrate --schema trade
[ "$CODE" = "0" ] && printf '%s\n' "$OUT" | grep -q '^migrate=ok schema=trade' \
  && ok "--schema trade 能补交易 schema（角色表已在）" || bad "--schema trade 不对：$(printf '%s\n' "$OUT" | tail -4)"

echo "-- 3.6 坏清单与 checksum 漂移"
write_bad() { python3 - "$1" "$2" <<'PY'
import json
import sys

kind = sys.argv[2]
base = {"schema": "migration-manifest/1", "version": 1,
        "schemas": [{"key": "character", "name": "角色", "entries": [
            {"version": 1, "file": "db/character/schema.sql", "reapply": True}]}]}
entries = base["schemas"][0]["entries"]
if kind == "gap":
    base["schemas"][0]["entries"] = [entries[0], {"version": 3, "file": "db/character/account.sql"}]
elif kind == "missing":
    base["schemas"][0]["entries"] = [{"version": 1, "file": "db/character/nope.sql"}]
elif kind == "duplicate":
    base["schemas"][0]["entries"] = [entries[0], {"version": 2, "file": entries[0]["file"]}]
elif kind == "reapply":
    entries[0]["reapply"] = "yes"
json.dump(base, open(sys.argv[1], "w"), ensure_ascii=False)
PY
}
for kind in gap missing duplicate reapply; do
  write_bad "$TMP/bad-$kind.json" "$kind"
  run --migrate --migrations "$TMP/bad-$kind.json"
  if [ "$CODE" != "0" ] && printf '%s\n' "$OUT" | grep -q '^config_error='; then
    ok "坏清单 $kind 被 config_error 挡住（退出 $CODE）"
  else
    bad "坏清单 $kind 没挡住：退出 $CODE $(printf '%s\n' "$OUT" | tail -2)"
  fi
done

python3 - "$TMP/drift.json" <<'PY'
import json
import sys
json.dump({"schema": "migration-manifest/1", "version": 1,
           "schemas": [{"key": "character", "name": "角色", "entries": [
               {"version": 1, "file": "db/character/account.sql"}]}]},
          open(sys.argv[1], "w"), ensure_ascii=False)
PY
run --migrate --migrations "$TMP/drift.json"
if [ "$CODE" = "6" ] && printf '%s\n' "$OUT" | grep -q '^migrate_error=drift'; then
  ok "已应用版本的 sha256 变了会报 drift（退出 6）"
else
  bad "checksum 漂移没挡住：退出 $CODE $(printf '%s\n' "$OUT" | tail -2)"
fi

run --migrate --schema nope
if [ "$CODE" = "2" ] && printf '%s\n' "$OUT" | grep -q '^config_error='; then
  ok "未知 --schema 被 config_error 挡住"
else
  bad "未知 --schema 没挡住：退出 $CODE"
fi

finish "$TITLE"
