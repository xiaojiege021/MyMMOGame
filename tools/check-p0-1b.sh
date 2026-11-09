#!/usr/bin/env bash
# P0-1b 核对：一场单人 Boss，一次只结算一个技能。
#
# 对着 P0-1 的完成标准跑：
#   1. 连续发出两个还在冷却中的技能，只有第一次生效
#   2. 打断后这次施法没有伤害
#   3. 战斗进程重启后，这场没有写入的奖励不会出现
#
# 另外确认：距离不够、目标不对、技能编号不认识、前摇没走完、后摇没走完，
# 这几种都拒绝，而且拒绝时目标生命一点不动。
#
# 用法：tools/check-p0-1b.sh
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
PROBE_DLL="$ROOT/tools/BattleProbe/bin/Release/net10.0/BattleProbe.dll"

PASS=0
FAIL=0
TMP="$(mktemp -d)"
BATTLE_PID=""

# 让目录「能列、不能建」。Windows 上 chmod 改的是 POSIX 位，NTFS 不认，chmod 500 之后照样能写，
# 第 11 段就会假失败。这里落到 ACL：只拒 WD（建文件）和 AD（建子目录），保留 RD 列目录 ——
# 列目录也得拒的话，战斗服启动时 SettlementStore.Count() 枚举就会崩，根本走不到写那一步。
make_unwritable() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      MSYS_NO_PATHCONV=1 icacls "$(cygpath -w "$1")" /deny "*S-1-1-0:(WD,AD)" >/dev/null 2>&1 || true ;;
    *) chmod 500 "$1" 2>/dev/null || true ;;
  esac
}

make_writable() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      MSYS_NO_PATHCONV=1 icacls "$(cygpath -w "$1")" /remove:d "*S-1-1-0" >/dev/null 2>&1 || true ;;
    *) chmod 700 "$1" 2>/dev/null || true ;;
  esac
}

cleanup() {
  if [ -n "$BATTLE_PID" ] && kill -0 "$BATTLE_PID" 2>/dev/null; then
    kill -TERM "$BATTLE_PID" 2>/dev/null || true
    wait "$BATTLE_PID" 2>/dev/null || true
  fi
  [ -d "$TMP/readonly/settlements" ] && make_writable "$TMP/readonly/settlements"
  rm -rf "$TMP"
}
trap cleanup EXIT

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }

detail() { sed 's/^/       /'; }

echo "== 1. 生成物和 proto 对得上 =="
if bash "$ROOT/tools/gen-proto.sh" --into "$TMP/gen" >"$TMP/gen.log" 2>&1; then
  ok "重新生成成功"
else
  bad "生成失败，见下面几行"
  tail -5 "$TMP/gen.log" | detail
fi

if diff -r -q -x "*.meta" "$TMP/gen" "$ROOT/src/Shared/Generated" >/dev/null 2>&1; then
  ok "服务端生成物与仓库一致"
else
  bad "服务端生成物和 proto 不一致，重跑 tools/gen-proto.sh 并提交"
  diff -r -x "*.meta" "$TMP/gen" "$ROOT/src/Shared/Generated" | head -8 | detail
fi

if diff -r -q -x "*.meta" "$ROOT/src/Shared/Generated" "$ROOT/client/MMOGame/Assets/Proto/Generated" >/dev/null 2>&1; then
  ok "客户端与服务端两份生成物逐字相同"
else
  bad "两端生成物不同"
fi

# P0-1a 已经核过的字段不能被这次改动碰掉。编号变了就等于改协议，老客户端会读错位。
PROTO="$ROOT/proto/mmo/battle.proto"
if grep -q 'uint32 skill_id = 1;' "$PROTO" \
   && grep -q 'uint64 target_id = 2;' "$PROTO" \
   && grep -q 'int64 cast_time_ms = 3;' "$PROTO" \
   && grep -q 'int64 damage = 5;' "$PROTO" \
   && grep -q 'uint32 stagger_ms = 7;' "$PROTO"; then
  ok "P0-1a 定下的请求与结果字段还在，编号没动"
else
  bad "P0-1a 的字段或编号被改动了"
fi

