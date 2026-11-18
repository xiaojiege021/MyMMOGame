#!/usr/bin/env bash
# tools/check/check-q12.sh —— Q12 K3 角色名全局唯一 / 敏感词 / 改名（阶段 2 第十一张）。
#
# 完成标准（未覆盖排期第 61 行）：
#   跨账号重名被拒；长度与敏感词按配置；改名有事务号与费用；旧名不占用。
#   方案 12.1 追加第十九类「改名卡」（用户开卡时定，一张改一次名、商城 500 元宝）。
#
# 本脚本：
#   1. 编译角色服与网关；
#   2. 静态断言（规则正本、商城第十九类、登记表、三处接线、方案行数联动）；
#   3. 只读入口：规则事实值、长度 / 敏感词 / 归一判定、六类坏配置起不来；
#   4. 真库段（MMO_Q12_DB=1）：回填 → 造两个账号两个角色 → 改名（有卡 / 重放 /
#      重名 / 没卡）→ 旧名释放后能被别人用。真库只跑最终版，本地迭代别开这一段。
#
#   bash tools/check/check-q12.sh
#   MMO_Q12_DB=1 bash tools/check/check-q12.sh        # 有库的机器上跑第 4 段
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PB="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TITLE="Q12（K3 角色名全局唯一 / 敏感词 / 改名）"
TMP="$(mktemp -d)"
TMP="${TMP//\\//}"
cleanup() { rm -rf "$TMP" 2>/dev/null || true; }
trap cleanup EXIT
utf8() { case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) python3 -c 'import sys; sys.stdout.buffer.write(sys.stdin.buffer.read().decode("gbk","replace").encode("utf-8"))' ;; *) cat ;; esac; }
probe() { "$DOTNET" "$PB" --config config/character.json "$@" 2>&1 | utf8; }

echo "== 1. 编译 =="
if [ "${MMO_Q12_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_Q12_SKIP_BUILD=1）\n'
else
  BUILD_OK=1
  for proj in src/Character src/Gateway; do
    if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
      ok "编译 $proj"
    else
      BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build.log" | head -4 | detail
    fi
  done
  [ "$BUILD_OK" = "1" ] || { finish "$TITLE"; exit 1; }
fi

echo "== 2. 静态断言 =="
python3 "$ROOT/tools/check/check-q12-static.py" "$ROOT" >"$TMP/static.txt" 2>&1
S_PASS="$(grep -c '^PASS ' "$TMP/static.txt" 2>/dev/null || true)"
S_FAIL="$(grep -c '^FAIL ' "$TMP/static.txt" 2>/dev/null || true)"
S_PASS="${S_PASS:-0}"; S_FAIL="${S_FAIL:-0}"
if [ "$S_PASS" = "0" ] && [ "$S_FAIL" = "0" ]; then
  bad "静态段一行都没输出（脚本崩了）"; head -10 "$TMP/static.txt" | detail
else
  PASS=$((PASS + S_PASS)); FAIL=$((FAIL + S_FAIL))
  [ "$S_FAIL" = "0" ] && ok "静态断言 $S_PASS 条全过" \
    || { bad "静态断言 $S_FAIL 条红"; grep '^FAIL ' "$TMP/static.txt" | head -12 | detail; }
fi

echo "== 3. 只读入口：规则事实值与判定 =="
RULES="$(probe --name-rules)"
printf '%s' "$RULES" | grep -q '^name_rules=ok' \
  && ok "规则表读得出来：$(printf '%s' "$RULES" | grep '^name_rules=' | head -1)" \
  || { bad "规则表读不出来：$(printf '%s' "$RULES" | head -3 | detail)"; }

MIN="$(python3 -c 'import json;print(json.load(open("config/name_rules.json"))["min_length"])')"
MAX="$(python3 -c 'import json;print(json.load(open("config/name_rules.json"))["max_length"])')"
CARD="$(python3 -c 'import json;print(json.load(open("config/name_rules.json"))["rename_item_id"])')"
SHORT="$(python3 -c 'print("甲" * 1)')"
LONG="$(python3 -c "print('甲' * ($MAX + 1))")"
WORDS="$(python3 - "$CARD" <<'PY'
import json, sys
rules = json.load(open("config/name_rules.json", encoding="utf-8"))
for line in open(rules["banned_words_file"], encoding="utf-8"):
    line = line.strip()
    if line and not line.startswith("#"):
        print(line)
        break
PY
)"

OUT="$(probe --name-check "$SHORT")"
printf '%s' "$OUT" | grep -q 'reason=name_too_short' \
  && ok "1 个字被拒（min_length=${MIN} 从配置读）" \
  || bad "短名没被拒：$OUT"

OUT="$(probe --name-check "$LONG")"
printf '%s' "$OUT" | grep -q 'reason=name_too_long' \
  && ok "${MAX}+1 个字被拒（max_length=${MAX} 从配置读）" \
  || bad "超长没被拒：$OUT"

