using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：货币加减 / 读回 / 发奖。</summary>
public static partial class CharacterApi
{
    static void RegisterCurrency(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // 货币加减。金币、灵石五档、元宝同一个口子，扣成负数由角色服拒。
        app.MapPost("/currency/add", async context =>
        {
            if (!TryAuthorize(context, tokens, "currency_add", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            CurrencyBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CurrencyBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.Delta is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var balance = await client.AddCurrencyAsync(
                    new AddCurrencyRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Kind = body.Kind ?? "",
                        Delta = body.Delta.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("currency account=" + account
                    + " id=" + body.CharacterId.Value
                    + " kind=" + balance.Kind
                    + " delta=" + body.Delta.Value
                    + " amount=" + balance.Amount);

                await WriteAsync(context, 200, new
                {
                    kind = balance.Kind,
                    amount = balance.Amount,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "currency_add", account, ex);
            }
        });

        // 读回全部币种，没记过账的按 0。
        app.MapGet("/currency", async context =>
        {
            if (!TryAuthorize(context, tokens, "currency_get", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var balances = await client.GetCurrencyAsync(
                    new GetCurrencyRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                await WriteAsync(context, 200, new
                {
                    balances = balances.Balances.Select(b => new { kind = b.Kind, amount = b.Amount }),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "currency_get", account, ex);
            }
        });

        // 发一笔奖。整笔在角色服的一个事务里，事务号重复就不发第二次。
        app.MapPost("/grant", async context =>
        {
            if (!TryAuthorize(context, tokens, "grant", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            GrantBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<GrantBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.Entries is null || body.Entries.Count == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var request = new ApplyGrantRequest
            {
                Account = account,
                CharacterId = body.CharacterId.Value,
                TxnId = (body.TxnId ?? "").Trim(),
            };

            foreach (var entry in body.Entries)
            {
                if (entry.Currency is not null)
                {
                    request.Entries.Add(new Mmo.Proto.Character.GrantEntry
                    {
                        Currency = new CurrencyDelta
                        {
                            Kind = entry.Currency.Kind ?? "",
                            Delta = entry.Currency.Delta ?? 0,
                        },
                    });
                }
                else if (entry.Item is not null && entry.Item.Slot is not null)
                {
                    request.Entries.Add(new Mmo.Proto.Character.GrantEntry
                    {
                        Item = new ItemDelta
                        {
                            Slot = entry.Item.Slot.Value,
                            ItemId = entry.Item.ItemId ?? "",
                            Count = entry.Item.Count ?? 0,
                        },
                    });
                }
                else
                {
                    await WriteAsync(context, 400, new { error = "entry_invalid" });
                    return;
                }
            }

            try
            {
                var result = await client.ApplyGrantAsync(
                    request,
                    deadline: DateTime.UtcNow.AddSeconds(10),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("grant account=" + account
                    + " id=" + body.CharacterId.Value
                    + " txn=" + request.TxnId
                    + " duplicate=" + result.Duplicate
                    + " entries=" + request.Entries.Count);

                await WriteAsync(context, 200, new
                {
                    duplicate = result.Duplicate,
                    balances = result.Balances.Select(b => new { kind = b.Kind, amount = b.Amount }),
                    slots = result.Slots.Select(s => new
                    {
                        slot = s.Slot,
                        item_id = s.ItemId,
                        count = s.Count,
                        bound = s.Bound,
                    }),
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "grant", account, ex);
            }
        });
    }
}