echo "== 2. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet，后面的核对没法跑"
  echo
  echo "P0-1b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Battle/Battle.csproj" -c Release --nologo -v q >"$TMP/build-battle.log" 2>&1; then
  ok "战斗进程编译通过"
else
  bad "战斗进程编译失败"
  grep -E "error " "$TMP/build-battle.log" | head -5 | detail
fi

if "$DOTNET" build "$ROOT/tools/BattleProbe/BattleProbe.csproj" -c Release --nologo -v q >"$TMP/build-probe.log" 2>&1; then
  ok "核对客户端编译通过"
else
  bad "核对客户端编译失败"
  grep -E "error " "$TMP/build-probe.log" | head -5 | detail
fi

if [ "$FAIL" -gt 0 ]; then
  echo
  echo "P0-1b 核对：$PASS 项通过，$FAIL 项未通过（编译没过，场景不跑）"
  exit 1
fi

# 临时配置：端口让系统给，结算目录放临时目录，不污染仓库里的 var/。
python3 - "$ROOT" "$TMP" <<'PY' > "$TMP/ids.env"
import json, os, sys

root, tmp = sys.argv[1], sys.argv[2]
battle = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
battle["listen"] = "127.0.0.1:0"
battle["settlement_dir"] = os.path.join(tmp, "settlements")
battle["character_dir"] = os.path.join(tmp, "characters")
battle["skill_table"] = os.path.join(root, "config", "skills.json")
battle["class_table"] = os.path.join(root, "config", "classes.json")
json.dump(battle, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)

# 只在默认职业的技能里挑，别挑到别的职业手上。
cls_id = battle["default_class"]
skills = [s for s in json.load(open(os.path.join(root, "config", "skills.json"), encoding="utf-8"))["skills"]
          if s["class_id"] == cls_id]
ranged = max(skills, key=lambda s: s["range_mm"])
melee = min(skills, key=lambda s: s["range_mm"])
a, b = battle["caster"]["spawn_mm"], battle["boss"]["spawn_mm"]
dist = int(round(((a["x"] - b["x"]) ** 2 + (a["y"] - b["y"]) ** 2) ** 0.5))

print("BOSS=%d" % battle["boss"]["id"])
print("BOSS_HP_MAX=%d" % battle["boss"]["hp_max"])
print("EXP_ON_CLEAR=%d" % battle["boss"]["exp_on_clear"])
print("RANGED=%d" % ranged["id"])
print("MELEE=%d" % melee["id"])
print("DIST=%d" % dist)
print("MELEE_RANGE=%d" % melee["range_mm"])
PY

# shellcheck disable=SC1090
. "$TMP/ids.env"

if [ "$DIST" -gt "$MELEE_RANGE" ]; then
  ok "近战技能够不着站位（${DIST}mm > ${MELEE_RANGE}mm），可以用来验距离拒绝"
else
  bad "近战技能够得着站位（${DIST}mm <= ${MELEE_RANGE}mm），距离拒绝这一项没法验"
fi

start_battle() {
  local log="$1" cfg="$2"
  : > "$log"
  "$DOTNET" "$BATTLE_DLL" --config "$cfg" >>"$log" 2>&1 &
  BATTLE_PID=$!

  local i
  for i in $(seq 1 120); do
    if grep -q '^listen=' "$log" 2>/dev/null; then
      local addr
      addr="$(grep -m1 '^listen=' "$log" | cut -d= -f2-)"
      WS_URL="$(printf '%s' "$addr" | sed 's|^http|ws|')/battle"
      return 0
    fi
    if ! kill -0 "$BATTLE_PID" 2>/dev/null; then
      return 1
    fi
    sleep 0.1
  done
  return 1
}

stop_battle() {
  if [ -n "$BATTLE_PID" ] && kill -0 "$BATTLE_PID" 2>/dev/null; then
    kill -TERM "$BATTLE_PID" 2>/dev/null || true
    wait "$BATTLE_PID" 2>/dev/null || true
  fi
  BATTLE_PID=""
}

settlement_count() {
  local dir="$1"
  if [ ! -d "$dir" ]; then
    echo 0
    return
  fi
  find "$dir" -maxdepth 1 -name '*.json' | wc -l | tr -d ' '
}

