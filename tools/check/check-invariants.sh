#!/usr/bin/env bash
# tools/check/check-invariants.sh —— D17 不变量核对（开发架构 14.11.4）。
#
# 完成标准（14.11.4）：
#   ① docs/不变量清单.md 至少 8 条，逐条有编号 / 判定 / 原因 / 先看哪里；
#   ② probe --invariants 只读入口存在，干净库上违反数 0；
#   ③ 故意造一条违反能被抓住（临时库），退出非 0，且核对器不写库、不自愈；
#   ④ 风险含 2 的卡收口口径写进 14.11.5，并至少在一张已收口卡上补跑一次；
#   ⑤ 只读：不给任何表加列、不改配置、不动已有表数据。
#
# 两段：
#   bash tools/check/check-invariants.sh                        无库段（进 ci.sh）
#   MMO_INVARIANTS_HAS_DB=1 bash tools/check/check-invariants.sh  真库段（在 ubu 本机跑）
#
# 真库段默认自己建临时库；pg_hba 只放行 gamedb / 用户没有 CREATEDB 时，
# 用 MMO_INVARIANTS_DB=<已准备好的干净库> 跑（脚本会往里迁移、造违反，故必须是一次性的库）。
# MMO_INVARIANTS_DROP_DB=1 才会在收尾删掉外部库；自动建的库一律删。
#
# 真库段不碰现成的 gamedb：自己建一个临时库、按 db/migrations.json 建全，
# 先验 0 违反，再删一行 schema_version 造违反，验它被抓住且第二次跑仍红。
# 临时库跑完就 drop；现成库里的测试残留不会被这个脚本改一字。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/tools/check/lib-common.sh"

TITLE="D17（不变量核对）"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TMP="$(mktemp -d)"
TEMP_DB=""
OWN_DB=0
cleanup_db() {
  if [ "$OWN_DB" = "1" ] && [ -n "$TEMP_DB" ] && [ -f "$PROBE_DLL" ]; then
    printf 'drop database if exists "%s" with (force);\n' "$TEMP_DB" > "$TMP/drop.sql" 2>/dev/null || true
    "$DOTNET" "$PROBE_DLL" --db postgres --migrate --sql "$TMP/drop.sql" >/dev/null 2>&1 || true
  fi
}
trap 'cleanup_db; rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_INVARIANTS_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_INVARIANTS_SKIP_BUILD=1）\n'
elif "$DOTNET" build tools/probe/CliProbe/CliProbe.csproj -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "probe 编译通过"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 无库段：清单、编号、只读入口、护栏 =="
python3 - >"$TMP/static.txt" 2>&1 <<'PY'
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


doc = read("docs/不变量清单.md")
report("doc_exists", bool(doc))
rows = [line for line in doc.splitlines() if re.match(r"\|\s*INV-\d{2}\s*\|", line)]
ids = [re.match(r"\|\s*(INV-\d{2})\s*\|", line).group(1) for line in rows]
report("doc_at_least_8", len(ids) >= 8, len(ids))
report("doc_ids_unique", len(ids) == len(set(ids)), len(ids) - len(set(ids)))
report("doc_ids_contiguous", ids == ["INV-%02d" % i for i in range(1, len(ids) + 1)], ids[:3])
report("doc_each_row_five_cells",
       all(len([c for c in line.strip().strip("|").split("|")]) == 5 for line in rows),
       [line[:24] for line in rows if len(line.strip().strip("|").split("|")) != 5][:3])
report("doc_each_row_has_select", all(re.search(r"\bselect\b", line, re.I) for line in rows))
report("doc_says_read_only", "只读" in doc)
report("doc_says_zero", "违反数必须为 0" in doc)
report("doc_names_probe_entry", "--invariants" in doc and "check-invariants.sh" in doc)

probe = read("tools/probe/CliProbe/InvariantCli.cs")
probe_ids = re.findall(r'new\("(INV-\d{2})"', probe)
report("probe_ids_match_doc", probe_ids == ids, "%s vs %s" % (probe_ids[:3], ids[:3]))
report("probe_read_only_transaction",
       "set transaction read only" in probe and "BeginTransactionAsync" in probe)
report("probe_reports_read_only", "transaction_read_only" in probe)
report("probe_exit_violations", "ExitViolations = 7" in probe)
report("probe_savepoint_per_check", "savepoint inv" in probe and "rollback to savepoint inv" in probe)
report("probe_schema_version_manifest", "MigrationManifest.Load" in probe
       and "schema_version" in probe)
