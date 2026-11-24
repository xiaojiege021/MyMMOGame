using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>
/// 宗门的 HTTP 入口（P2-1a1 / a2 / a3 / a4）：建宗门 / 收人 / 任命免职 /
/// 传位（P2-1a3 只给副掌门）/ 退出宗门（掌门不能直接退）/
/// 升级（P2-1a4 只耗建设、建设不从商城买）。
/// 网关只做三件事：认令牌、复核角色归属、服务端生成事务号后转给社交服。
/// 人数上限、等级、人数都在社交服按 config/sect.json 判定，网关一个都不算，
/// 也不碰社交库或角色库。业务前提不满足是 HTTP 200 + ok=false + reason；
/// 身份与参数错才走 401 / 400 / 404。
/// </summary>
public static partial class SectApi
{
    public sealed record CreateBody(ulong? CharacterId, string? Name);

    public sealed record JoinBody(ulong? CharacterId, ulong? SectId);

    // P2-1a2 请求体只带这四个字段，席位数字带不进来。
    public sealed record PositionBody(
        ulong? CharacterId, ulong? SectId, ulong? TargetCharacterId, string? Position);

    // P2-1a3 传位请求体：发起者 / 宗门 / 传位目标（现任副掌门）。
    // 谁是新掌门之外的判定值（掌门权威、席位数字）一概带不进来。
    public sealed record TransferBody(
        ulong? CharacterId, ulong? SectId, ulong? TargetCharacterId);

    // P2-1a3 退出请求体：不带 target（谁是新掌门、席位数字都带不进来）。
    public sealed record LeaveBody(ulong? CharacterId, ulong? SectId);

    // P2-1a4 升级请求体：只带发起者 / 宗门。目标等级、建设数、消耗一概带不进来，
    // 升到几级、扣多少建设只由社交服按 config/sect.json 判定。
    public sealed record UpgradeBody(ulong? CharacterId, ulong? SectId);

    // Q4 完成宗门任务的请求体：只带发起者 / 宗门 / 任务编号。次数、奖励、周期与
    // 等级门槛一概带不进来，全部由社交服按 config/sect_tasks.json 与日历判定。
    public sealed record SectTaskBody(ulong? CharacterId, ulong? SectId, string? TaskId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(characterChannel);
        var socialChannel = GrpcChannel.ForAddress(config.SocialUpstream);
        var social = new SocialService.SocialServiceClient(socialChannel);

        RegisterMembership(app, tokens, social, character);
        RegisterJoin(app, tokens, social, character);
        RegisterPosition(app, tokens, social, character);
        RegisterLeadership(app, tokens, social, character);
        RegisterUpgrade(app, tokens, social, character);
        RegisterTasks(app, tokens, social, character);
    }

    static object SectBody(SectResponse r) => new
    {
        ok = r.Ok,
        reason = r.Reason,
        sect_id = r.SectId,
        level = r.Level,
        member_cap = r.MemberCap,
        member_count = r.MemberCount,
        name = r.Name,
        txn_id = r.TxnId,
    };

    static object PositionSectBody(PositionStateResponse r) => new
    {
        ok = r.Ok,
        reason = r.Reason,
        sect_id = r.SectId,
        position = r.Position,
        seat_cap = r.SeatCap,
        seat_count = r.SeatCount,
        txn_id = r.TxnId,
    };

    // P2-1a3 回包全是只读事实值；业务拒绝时社交服把未生效数值给 0。
    static object LeadershipSectBody(LeadershipResponse r) => new
    {
        ok = r.Ok,
        reason = r.Reason,
        sect_id = r.SectId,
        old_master = r.OldMaster,
        new_master = r.NewMaster,
        master_count = r.MasterCount,
        vice_master_count = r.ViceMasterCount,
        member_count = r.MemberCount,
        txn_id = r.TxnId,
    };

    // P2-1a4 回包全是只读事实值；业务拒绝时社交服把未生效数值给 0。
    static object UpgradeSectBody(UpgradeResponse r) => new
    {
        ok = r.Ok,
        reason = r.Reason,
        sect_id = r.SectId,
        old_level = r.OldLevel,
        new_level = r.NewLevel,
        member_cap = r.MemberCap,
        construction_before = r.ConstructionBefore,
        construction_after = r.ConstructionAfter,
        upgrade_cost = r.UpgradeCost,
        txn_id = r.TxnId,
    };
    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