echo "== 3. 起战斗进程 =="
if start_battle "$TMP/battle-1.log" "$TMP/battle.json"; then
  ok "起来了，$WS_URL"
  if grep -q '^resume=off$' "$TMP/battle-1.log"; then
    ok "启动声明不恢复上一轮的场次"
  else
    bad "启动日志里没有 resume=off"
  fi
else
  bad "战斗进程没起来"
  tail -10 "$TMP/battle-1.log" | detail
  echo
  echo "P0-1b 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

run_case() {
  local name="$1" title="$2"
  local out
  out="$("$DOTNET" "$PROBE_DLL" --url "$WS_URL" --case "$name" \
        --boss "$BOSS" --ranged "$RANGED" --melee "$MELEE" 2>&1)"
  if printf '%s\n' "$out" | grep -qx 'verdict=ok'; then
    ok "$title"
  else
    bad "$title"
  fi
  printf '%s\n' "$out" | grep -v '^verdict=' | detail
}

echo "== 4. 开场与结算 =="
run_case open    "连上就开一场，双方满血，站位有距离"
run_case damage  "一次施法：伤害、剩余血量、硬直、冷却都对得上"

echo "== 5. 一次只结算一个 =="
run_case cooldown "完成标准一：冷却里再发一次，只有第一次生效"
run_case casting  "前摇里再发，后一次被挡住"
run_case recovery "后摇没走完不能接下一个"

echo "== 6. 拒绝不动目标生命 =="
run_case range  "距离不够就拒绝，目标生命不变"
run_case reject "编的技能号和对不上的目标号都拒绝，目标生命不变"

echo "== 7. 打断 =="
run_case cancel "完成标准二：前摇里取消，这次没有伤害，冷却也退回去了"

echo "== 8. 通关与结算落盘 =="
SETTLE_1="$TMP/settlements"
CLEAR_OUT="$("$DOTNET" "$PROBE_DLL" --url "$WS_URL" --case clear \
             --boss "$BOSS" --ranged "$RANGED" --melee "$MELEE" 2>&1)"
if printf '%s\n' "$CLEAR_OUT" | grep -qx 'verdict=ok'; then
  ok "打到通关，回包带结算号"
else
  bad "通关流程不对"
fi
printf '%s\n' "$CLEAR_OUT" | grep -v '^verdict=' | detail

COUNT_AFTER_CLEAR="$(settlement_count "$SETTLE_1")"
if [ "$COUNT_AFTER_CLEAR" = "1" ]; then
  ok "结算目录正好一条记录"
else
  bad "结算目录有 ${COUNT_AFTER_CLEAR} 条，应该是 1 条"
fi

RECORD="$(find "$SETTLE_1" -maxdepth 1 -name '*.json' | head -1)"
if [ -n "$RECORD" ] && python3 - "$RECORD" "$BOSS" "$EXP_ON_CLEAR" <<'PY'
import json, sys
record = json.load(open(sys.argv[1], encoding="utf-8"))
boss, exp = int(sys.argv[2]), int(sys.argv[3])
need = ["settlement_id", "session_id", "caster_id", "boss_id", "exp_gain",
        "damage_total", "cast_count", "closed_at", "skill_table_version"]
missing = [k for k in need if k not in record]
problems = []
if missing:
    problems.append("缺字段 " + ",".join(missing))
if record.get("boss_id") != boss:
    problems.append("boss_id 不是 %d" % boss)
if record.get("exp_gain") != exp:
    problems.append("exp_gain 不是 %d" % exp)
if record.get("damage_total", 0) <= 0:
    problems.append("damage_total 不是正数")
if record.get("cast_count", 0) <= 0:
    problems.append("cast_count 不是正数")
if problems:
    print("；".join(problems))
    sys.exit(1)
print("settlement_id=%s exp_gain=%s casts=%s damage_total=%s"
      % (record["settlement_id"], record["exp_gain"], record["cast_count"], record["damage_total"]))
PY
then
  ok "记录内容齐，事务号、经验、伤害合计都在"
