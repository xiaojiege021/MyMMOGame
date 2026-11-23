#!/usr/bin/env python3
"""N14 静态断言：世界列表口按 RegionGate 的结论回开放，网关只转发，客户端只照回包写。"""
import os
import re
import sys

root = os.environ.get("ROOT") or os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))


# 14.8 拆分卡之后：网关的 HTTP 注册体按域分片（主文件 + Api/<域>/），断言文本不变，只改读法。
_SUB = {"CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World", "SectRepository.cs": "Sect", "SocialGrpcService.cs": "Grpc", "WorldGrpcService.cs": "Grpc", "Program.cs": "Cli"}


def read(*parts):
    path = os.path.join(root, *parts)
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    sub = _SUB.get(os.path.basename(path))
    if sub:
        import glob
        text += "".join(open(f, encoding="utf-8").read()
                        for f in sorted(glob.glob(os.path.join(os.path.dirname(path), sub, "*.cs"))))
    return text


proto = read("proto", "mmo", "world.proto")
gate = read("src", "World", "RegionGate.cs")
grpc = read("src", "World", "WorldGrpcService.cs")
world_cli = read("src", "World", "Program.cs")
gateway = read("src", "Gateway", "WorldApi.cs")
entry = read("client", "MMOGame", "Assets", "Net", "WorldEntry.cs")
flow = read("client", "MMOGame", "Assets", "HotUpdate", "Ui", "UiWorldFlow.cs")
screen = read("client", "MMOGame", "Assets", "HotUpdate", "Ui", "UiWorldMapScreen.cs")
probe = read("tools", "probe", "WorldEntryProbe", "Program.cs")

# 1) proto 只加字段，老字段语义不变
assert re.search(r"message ListRegionsRequest \{[^}]*string account = 1;", proto, re.S), \
    "ListRegionsRequest 没加 account"
assert re.search(r"message ListRegionsRequest \{[^}]*uint64 character_id = 2;", proto, re.S), \
    "ListRegionsRequest 没加 character_id"
for field in ("string reason = 7;", "int32 unlock_stage = 8;", "int32 main_stage = 9;"):
    assert field in proto, "RegionInfo 缺 " + field
assert "bool open = 6;" in proto, "RegionInfo 的老字段 open 被改了"

# 2) 世界服：列表口过 RegionGate，判定仍只有 Evaluate 一份
assert "regions.EvaluateManyAsync(" in grpc, "ListRegions 没走 RegionGate 的批量判定"
assert "EvaluateManyAsync" in gate, "RegionGate 没有批量判定的方法"
assert "public RegionAccess Evaluate(" in gate, "RegionGate 的纯判定入口不见了"
# 批量法只负责取事实值，不许再写一套门槛比较
batch = gate.split("public async Task<IReadOnlyList<RegionAccess>> EvaluateManyAsync", 1)[1]
batch = batch.split("RegionAccess EvaluateOne", 1)[0]
for key in ("UnlockStage <=", "alignment ==", "region_route_mismatch =="):
    assert key not in batch, "批量判定里又写了一套规则：" + key
# 列表口不许再把静态字段直接当结论
assert "Open = region.Open" not in grpc, "列表口还在拿静态 open 当结论"
assert "regions.EvaluateManyAsync" in grpc and "access is null" in grpc, \
    "列表口没分开「带角色」与「只读静态表」两条路"

# 3) 网关：可选的 character_id + 归属复核 + 三个新字段
assert 'context.Request.Query["character_id"]' in gateway, "网关没读 character_id"
assert "OwnsAsync(characters, account, characterId" in gateway, "网关没复核角色归属"
for key in ("reason = r.Reason", "unlock_stage = r.UnlockStage", "main_stage = r.MainStage"):
    assert key in gateway, "网关回包缺 " + key

# 4) 客户端：只照回包写，没回包退回配置并注明
assert "RegionsPath(ulong characterId)" in entry, "客户端没有带角色号的路由"
for key in ("TryRegion", "RegionStateText", "region_route_mismatch"):
    assert key in entry, "WorldEntry 缺 " + key
assert "WorldEntry.RegionsPath(_characterId)" in flow, "LoadRegions 没带角色号"
assert "TryRegionState" in flow, "流程层没有开放状态的唯一出口"
assert 'region.Open ? "已开（配置）" : "未开（配置）"' in screen, \
    "配置兜底那一支没写明是配置值"
assert "未开的是第 11 场主线开放" not in screen, "写死第 11 场的那句还留着"
assert "TryRegionState" in screen, "屏上没有「回包优先 / 配置兜底」两条路"

# 5) 探针能念出开放状态（客户端拆装与核对比对同一份）
assert "regions_open=" in probe and "locked=" in probe, "拆装探针没打开放状态"

# 6) 世界服的只读入口：列表口纯计算，判定与运行期同一份
assert '"--region-list"' in world_cli, "世界服没有 --region-list 只读入口"
assert "gate.Evaluate(region, accessStage, accessAlignment)" in world_cli, \
    "只读入口没用同一份判定"
assert "gate.NeedsProgress(region)" in world_cli, "只读入口没按「要不要问进度」分开"

# 7) 地名 / 解锁规则一个字没改
unlock = read("config", "region_unlock.json")
assert '"unlock_stage": 11' in unlock and '"alignment": "立仙"' in unlock and '"alignment": "立魔"' in unlock
for name in ("旧天城区", "焦原"):
    assert '"region_id": "%s"' % name in unlock

# 8) 总控三处都挂上 n14
allcards = read("tools", "check", "check-all.sh")
assert "n14) echo" in allcards, "check-all 的 deps_of 没挂 n14"
assert re.search(r"\bn14\b", allcards), "check-all 的 ALL_CARDS 没挂 n14"

# 9) 排期表口径
arch = read("docs", "开发架构.md")
assert "| 98 | N14 | 地表列表与 N1 解锁一致 |" in arch, "开发架构第 98 行口径变了"

print("n14_static=ok")
sys.exit(0)
