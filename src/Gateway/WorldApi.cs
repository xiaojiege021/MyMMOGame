using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.World;
using Mmo.Shared.Domain;
using CharacterClient = Mmo.Proto.Character.CharacterService.CharacterServiceClient;
using WorldClient = Mmo.Proto.World.WorldService.WorldServiceClient;

namespace Gateway;

/// <summary>
/// 表世界分线的入口。账号从令牌里取，不信请求体；复核角色归属后转给世界服。
/// 分线号、人数、容量、区域编号都不从客户端读，网关与世界服都不接受伪造的分线状态。
/// 网关只做鉴权、转发和错误码翻译，分线的唯一写入方是世界服。
/// </summary>
public static partial class WorldApi
{
    public sealed record EnterBody(ulong? CharacterId, string? RegionId);

    public sealed record LeaveBody(ulong? CharacterId);

    // 客户端只给节点编号。产物、数量、背包格、事务号与账号都不在请求体里，
    // 多塞这些字段也不会被读进世界服的 GatherRequest。
    public sealed record GatherBody(ulong? CharacterId, string? NodeId);

    // 客户端只给想不想飞 / 想去哪块地表。境界、权限、当前区域与身份都不在请求体里，
    // 多塞这些字段也不会被读进世界服的请求；账号一律取自令牌。
    public sealed record FlyBody(ulong? CharacterId, bool? WantFly);

    public sealed record CrossRegionBody(ulong? CharacterId, string? TargetRegion);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(
        WebApplication app, TokenStore tokens, GatewayConfig config, CalendarTable calendar)
    {
        // 通道建一次复用；地址在启动配置里定死，运行期不变。
        var worldChannel = GrpcChannel.ForAddress(config.WorldUpstream);
        var world = new WorldClient(worldChannel);
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var characters = new CharacterClient(characterChannel);

        RegisterTravel(app, world, characters, tokens, calendar);
        RegisterGather(app, world, characters, tokens, calendar);
        RegisterRoam(app, world, characters, tokens, calendar);
        RegisterNodes(app, world, characters, tokens, calendar);
        RegisterWorldBoss(app, world, characters, tokens, calendar);
    }

    // 复核归属的同时取回名字，只走服与服这一段；请求体里没有这个名字字段。
    static async Task<string?> ResolveAsync(
        CharacterClient characters, string account, ulong characterId, HttpContext context)
    {
        try
        {
            var reply = await characters.ListInventoryAsync(
                new ListInventoryRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: context.RequestAborted);
            return reply.CharacterName;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }
    static async Task WriteRpcErrorAsync(HttpContext context, string what, string account, RpcException ex)
    {
        var code = ex.Status.Detail;
        var http = ex.StatusCode switch
        {
            StatusCode.InvalidArgument => 400,
            StatusCode.NotFound => 404,
            StatusCode.AlreadyExists => 409,
            StatusCode.FailedPrecondition => 412,
            StatusCode.Unavailable => 503,
            _ => 502,
        };

        Console.WriteLine(what + "_reject account=" + account + " code=" + code + " http=" + http);
        await WriteAsync(context, http, new { error = code });
    }

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(body, Json);
    }
}
