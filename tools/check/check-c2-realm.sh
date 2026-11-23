#!/usr/bin/env bash
# tools/check/check-c2-realm.sh —— C2 立魔显示散仙（开发架构 14.6 顺序 6）。
#
# 注意：任务队列里的 C2 与内容卡「C2 采集节点铺开」重号。内容卡的正本是
# tools/check/check-c2.sh（MMO_C2_HAS_DB 那套，仍按原口径跑）；本卡（境界展示）
# 用 check-c2-realm.sh，两个脚本互不覆盖。
#
# 完成标准（14.6 的 C2 行，逐条对应下面的断言）：
#   1. 立仙第十段显示金仙、立魔显示散仙；
#   2. 库里的 realm 仍写顺序表规范名；
#   3. `散仙` 别名只做兼容；
#   4. 开关关着时行为不变。
#
# 口径：
#   - 展示名只在 `RealmTable.DisplayName(realm, alignment)` 算一次；realm 仍走
#     `Normalize`（散仙→金仙），世界服门槛、突破写库、库默认值都只认规范名。
#   - `/inventory` 回包同时给 `realm`（规范名）与 `realm_display`（展示名）；
#     客户端只显示 `realm_display`，没这一格时退回 `realm`，不自己按 alignment 判。
#
#   bash tools/check/check-c2-realm.sh
#   MMO_C2_REALM_SKIP_BUILD=1 bash tools/check/check-c2-realm.sh   跳过编译
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
AGG_CHARGRPC="$(src_agg src/Character/CharacterGrpcService.cs)"; export AGG_CHARGRPC
# Windows 上 dotnet 重定向输出走 GBK：境界名全是中文（金仙 / 散仙 / 大乘），
# 转成 UTF-8 再比，grep -F 才比得中（同 P3-1a1 的口径）。
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
CLI="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TMP="$(mktemp -d)"
TITLE="C2（境界展示）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_C2_REALM_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_C2_REALM_SKIP_BUILD=1）\n'
else
  for proj in src/Character/Character.csproj src/Gateway/Gateway.csproj; do
    if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
      ok "$(basename "$proj" .csproj) 编译通过"
    else
      bad "$(basename "$proj" .csproj) 编译失败"; tail -14 "$TMP/build.log" | detail
    fi
  done
  if "$DOTNET" build tools/probe/CliProbe/CliProbe.csproj -c Release --nologo -v q \
      >"$TMP/build-probe.log" 2>&1; then
    ok "CliProbe 编译通过"
  else
    bad "CliProbe 编译失败"; tail -14 "$TMP/build-probe.log" | detail
  fi
fi

echo "== 2. 静态断言：展示名只算一次、库只写规范名 =="
ROOT="$ROOT" python3 - >"$TMP/static.txt" 2>&1 <<'PY'
import json, os, re

root = os.environ["ROOT"]

def read(p):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server", "CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World"}
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

realms = cfg("config/realms.json")
aliases = realms.get("aliases") or {}
order = realms.get("realms") or []
switches = {x.get("realm"): x.get("open") for x in (realms.get("switches") or [])}
report("config_tenth", len(order) == 11 and order[9] == "金仙" and order[10] == "极境",
       "第十段不是金仙")
report("config_alias", aliases.get("散仙") == "金仙", "散仙不是金仙的兼容别名")
report("config_closed", switches.get("金仙") is False,
       "金仙开关不是 false，开关关着的行为会变")
report("config_launch", realms.get("launch_until") == "结丹", "首发不是结丹")

rt = read("src/Character/RealmTable.cs")
report("display_method", "public string DisplayName(string? realm, string? alignment)" in rt,
       "RealmTable 没有 DisplayName(realm, alignment)")
report("display_normalizes", 'var canonical = Normalize(realm);' in rt,
       "DisplayName 没先归一别名")
report("display_tenth_only", "_order[^2]" in rt and '"立魔"' in rt,
       "DisplayName 没钉在第十段 / 立魔")
report("display_reverse_alias", "pair.Value, canonical" in rt,
       "DisplayName 没从 aliases 反查展示名")

inv = read("src/Character/InventoryRepository.cs")
report("inventory_facts",
       "RealmFactsAsync" in inv and 'select realm, coalesce(alignment, \'\')' in inv,
       "InventoryRepository 没读 realm + alignment")

svc = read(os.environ["AGG_CHARGRPC"])
report("inventory_uses_display",
       "inventory.RealmFactsAsync" in svc and "realms.Normalize" in svc
       and "RealmDisplay = realms.DisplayName(realm, facts?.Alignment ?? \"\")" in svc,
       "ListInventory 没把规范名 / 展示名分开")
