using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>N12 英灵图鉴只读 HTTP 口。收集度与被动只在角色服算，不做卡组战斗。</summary>
public static class HeroCodexApi
{
    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var character = new CharacterService.CharacterServiceClient(
            GrpcChannel.ForAddress(config.CharacterUpstream));

        app.MapGet("/hero-codex", async context =>
        {
            if (!Authorize(context, tokens, "hero_codex", out var account))
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
                var reply = await character.DescribeHeroCodexAsync(
                    new DescribeHeroCodexRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                if (!reply.Ok)
                {
                    await WriteAsync(context, 404, new { ok = false, error = "character_not_found" });
                    return;
                }

                var entries = new List<object>(reply.Entries.Count);
                foreach (var row in reply.Entries)
                {
                    entries.Add(new
                    {
                        id = row.Id,
                        name = row.Name,
                        unlock_main_stage = row.UnlockMainStage,
                        collected = row.Collected,
                        passive_pct_bps = row.PassivePctBps,
                    });
                }

                await WriteAsync(context, 200, new
                {
                    ok = true,
                    character_id = characterId,
                    main_stage = reply.MainStage,
                    collected_count = reply.CollectedCount,
                    entry_count = reply.EntryCount,
                    collection_pct_bps = reply.CollectionPctBps,
                    total_passive_pct_bps = reply.TotalPassivePctBps,
                    passive_kind = reply.PassiveKind,
                    entries,
                });
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
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
        await context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}