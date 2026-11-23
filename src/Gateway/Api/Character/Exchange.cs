using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：P3-2a 灵石相邻档兑换。</summary>
public static partial class CharacterApi
{
    static void RegisterExchange(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // P3-2a：灵石相邻档兑换，不经过拍卖。请求只有角色与低档币种，
        // 比例、产出、最高档名与到账都由角色服从 config/stone_exchange.json 与库读出；
        // 事务号由网关生成，客户端带不进来，也不带任何数值字段。
        app.MapPost("/currency/exchange", async context =>
        {
            if (!TryAuthorize(context, tokens, "currency_exchange", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            ExchangeStoneBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<ExchangeStoneBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.FromKind))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await OwnsAsync(client, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }

                var txnId = "stone_exchange:" + Guid.NewGuid().ToString("N");
                var result = await client.ExchangeStoneAsync(
                    new ExchangeStoneRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        FromKind = body.FromKind!.Trim(),
                        TxnId = txnId,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("currency_exchange account=" + account
                    + " id=" + body.CharacterId.Value
                    + " from=" + result.FromKind
                    + " to=" + result.ToKind
                    + " cost=" + result.FromCost
                    + " gain=" + result.ToGain
                    + " reason=" + result.Reason
                    + " duplicate=" + result.Duplicate
                    + " txn=" + txnId
                    + " ok=" + result.Ok);

                await WriteAsync(context, 200, new
                {
                    ok = result.Ok,
                    reason = result.Reason,
                    from_kind = result.FromKind,
                    to_kind = result.ToKind,
                    from_cost = result.FromCost,
                    to_gain = result.ToGain,
                    from_amount = result.FromAmount,
                    to_amount = result.ToAmount,
                    ratio = result.Ratio,
                    highest_name = result.HighestName,
                    duplicate = result.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "currency_exchange", account, ex);
            }
        });
    }
}
