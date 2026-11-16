using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

public static class HomeCoinShopApi
{
    public sealed record BuyBody(ulong? CharacterId, string? ItemId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var character = new CharacterService.CharacterServiceClient(
            GrpcChannel.ForAddress(config.CharacterUpstream));

        app.MapPost("/home-coin/shop/buy", async context =>
        {
            if (!Authorize(context, tokens, "home_coin_shop", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            BuyBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<BuyBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null or 0 || string.IsNullOrWhiteSpace(body.ItemId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var txnId = "home_coin_shop:" + Guid.NewGuid().ToString("N");
            try
            {
                var result = await character.BuyHomeCoinShopItemAsync(
                    new BuyHomeCoinShopRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        ItemId = body.ItemId.Trim(),
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    item_id = result.ItemId,
                    kind = result.Kind,
                    currency = result.Currency,
                    balance = result.Balance,
                    charged_home_coin = result.ChargedHomeCoin,
                    delivered = result.Delivered,
                    duplicate = result.Duplicate,
                    txn_id = txnId,
                });
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                await WriteAsync(context, 404, new { error = "character_not_found" });
            }
        });
    }

    static bool Authorize(HttpContext context, TokenStore tokens, string what, out string account)
    {
        var token = context.Request.Query["token"].ToString();
        if (string.IsNullOrEmpty(token))
        {
            var header = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                token = header[prefix.Length..].Trim();
            }
        }

        if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out account, out _))
        {
            Console.WriteLine(what + "_reject reason=token");
            return false;
        }

        return true;
    }

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}