report("inventory_realm_canonical",
       'var realm = realms.Normalize(facts?.Realm ?? "")' in svc and "Realm = realm," in svc,
       "ListInventory 的 realm 没归一成规范名")

proto = read("proto/mmo/character.proto")
report("proto_field", "string realm_display = 5;" in proto,
       "proto 没有 realm_display = 5")
for side, path in (("server", "src/Shared/Generated/Character.cs"),
                   ("client", "client/MMOGame/Assets/Proto/Generated/Character.cs")):
    body = read(path)
    report("generated_" + side,
           "RealmDisplayFieldNumber = 5" in body and "public string RealmDisplay" in body,
           path + " 没有生成 RealmDisplay")

gw = read("src/Gateway/CharacterApi.cs")
report("gateway_json", "realm_display = list.RealmDisplay" in gw
       and "realm = list.Realm" in gw, "网关 /inventory 没回 realm_display")

bag = read("client/MMOGame/Assets/Net/CharacterBag.cs")
report("client_parse", "RealmDisplay = ReadRaw(json, \"realm_display\")" in bag
       and "public string DisplayRealm" in bag and "RealmDisplay : Realm" in bag,
       "客户端没拆 realm_display 或没做旧回包退回")
for path in ("client/MMOGame/Assets/HotUpdate/Ui/UiCharacterScreen.cs",
             "client/MMOGame/Assets/HotUpdate/Ui/UiRealmScreen.cs"):
    report("screen_" + os.path.basename(path),
           "flow.Bag.DisplayRealm" in read(path), path + " 没显示 DisplayRealm")

# 服务端不写死「散仙」这个名字；展示名从 config/realms.json 的别名反查。
# 客户端自检里那句「散仙」是喂给解析器的假回包，不参与展示规则。
literal = []
for base in ("src", "db"):
    for dirpath, dirnames, names in os.walk(os.path.join(root, base)):
        dirnames[:] = [d for d in dirnames if d not in (".git", "bin", "obj", "Library")]
        for name in names:
            if not name.endswith((".cs", ".sql")):
                continue
            body = read(os.path.relpath(os.path.join(dirpath, name), root))
            for line in body.splitlines():
                code = line.split("//")[0]
                if '"散仙"' in code:
                    literal.append(os.path.relpath(os.path.join(dirpath, name), root))
report("no_hardcoded_display_alias", not literal, ";".join(sorted(set(literal))[:5]))

# 库里的 realm 只写规范名：DDL 默认炼体，写库路径只取配置里的 requirement.Next。
report("ddl_default", "realm       text        NOT NULL DEFAULT '炼体'" in read("db/character/character.sql"),
       "character.realm 的默认值不是炼体")
report("advance_writes_canonical",
       "write.Parameters.AddWithValue(requirement.Next);" in read("src/Character/RealmAdvanceRepository.cs"),
       "突破写库没写配置里的规范名")

allsh = read("tools/check/check-all.sh")
ci = read("tools/check/ci.sh")
report("registered_all", re.search(r"^\s*c2-realm\)", allsh, re.M) is not None
       and "c2-realm" in allsh, "check-all 没挂 c2-realm")
report("registered_ci", re.search(r'^CHECKS="[\s\S]*\bc2-realm\b', ci, re.M) is not None,
       "ci.sh 的 CHECKS 里没有 c2-realm")

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

echo "== 3. 无库段：展示名 / 别名兼容 / 开关不变 =="
"$DOTNET" "$CLI" --realm-table --config config/character.json 2>&1 | utf8 >"$TMP/rt.txt"
rt_check() {
  local pattern="$1" msg="$2"
  if grep -qF "$pattern" "$TMP/rt.txt"; then
    ok "$msg"
  else
    bad "$msg"
    grep -E "realm_display|advance=大乘|advance=金仙" "$TMP/rt.txt" | tail -8 | detail
  fi
}
rt_check 'realm_display=金仙 immortal=金仙 demon=散仙 none=金仙' "金仙：立仙显示金仙、立魔显示散仙"
rt_check 'realm_display_alias=散仙 immortal=金仙 demon=散仙' "旧别名散仙：立仙归金仙、立魔显示散仙"
rt_check 'realm_display_other=结丹 immortal=结丹 demon=结丹' "非第十段不因立魔改显示名"
rt_check 'advance=大乘->金仙:realm_locked' "金仙开关关着：大乘仍突破不到金仙"
rt_check 'advance=金仙->极境:realm_locked' "极境开关关着：金仙仍突破不到极境"
rt_check 'realm_table_state=ok version=5 realms=11 launch_until=结丹 open=炼体,筑基,结丹' \
  "境界表仍是 11 段、首发三段、开关关着"

finish "$TITLE"
