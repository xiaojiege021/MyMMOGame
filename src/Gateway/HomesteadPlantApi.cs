using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;
using Mmo.Proto.Social;

namespace Gateway;

/// <summary>N11 家园种植 HTTP 口。收获发家园币经社交服调角色服。</summary>
public static class HomesteadPlantApi
{
    public sealed record PlantPostBody(ulong? CharacterId, string? SlotId, string? CropId);
    public sealed record HarvestBody(ulong? CharacterId, string? SlotId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var character = new CharacterService.CharacterServiceClient(
            GrpcChannel.ForAddress(config.CharacterUpstream));
        var social = new SocialService.SocialServiceClient(
            GrpcChannel.ForAddress(config.SocialUpstream));

        app.MapGet("/homestead/plants", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            if (!ulong.TryParse(context.Request.Query["owner"].ToString(), out var owner) || owner == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var reply = await social.DescribeHomesteadPlantsAsync(
                new DescribeHomesteadPlantsRequest { Account = account, OwnerCharacterId = owner },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: context.RequestAborted);
            await WriteAsync(context, 200, PlantBody(reply));
        });

        app.MapPost("/homestead/plant", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            var body = await JsonSerializer.DeserializeAsync<PlantPostBody>(
                context.Request.Body, Json, context.RequestAborted);
            if (body?.CharacterId is null or 0
                || string.IsNullOrWhiteSpace(body.SlotId)
                || string.IsNullOrWhiteSpace(body.CropId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
            {
                await WriteAsync(context, 404, new { error = "character_not_found" });
                return;
            }

            var txnId = "homestead_plant:" + Guid.NewGuid().ToString("N");
            var result = await social.PlantHomesteadCropAsync(
                new HomesteadPlantRequest
                {
                    Account = account,
                    CharacterId = body.CharacterId.Value,
                    TxnId = txnId,
                    SlotId = body.SlotId.Trim(),
                    CropId = body.CropId.Trim(),
                },
                deadline: DateTime.UtcNow.AddSeconds(10),
                cancellationToken: context.RequestAborted);
            await WriteAsync(context, 200, PlantBody(result));
        });

        app.MapPost("/homestead/harvest", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            var body = await JsonSerializer.DeserializeAsync<HarvestBody>(
                context.Request.Body, Json, context.RequestAborted);
            if (body?.CharacterId is null or 0 || string.IsNullOrWhiteSpace(body.SlotId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
            {
                await WriteAsync(context, 404, new { error = "character_not_found" });
                return;
            }

            var txnId = "homestead_harvest:" + Guid.NewGuid().ToString("N");
            var result = await social.HarvestHomesteadCropAsync(
                new HomesteadPlantRequest
                {
                    Account = account,
                    CharacterId = body.CharacterId.Value,
                    TxnId = txnId,
                    SlotId = body.SlotId.Trim(),
                },
                deadline: DateTime.UtcNow.AddSeconds(10),
                cancellationToken: context.RequestAborted);
            await WriteAsync(context, 200, PlantBody(result));
        });
    }

    static object PlantBody(HomesteadPlantResponse reply) => new
    {
        ok = reply.Ok,
        reason = reply.Reason,
        character_id = reply.CharacterId,
        slot_id = reply.SlotId,
        crop_id = reply.CropId,
        txn_id = reply.TxnId,
        duplicate = reply.Duplicate,
        slot_count = reply.SlotCount,
        granted_home_coin = reply.GrantedHomeCoin,
        home_coin_balance = reply.HomeCoinBalance,
        plants = reply.Plants.Select(p => new
        {
            slot_id = p.SlotId,
            crop_id = p.CropId,
            planted_at_unix = p.PlantedAtUnix,
            ready_at_unix = p.ReadyAtUnix,
            mature = p.Mature,
        }),
    };
    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}