else
  bad "结算记录内容不对"
fi

echo "== 9. 重启：不重复写，也不恢复上一场 =="
stop_battle
if start_battle "$TMP/battle-2.log" "$TMP/battle.json"; then
  ok "重启起来了"
  if [ "$(settlement_count "$SETTLE_1")" = "1" ]; then
    ok "重启没有把同一条结算再写一次"
  else
    bad "重启后结算条数变了"
  fi
  run_case open "完成标准三：重启后新开一场是满血，上一场的进度没被恢复"
else
  bad "重启没起来"
  tail -10 "$TMP/battle-2.log" | detail
fi

echo "== 10. 打到一半崩掉，不会有奖励冒出来 =="
# 打两下但打不死，然后 kill -9。Boss 没死本来就没有奖励，
# 要确认的是重启后既没有补发记录，也没有把血量和进度带回来。
"$DOTNET" "$PROBE_DLL" --url "$WS_URL" --case damage --boss "$BOSS" --ranged "$RANGED" >"$TMP/half.log" 2>&1
if grep -qx 'verdict=ok' "$TMP/half.log"; then
  ok "崩之前先打掉一截血"
else
  bad "崩之前的施法没成功"
  detail < "$TMP/half.log"
fi

if [ -n "$BATTLE_PID" ] && kill -0 "$BATTLE_PID" 2>/dev/null; then
  kill -9 "$BATTLE_PID" 2>/dev/null || true
  wait "$BATTLE_PID" 2>/dev/null || true
  BATTLE_PID=""
  ok "kill -9 掉"
else
  bad "进程已经不在，没法做崩溃这一项"
fi

if [ "$(settlement_count "$SETTLE_1")" = "1" ]; then
  ok "崩溃没留下新记录"
else
  bad "崩溃后结算条数变了"
fi

if start_battle "$TMP/battle-3.log" "$TMP/battle.json"; then
  ok "崩完能重新起来"
  if [ "$(settlement_count "$SETTLE_1")" = "1" ]; then
    ok "重启后还是那一条，没有补发"
  else
    bad "重启后结算条数变了"
  fi
  run_case open "崩之前打掉的血没有带回来（新一场仍是满血）"
else
  bad "崩完起不来"
  tail -10 "$TMP/battle-3.log" | detail
fi

echo "== 11. 写不下就不发号 =="
# 结算目录不可写时，通关也不能给出一个查不到的事务号。
mkdir -p "$TMP/readonly/settlements"
make_unwritable "$TMP/readonly/settlements"
if ( : > "$TMP/readonly/settlements/.probe" ) 2>/dev/null; then
  echo "  注意：这个目录仍然可写，第 11 段的两项验不了（下面会失败，不是产品的问题）"
  rm -f "$TMP/readonly/settlements/.probe" 2>/dev/null || true
fi
python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
cfg = json.load(open(os.path.join(tmp, "battle.json"), encoding="utf-8"))
cfg["settlement_dir"] = os.path.join(tmp, "readonly", "settlements")
json.dump(cfg, open(os.path.join(tmp, "battle-ro.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)
PY
stop_battle
if start_battle "$TMP/battle-4.log" "$TMP/battle-ro.json"; then
  RO_OUT="$("$DOTNET" "$PROBE_DLL" --url "$WS_URL" --case clear \
            --boss "$BOSS" --ranged "$RANGED" --melee "$MELEE" 2>&1)"
  printf '%s\n' "$RO_OUT" | grep -v '^verdict=' | detail
  if case "$RO_OUT" in
       *"settlement_id= exp_gain=0"*) true ;;
       *) false ;;
     esac; then
    ok "写不下就交空号，经验也给 0，客户端领不到奖"
  else
    bad "结算目录不可写时仍然发了号"
  fi
  if grep -q '^settlement_error=' "$TMP/battle-4.log"; then
    ok "进程自己把写失败记下来了"
  else
    bad "写失败没有留日志"
  fi
else
  bad "只读结算目录这一轮没起来"
  tail -10 "$TMP/battle-4.log" | detail
fi

stop_battle
make_writable "$TMP/readonly/settlements"

echo
echo "P0-1b 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