exec_lines = [line.strip() for line in probe.splitlines() if "await ExecAsync(" in line]
allowed = ("set transaction read only", "savepoint inv", "release savepoint inv")
report("probe_exec_only_tx_control",
       bool(exec_lines) and all(any(a in line for a in allowed) for line in exec_lines),
       [line for line in exec_lines if not any(a in line for a in allowed)][:3])

parser = read("tools/probe/CliProbe/CliParser.cs")
report("parser_invariants_flag", 'case "--invariants"' in parser)
report("parser_db_override", 'case "--db"' in parser)
flags = read("tools/probe/CliProbe/CliFlags.cs")
report("flags_invariants", re.search(r"public static bool invariants\b", flags) is not None)
report("flags_db_override", "dbOverride" in flags)
dbcli = read("tools/probe/CliProbe/DbCli.cs")
report("db_dispatch_invariants", "InvariantCli.TryRunDbAsync" in dbcli)
report("db_anyflag_invariants", "invariants || dbOverride" in dbcli)
report("db_applies_override", "Database = dbOverride" in dbcli)

ci = read("tools/check/ci.sh")
report("ci_registered", re.search(r"^CHECKS=.*\binvariants\b", ci, re.M) is not None)
allsh = read("tools/check/check-all.sh")
report("checkall_deps", re.search(r"invariants\)\s*echo", allsh) is not None)
report("checkall_cards", re.search(r'ALL_CARDS="[^"]*\binvariants\b', allsh, re.S) is not None)

arch = read("docs/开发架构.md")
report("arch_doc_and_script", "docs/不变量清单.md" in arch and "check-invariants.sh" in arch)
report("arch_close_rule", "--invariants" in arch and "风险含 `2`" in arch)
report("arch_close_record", "收口记录（v1.182）" in arch)
queue = read("docs/任务队列.md")
report("queue_row_d17", re.search(r"\| 19b \| 1 \| D17 \|[^\n]*\| (进行中|完成) \|", queue) is not None)
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
if [ "${MMO_INVARIANTS_HAS_DB:-0}" != "1" ]; then
  echo "（跳过真库段：MMO_INVARIANTS_HAS_DB=1 才跑；CI 只跑无库段）"
  finish "$TITLE"
fi

run() { OUT="$("$DOTNET" "$PROBE_DLL" "$@" 2>&1)"; CODE=$?; }
line_of() { printf '%s\n' "$OUT" | grep -F "$2" | head -1; }
field() { printf '%s' "$1" | sed -n "s/.* $2=\([^ ]*\).*/\1/p"; }

echo "-- 3.1 准备临时库（不碰现成 gamedb）"
if [ -n "${MMO_INVARIANTS_DB:-}" ]; then
  TEMP_DB="$MMO_INVARIANTS_DB"
  echo "       使用 MMO_INVARIANTS_DB=${TEMP_DB}（一次性库；脚本会往里迁移并造一条违反）"
  ok "外部临时库 ${TEMP_DB}"
else
  TEMP_DB="mmo_d17_inv_$$"
  printf 'create database "%s";\n' "$TEMP_DB" >"$TMP/create.sql"
  run --db postgres --migrate --sql "$TMP/create.sql"
  if [ "$CODE" = "0" ] && printf '%s\n' "$OUT" | grep -q '^migrate=ok sql='; then
    OWN_DB=1
    ok "临时库 ${TEMP_DB} 已建（失败时先看 pg_hba 是否放行 postgres / 用户有没有 CREATEDB）"
  else
    bad "临时库建不出来：退出 ${CODE}；要么在 ubu 本机放宽 pg_hba，要么用 MMO_INVARIANTS_DB 指一个一次性库"
    printf '%s\n' "$OUT" | tail -4 | detail
    finish "$TITLE"
  fi
fi

echo "-- 3.2 按迁移清单建全（干净库基线）"
run --db "$TEMP_DB" --migrate
[ "$CODE" = "0" ] && printf '%s\n' "$OUT" | grep -q '^migrate=ok schema=all' \
  && ok "临时库按 db/migrations.json 建全" \
  || { bad "临时库迁移失败：退出 ${CODE}"; printf '%s\n' "$OUT" | tail -6 | detail; }

echo "-- 3.3 干净库：每条不变量 0 违反"
run --db "$TEMP_DB" --invariants
CLEAN_OUT="$OUT"
[ "$CODE" = "0" ] && ok "干净库 --invariants 退出 0" \
  || { bad "干净库 --invariants 退出 ${CODE}（有违反或报错）"; printf '%s\n' "$OUT" | grep '^invariant=fail' | head -5 | detail; }
printf '%s\n' "$OUT" | grep -q '^invariants_read_only=on' \
  && ok "只读事务已生效（transaction_read_only=on）" \
  || bad "没有打印 invariants_read_only=on"
