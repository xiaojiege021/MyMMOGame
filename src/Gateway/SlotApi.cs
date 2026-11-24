using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// P3-4b 两个账号级只读口：角色位与外观收藏。
///
/// 网关只做两件事：认令牌、把账号转给角色服。名额（基础位 / 上限 / 现在几个）与
/// 外观收藏的并集全在角色服按配置与角色库回答，客户端连一个数字都带不进来——
/// 请求里除 token 之外只认 character_id（便于核对归属），别的 query 一律不读。
/// 两个口都只读：不写库、不发奖、不改名额。
/// </summary>
public static class SlotApi
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(characterChannel);

        // 只读：这个账号有几个角色位、已经建了几个角色。
        app.MapGet("/character/slots", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            try
            {
                var reply = await character.DescribeCharacterSlotsAsync(
                    new DescribeCharacterSlotsRequest { Account = account },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("character_slots account=" + account
                    + " base_slots=" + reply.BaseSlots
                    + " max_slots=" + reply.MaxSlots
                    + " slots=" + reply.Slots
                    + " used=" + reply.Used);

                await WriteAsync(context, 200, new
                {
                    account = reply.Account,
                    base_slots = reply.BaseSlots,
                    max_slots = reply.MaxSlots,
                    slots = reply.Slots,
                    used = reply.Used,
                    product_id = reply.ProductId,
                    price_yuanbao = reply.PriceYuanbao,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("character_slots_reject account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, ex.StatusCode == StatusCode.NotFound ? 404 : 502,
                    new { error = ex.Status.Detail });
            }
        });

        // 只读：外观收藏（同账号下所有角色持有的外观类物品取并集）。
        app.MapGet("/wardrobe", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            try
            {
                var reply = await character.DescribeWardrobeAsync(
                    new DescribeWardrobeRequest { Account = account },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("wardrobe account=" + account
                    + " found=" + reply.Found
                    + " entries=" + reply.Entries.Count);

                await WriteAsync(context, 200, new
                {
                    account = reply.Account,
                    found = reply.Found,
                    entries = reply.Entries.Select(e => new
                    {
                        item_id = e.ItemId,
                        kind = e.Kind,
                        name = e.Name,
                        character_ids = e.CharacterIds,
                    }),
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("wardrobe_reject account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, ex.StatusCode == StatusCode.NotFound ? 404 : 502,
                    new { error = ex.Status.Detail });
            }
        });
    }
    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
