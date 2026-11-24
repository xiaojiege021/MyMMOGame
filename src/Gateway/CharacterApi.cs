using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// 建角色的入口。账号从令牌里取，不信请求体——客户端改不了自己是谁。
/// 真正的检查与落库在角色服（角色的唯一写入），这里只做鉴权、转发和错误码翻译。
/// </summary>
public static partial class CharacterApi
{
    public sealed record CreateBody(string? Name, string? Race, string? Appearance, string? ClassId, string? Branch);

    public sealed record LevelBody(ulong? CharacterId, int? Level);

    public sealed record AddItemBody(ulong? CharacterId, uint? Slot, string? ItemId, int? Count);

    public sealed record CanTradeBody(ulong? CharacterId, uint? Slot);

    // Q10：移动 / 丢弃 / 排序 / 批量使用。数量、格号都由客户端指定，但判不判得下来
    // 只由角色服按库里的格子与配置决定；discard 的 txn_id 可以由客户端带，没带就现生成。
    public sealed record MoveItemBody(ulong? CharacterId, uint? FromSlot, uint? ToSlot);

    public sealed record DiscardItemBody(
        ulong? CharacterId, uint? Slot, int? Count, bool? Confirm, string? TxnId);

    public sealed record SortInventoryBody(ulong? CharacterId);

    public sealed record UseItemBatchBody(
        ulong? CharacterId, uint? Slot, int? Count, string? TxnId);

    // B9：穿上只给背包格号，脱下只给部位。部位与门槛由角色服从配置与库里读。
    public sealed record EquipItemBody(ulong? CharacterId, uint? Slot);

    public sealed record UnequipItemBody(ulong? CharacterId, string? EquipSlot);

    // B4：骑上只给背包格号，下坐骑只给角色号。档位与速度由角色服从 config/mount.json 读。
    public sealed record MountUpBody(ulong? CharacterId, uint? Slot);

    public sealed record DismountBody(ulong? CharacterId);

    public sealed record CurrencyBody(ulong? CharacterId, string? Kind, long? Delta);

    public sealed record GrantEntryBody(CurrencyDeltaBody? Currency, ItemDeltaBody? Item);

    public sealed record CurrencyDeltaBody(string? Kind, long? Delta);

    public sealed record ItemDeltaBody(uint? Slot, string? ItemId, int? Count);

    public sealed record GrantBody(ulong? CharacterId, string? TxnId, List<GrantEntryBody>? Entries);

    public sealed record EnterDungeonBody(ulong? CharacterId, string? DungeonId, uint? PartySize);

    public sealed record HotSpringBody(ulong? CharacterId);

    // 经验结算请求里只有角色、事务号、来源、人数与整队成员，没有经验数值：
    // 客户端多传一个 exp_gain 也进不了角色服的那一笔。member_ids 只用于判同门，
    // 客户端传不传都不影响「谁同门」的最终判定——角色服按社交服的事实重判。
    public sealed record SettleExpBody(
        ulong? CharacterId, string? TxnId, string? Source, uint? PartySize, List<ulong>? MemberIds);

    // 强化请求里只有角色、格子、用哪种石头和「这一手用不用保护券」：等级、
    // 成功率、消耗、结果、券编号 / 数量 / 来源与事务号都读不进来
    // （事务号由网关生成），多传这些字段也不会被反序列化到请求里。
    public sealed record EnhanceBody(
        ulong? CharacterId, uint? Slot, string? StoneId, bool? UseCoupon);

    // 转移请求里只有角色和两个格子：两格等级、绑定状态、转移石编号 / 张数、
    // 金币费用、结果与事务号都读不进来（事务号由网关生成）。
    public sealed record TransferBody(ulong? CharacterId, uint? FromSlot, uint? ToSlot);

    // Q12/K3：改名请求只有角色号与新名字。长度、敏感词、唯一性、要用哪件卡、
    // 扣几张、结果与事务号都读不进来（事务号由网关生成）。
    public sealed record RenameBody(ulong? CharacterId, string? NewName);

    // 装上结晶只带装备格、结晶所在背包格与镶嵌位号：类别、部位、固定值、
    // 能不能装、结果与事务号都读不进来（事务号由网关生成）。
    public sealed record InstallCrystalBody(
        ulong? CharacterId, uint? Slot, uint? CrystalSlot, uint? Socket);

    // 拆下只带装备格与镶嵌位号，拆下来的结晶由角色服放回背包。
    public sealed record RemoveCrystalBody(ulong? CharacterId, uint? Slot, uint? Socket);

    // P3-2a：灵石相邻档兑换只带角色与低档币种。比例、产出、到账、数值与事务号
    // 都不在请求体里（事务号由网关生成），客户端改不了「相邻一档换一个」。
    public sealed record ExchangeStoneBody(ulong? CharacterId, string? FromKind);

    // K3：开始制作只带角色与配方编号。材料 / 产出 / 费用 / 成功率 / 耗时 / 解锁等级
    // 都读角色服的 config/recipes.json；事务号由网关生成，客户端带不进来。
    public sealed record CraftStartBody(ulong? CharacterId, string? RecipeId);

    // K3：收取只带角色。掷点与产出入包都在角色服，请求里没有「成功」这个字段。
    public sealed record CraftFinishBody(ulong? CharacterId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        // 通道建一次复用；地址在启动配置里定死，运行期不变。
        var channel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var client = new CharacterService.CharacterServiceClient(channel);

        RegisterCreate(app, client, tokens);
        RegisterInventory(app, client, tokens);
        RegisterItemActions(app, client, tokens);
        RegisterItemBatch(app, client, tokens);
        RegisterEquip(app, client, tokens);
        RegisterCurrency(app, client, tokens);
        RegisterProgression(app, client, tokens);
        RegisterProfile(app, client, tokens);
        RegisterEnhance(app, client, tokens);
        RegisterCrystal(app, client, tokens);
        RegisterExchange(app, client, tokens);
        RegisterCraft(app, client, tokens);
        RegisterCompanion(app, client, tokens);
    }

    static Task WriteCrystalAsync(HttpContext context, CrystalResponse result) =>
        WriteAsync(context, 200, new
        {
            ok = result.Ok,
            reason = result.Reason,
            installed = result.Installed,
            slot = result.Slot,
            socket = result.Socket,
            crystal_item = result.CrystalItem,
            category = result.Category,
            part = result.Part,
            socket_count = result.SocketCount,
            filled_before = result.FilledBefore,
            filled = result.Filled,
            crystal_left = result.CrystalLeft,
            gold = result.Gold,
            enhance_level = result.EnhanceLevel,
            bound = result.Bound,
            duplicate = result.Duplicate,
        });
    static async Task WriteRpcErrorAsync(HttpContext context, string what, string account, RpcException ex)
    {
        var code = ex.Status.Detail;
        var http = ex.StatusCode switch
        {
            StatusCode.InvalidArgument => 400,
            StatusCode.NotFound => 404,
            StatusCode.AlreadyExists => 409,
            // 余额不足这类「前提不满足」走 412，与参数错分开。
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
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
