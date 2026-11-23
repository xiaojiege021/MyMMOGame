using Grpc.Core;
using Mmo.Proto.Social;

namespace Social;

/// <summary>
/// 社交服 gRPC（P2-1a1）。校验都在服务端：人数上限、等级、人数都从
/// config/sect.json 读，请求里带不进来。业务前提不满足是正常回包里的
/// ok=false + reason，不是 gRPC 错误码；身份与参数错由网关在 HTTP 层挡。
///
/// 14.8 D3h：RPC 按域拆进 Grpc/ 目录（Sect / Rank / Tasks / Homestead / Party，
/// 纯搬代码，判定与回包不变）。这个文件只留依赖声明；核对脚本仍按老路径读它，
/// 由 lib-common 的 src_agg 展开成「主文件 + 分片目录」。
/// </summary>
public sealed partial class SocialGrpcService(
    SectTable sectTable,
    SectRepository sects,
    SectFounderGrant founderGrant,
    HomesteadRuntime homes,
    HomesteadPlantRuntime plants,
    SectTaskTable taskTable,
    SectTaskRepository tasks,
    SectTaskLevelProbe levelProbe,
    PartyRuntime? parties) : SocialService.SocialServiceBase
{
}
