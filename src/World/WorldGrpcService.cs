using System.Text;
using Grpc.Core;
using Mmo.Proto.World;

namespace World;

/// <summary>
/// 表世界分线、位置、采集的 gRPC 入口。区域与人物编号全从配置读，请求里带不进
/// 分线号、人数、容量或采集产物：客户端改包改不了自己被分到哪条线，也改不了产物。
/// 这里不结算技能，也不写角色库；采集成功的产物由角色服的 ApplyGrant 落库。
/// </summary>
public sealed partial class WorldGrpcService(
    LineRegistry registry, WorldCatalog catalog, PositionHub hub,
    GatherTable gather, GatherNodeRegistry nodes, IGatherGrant grants,
    FlightGate flight, WorldBossEntrance worldBoss, WorldBossSlots worldBossSlots, RegionGate regions,
    StallRegistry stallStalls)
    : global::Mmo.Proto.World.WorldService.WorldServiceBase
{
// 14.8 D3h：六组 RPC 按域拆进 Grpc/ 目录（Line / Catalog / WorldBoss / Travel / Position，
// 纯搬代码，判定与回包不变）。这个文件只留依赖声明；核对脚本仍按老路径读它，
// 由 lib-common 的 src_agg 展开成「主文件 + 分片目录」。
}