OUT="$(probe --name-check "测试${WORDS}测试")"
printf '%s' "$OUT" | grep -q 'reason=name_banned' \
  && ok "含敏感词表里的词被拒（词表单独一份，代码没有第二份）" \
  || bad "敏感词没挡住：$OUT"

OUT="$(probe --name-check "青衫客")"
printf '%s' "$OUT" | grep -q '^name_check=ok' \
  && ok "正常两字名通过：$OUT" \
  || bad "正常名字被误拒：$OUT"

# 归一：全角与半角落到同一个 name_key（判重口径用代码那一份）。
FULL="$(probe --name-check "ＡＢ")"
HALF="$(probe --name-check "ab")"
KF="$(printf '%s' "$FULL" | grep -oE 'name_key=[^ ]+' | head -1)"
KH="$(printf '%s' "$HALF" | grep -oE 'name_key=[^ ]+' | head -1)"
[ -n "$KF" ] && [ "$KF" = "$KH" ] && case "$KF" in *AB) NORM_OK=1 ;; *) NORM_OK=0 ;; esac || NORM_OK=0
[ "$NORM_OK" = "1" ] \
  && ok "全角「ＡＢ」与半角「ab」同一个 name_key（${KF}）" \
  || bad "归一没生效：全角 ${KF} 半角 ${KH}"

echo "== 4. 坏配置六类都起不来 =="
mk() { python3 - "$1" "$2" "$3" <<'PY'
import json, sys
path, key, value = sys.argv[1], sys.argv[2], sys.argv[3]
d = json.load(open("config/name_rules.json", encoding="utf-8"))
if value == "__DELETE__":
    d.pop(key, None)
elif value in ("true", "false"):
    d[key] = (value == "true")
elif value.lstrip("-").isdigit():
    d[key] = int(value)
else:
    d[key] = value
json.dump(d, open(path, "w", encoding="utf-8"), ensure_ascii=False)
PY
}
bad_case() {   # $1 = 说明，$2 = 规则文件
  local out
  out="$(probe --name-rules-path "$2" --name-rules)"
  if printf '%s' "$out" | grep -q '^config_error='; then
    ok "坏配置起不来：$1"
  else
    bad "坏配置没拦住：$1 → $(printf '%s' "$out" | head -2 | detail)"
  fi
}

mk "$TMP/r1.json" schema "name-rules/1"      ; bad_case "schema 还是 name-rules/1" "$TMP/r1.json"
# 两个数要一起改，mk 一次只动一个键——直接写这一份。
python3 - "$TMP/r2.json" <<'PY'
import json, sys
d = json.load(open("config/name_rules.json", encoding="utf-8"))
d["min_length"] = 8
d["max_length"] = 4
json.dump(d, open(sys.argv[1], "w", encoding="utf-8"), ensure_ascii=False)
PY
bad_case "min_length >= max_length" "$TMP/r2.json"
mk "$TMP/r3.json" rename_item_id "no_such_card"
bad_case "改名卡不在 items.json 里" "$TMP/r3.json"
mk "$TMP/r4.json" global_unique "false"
bad_case "global_unique=false（这一版只做全局唯一）" "$TMP/r4.json"
mk "$TMP/r5.json" banned_words_file "$TMP/none.txt"
bad_case "敏感词表不存在" "$TMP/r5.json"
printf '# 只有注释\n\n' > "$TMP/empty.txt"
mk "$TMP/r6.json" banned_words_file "$TMP/empty.txt"
bad_case "敏感词表一条词都没有" "$TMP/r6.json"

echo "== 5. 真库段：改名与旧名释放 =="
if [ "${MMO_Q12_DB:-0}" != "1" ]; then
  printf '       （跳过：没给 MMO_Q12_DB=1）\n'
  printf '       MMO_Q12_DB=1 bash tools/check/check-q12.sh\n'
  finish "$TITLE"
  exit $?
fi

# 5.1 先清掉本卡自己的测试账号，再造两个账号两个角色（固定名，重复跑不叠）。
cat > "$TMP/seed.sql" <<'SQL'
delete from "角色".character where account in ('q12a', 'q12b');
delete from "角色".account where account in ('q12a', 'q12b');
insert into "角色".account (account, password_hash, password_salt, iterations)
values ('q12a', '\x00'::bytea, '\x00'::bytea, 10000),
       ('q12b', '\x00'::bytea, '\x00'::bytea, 10000);
insert into "角色".character (account, name, race, class_id)
select v.account, v.name, v.race, v.class_id
  from (values ('q12a', 'Q12甲', 'chen', 'warrior'),
               ('q12b', 'Q12乙', 'chen', 'warrior')) as v(account, name, race, class_id)
 where not exists (select 1 from "角色".character c where c.name = v.name);
SQL
probe --migrate --sql "$TMP/seed.sql" > "$TMP/seed.log" 2>&1

# 5.2 回填：旧库升级补登记行，幂等（第二次跑只有 existing）。
BF1="$(probe --name-backfill)"
printf '%s' "$BF1" | grep -q '^name_backfill=ok' \
  && ok "回填登记表成功：$(printf '%s' "$BF1" | grep '^name_backfill=')" \
  || bad "回填失败：$(printf '%s' "$BF1" | head -3 | detail)"