CLEAN_TOTAL="$(grep -c '^invariant=' <<<"$OUT")"
[ "$CLEAN_TOTAL" -ge 8 ] 2>/dev/null && ok "跑了 ${CLEAN_TOTAL} 条不变量（≥ 8）" \
  || bad "不变量条数不足：${CLEAN_TOTAL}"
printf '%s\n' "$OUT" | grep -q '^invariants=ok total=[0-9]* ok=[0-9]* violations=0$' \
  && ok "末行 invariants=ok violations=0" \
  || bad "末行不是 invariants=ok violations=0：$(line_of "$OUT" 'invariants=')"
printf '%s\n' "$OUT" | grep -q '^invariant=fail' \
  && bad "干净库上仍有 invariant=fail" || ok "干净库上没有任何 invariant=fail"

echo "-- 3.4 故意造一条违反：删一行 schema_version"
printf 'delete from "角色".schema_version where version = 1;\n' >"$TMP/violate.sql"
run --db "$TEMP_DB" --migrate --sql "$TMP/violate.sql"
[ "$CODE" = "0" ] && ok "删掉 角色/0001 这一行" \
  || { bad "造违反失败：退出 ${CODE}"; printf '%s\n' "$OUT" | tail -4 | detail; }

run --db "$TEMP_DB" --invariants
DIRTY_OUT="$OUT"
[ "$CODE" = "7" ] && ok "造违反后退出 7（不是 0，也不是 5）" \
  || bad "造违反后退出 ${CODE}，期望 7"
printf '%s\n' "$OUT" | grep -q '^invariant=fail id=INV-15 ' \
  && ok "抓的是 INV-15 schema_version_matches_manifest" \
  || bad "INV-15 没有红：$(printf '%s\n' "$OUT" | grep '^invariant=' | tail -3)"
printf '%s\n' "$OUT" | grep -q 'missing=character/0001' \
  && ok "sample 指到缺的那一行 character/0001" \
  || bad "sample 没写清缺哪一行"
DIRTY_VIOLATIONS="$(printf '%s\n' "$OUT" | sed -n 's/^invariants=fail total=[0-9]* ok=[0-9]* violations=\([0-9]*\)$/\1/p')"
[ "${DIRTY_VIOLATIONS:-0}" -ge 1 ] 2>/dev/null && ok "违反数 ${DIRTY_VIOLATIONS} ≥ 1" \
  || bad "违反数不对：${DIRTY_VIOLATIONS:-空}"

echo "-- 3.5 再跑一次：违反还在、输出逐字一致（核对器不写库、不自愈）"
run --db "$TEMP_DB" --invariants
[ "$CODE" = "7" ] && ok "第二次仍然退出 7" || bad "第二次退出 ${CODE}，期望仍是 7"
SECOND_VIOLATIONS="$(printf '%s\n' "$OUT" | sed -n 's/^invariants=fail total=[0-9]* ok=[0-9]* violations=\([0-9]*\)$/\1/p')"
[ "$SECOND_VIOLATIONS" = "$DIRTY_VIOLATIONS" ] && ok "违反数仍是 ${SECOND_VIOLATIONS}（没有把那一行补回去）" \
  || bad "两次违反数不同：${DIRTY_VIOLATIONS} → ${SECOND_VIOLATIONS}"
if [ "$OUT" = "$DIRTY_OUT" ]; then
  ok "两次输出逐字一致，只读入口没有写库"
else
  bad "两次输出不一致：核对器可能在写库"
  diff <(printf '%s\n' "$DIRTY_OUT") <(printf '%s\n' "$OUT") | head -6 | detail
fi
printf '%s\n' "$OUT" | grep -q '^invariants_read_only=on' \
  && ok "造违反那一轮也仍在只读事务里" || bad "造违反那一轮不是只读事务"

echo "-- 3.6 收尾：自动建的临时库删掉；外部库按 MMO_INVARIANTS_DROP_DB 决定"
if [ "$OWN_DB" = "1" ] || [ "${MMO_INVARIANTS_DROP_DB:-0}" = "1" ]; then
  printf 'drop database if exists "%s" with (force);\n' "$TEMP_DB" >"$TMP/drop.sql"
  run --db postgres --migrate --sql "$TMP/drop.sql"
  [ "$CODE" = "0" ] && ok "临时库 ${TEMP_DB} 已删" \
    || { bad "临时库没删掉：退出 ${CODE}"; printf '%s\n' "$OUT" | tail -4 | detail; }
  TEMP_DB=""
  OWN_DB=0
else
  printf '  ..   ${TEMP_DB} 是一次性库，留给人工删除（要脚本删就加 MMO_INVARIANTS_DROP_DB=1）\n'
fi

finish "$TITLE"
