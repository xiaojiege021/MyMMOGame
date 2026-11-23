#!/usr/bin/env bash
# N2 宗门名册只读（开发架构第 14 节第 81 行）。
#   bash tools/check/check-n2.sh
# 完成标准：能读到成员和职位。只读，不改任免和人数上限。
# 玩法正本：方案 11.1 职位是掌门、副掌门、长老、执事、弟子。
# 进程边界：技术架构 3 节，名册在社交服，不写角色库。
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
if "$DOTNET" build src/Social/Social.csproj -c Release -v quiet > "$TMP/build.log" 2>&1 \
   && "$DOTNET" build src/Gateway/Gateway.csproj -c Release -v quiet >> "$TMP/build.log" 2>&1; then
  ok "编译 Social + Gateway"
else
  bad "编译失败"
  tail -20 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言 =="
python3 - "$TMP/static.txt" <<'PY'
import os, re, sys
out = open(sys.argv[1], "w", encoding="utf-8")
def report(k, cond, info=""):
    out.write(("PASS " if cond else "FAIL ") + k + ("" if cond else "  " + info) + "\n")
def read(p):
    _sub = {"CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World"}
    _name = os.path.basename(p)
    if _name in _sub and os.path.exists(p):
        # 14.8 拆分卡之后：主文件 + 分片目录一起读（断言文本不变，只改读法）。
        return open(p, encoding="utf-8").read() + "".join(
            open(f, encoding="utf-8").read()
            for f in sorted(__import__("glob").glob(os.path.join(os.path.dirname(p), _sub[_name], "*.cs"))))
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

proto = read("proto/mmo/social.proto")
repo = read("src/Social/SectRepository.cs")
grpc = read("src/Social/SocialGrpcService.cs")
program = read("src/Social/Program.cs")
api = read("src/Gateway/SectApi.cs")
server = read("src/Shared/Generated/Social.cs")
client = read("client/MMOGame/Assets/Proto/Generated/Social.cs")
allcards = read("tools/check/check-all.sh")

report("proto_rpc", "rpc DescribeSectRoster" in proto and "message SectRosterMember" in proto)
report("proto_no_cap", "member_cap" not in proto.split("message DescribeSectRosterRequest")[1].split("service SocialService")[0])
report("ends_match", "class DescribeSectRosterRequest" in server and "class DescribeSectRosterRequest" in client)
report("grpc_method", "DescribeSectRoster" in grpc and "RosterAsync" in grpc)
start = repo.find("public async Task<Roster> RosterAsync")
end = repo.find("P2-1c：只读查一个角色", start)
body = repo[start:end] if start >= 0 and end > start else ""
report("roster_read_only", start >= 0 and not re.search(r"\b(insert|update|delete)\b", body, re.I))
report("master_filled", "master_character_id" in body and '"master"' in body)
report("gateway_get", 'MapGet("/sect/roster"' in api and 'Query["sect_id"]' in api)
report("gateway_ignores_position", 'Query["position"]' not in api and 'Query["member_cap"]' not in api)
report("cli_registered", '"--sect-roster"' in program and "sectRosterOf != 0" in program)
report("appoint_kept", "AppointAsync" in repo and "AppointPosition" in proto)
report("all_cards", "n2) echo" in allcards and re.search(r"\bn2\b", allcards))
PY
while IFS= read -r line; do
  case "$line" in
    PASS*) ok "${line#PASS }" ;;
    FAIL*) bad "${line#FAIL }" ;;
  esac
done < "$TMP/static.txt"

echo
finish