BF2="$(probe --name-backfill)"
printf '%s' "$BF2" | grep -q '^name_backfill=ok' \
  && ok "回填是幂等的（第二次不重复插）：$(printf '%s' "$BF2" | grep '^name_backfill=')" \
  || bad "第二次回填不幂等：$(printf '%s' "$BF2" | head -3 | detail)"

REG="$(probe --name-registry --name-registry-limit 5000)"
ID_A="$(printf '%s' "$REG" | grep ' name=Q12甲 ' | grep -oE 'id=[0-9]+' | head -1 | cut -d= -f2)"
ID_B="$(printf '%s' "$REG" | grep ' name=Q12乙 ' | grep -oE 'id=[0-9]+' | head -1 | cut -d= -f2)"
if [ -n "$ID_A" ] && [ -n "$ID_B" ]; then
  ok "两个测试角色都进了登记表（id=${ID_A} / ${ID_B}）"
else
  bad "登记表里找不到测试角色：$(printf '%s' "$REG" | grep Q12 | head -2 | detail)"
  finish "$TITLE"; exit $?
fi

# 5.3 给 A 一张改名卡（B 一张都不给），验「有卡能改、没卡不能改」。
cat > "$TMP/card.sql" <<SQL
delete from "角色".inventory where character_id in ($ID_A, $ID_B) and item_id = '$CARD';
insert into "角色".inventory (character_id, slot, item_id, count, bound, bound_at)
values ($ID_A, 199, '$CARD', 1, true, now());
SQL
probe --migrate --sql "$TMP/card.sql" > "$TMP/card.log" 2>&1

OUT="$(probe --character-rename "$ID_B" "Q12丙" --rename-txn "q12:nocard")"
printf '%s' "$OUT" | grep -q 'reason=rename_card_missing' \
  && ok "没改名卡不能改（reason=rename_card_missing，名字不动）" \
  || bad "没卡也改了：$OUT"

OUT="$(probe --character-rename "$ID_A" "Q12丙" --rename-txn "q12:ok")"
printf '%s' "$OUT" | grep -q '^character_rename=ok' \
  && printf '%s' "$OUT" | grep -q 'count_left=0' \
  && ok "有卡改名成功，卡扣到 0：$(printf '%s' "$OUT" | grep '^character_rename=')" \
  || bad "改名失败或没扣卡：$OUT"
STATE="$(probe --name-state "$ID_A")"
printf '%s' "$STATE" | grep -q 'registered=Q12丙' \
  && ok "登记表换到新名（旧名那行已释放）" \
  || bad "登记表没换：$STATE"

OUT="$(probe --character-rename "$ID_A" "Q12丙" --rename-txn "q12:ok")"
printf '%s' "$OUT" | grep -q 'reason=duplicate' \
  && ok "同一个事务号重放回 duplicate（不二次扣卡）" \
  || bad "重放没判重：$OUT"

# 上一笔把卡用完了：先补一张，才走得到「重名」这条拒绝。
cat > "$TMP/carda.sql" <<SQL
insert into "角色".inventory (character_id, slot, item_id, count, bound, bound_at)
values ($ID_A, 198, '$CARD', 1, true, now());
SQL
probe --migrate --sql "$TMP/carda.sql" > "$TMP/carda.log" 2>&1
OUT="$(probe --character-rename "$ID_A" "Q12乙" --rename-txn "q12:taken")"
printf '%s' "$OUT" | grep -q 'reason=name_taken' \
  && ok "改成别人（另一个账号）占着的名字被拒（reason=name_taken）" \
  || bad "跨账号重名没挡住：$OUT"

# 5.4 旧名不占用：B 现在可以拿 A 用过的名字。
cat > "$TMP/cardb.sql" <<SQL
insert into "角色".inventory (character_id, slot, item_id, count, bound, bound_at)
values ($ID_B, 199, '$CARD', 1, true, now());
SQL
probe --migrate --sql "$TMP/cardb.sql" > "$TMP/cardb.log" 2>&1
OUT="$(probe --character-rename "$ID_B" "Q12甲" --rename-txn "q12:reuse")"
printf '%s' "$OUT" | grep -q '^character_rename=ok' \
  && ok "旧名释放后能被别人用（B 改成了 A 用过的「Q12甲」）" \
  || bad "旧名没释放：$OUT"

STATE="$(probe --name-state "$ID_B")"
printf '%s' "$STATE" | grep -q 'registered=Q12甲' \
  && ok "B 的登记行是「Q12甲」：$(printf '%s' "$STATE" | grep '^name_state=')" \
  || bad "B 的登记不对：$STATE"

# 5.5 收尾：把测试账号删掉，别留在库里。
cat > "$TMP/clean.sql" <<'SQL'
delete from "角色".character where account in ('q12a', 'q12b');
delete from "角色".account where account in ('q12a', 'q12b');
SQL
probe --migrate --sql "$TMP/clean.sql" > "$TMP/clean.log" 2>&1
ok "测试账号已清理（q12a / q12b）"

finish "$TITLE"
