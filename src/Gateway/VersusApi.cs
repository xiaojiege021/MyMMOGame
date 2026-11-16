using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// N5：竞技场与战场的 HTTP 进场口。只做鉴权、复核归属、从角色服取职业与分支事实值，
/// 再告诉客户端该怎么连网关上的 /battle WebSocket——战斗仍在战斗服按 P2-3a / P2-3b 开战。
/// 不算伤害、不发奖、不做匹配算法；屏上的假按钮不在这张卡。
/// </summary>
public static class VersusApi
{
    public sealed record ArenaEnterBody(ulong? CharacterId, string? ArenaMode, string? Party);

    public sealed record BattlefieldEnterBody(ulong? CharacterId, string? Match, string? Team);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var arenaPath = Path.Combine("config", "arena_match.json");
        VersusArenaModes arenaModes;
        try
        {
            arenaModes = VersusArenaModes.Load(arenaPath);
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("versus_arena_modes=config_error detail=" + ex.Message);
            arenaModes = VersusArenaModes.MissingTable();
        }

        if (arenaModes.Missing)
        {
            Console.WriteLine("versus_arena_modes=missing");
        }
        else
        {
            Console.WriteLine("versus_arena_modes=ok path=" + arenaPath);
        }

        var channel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var characters = new CharacterService.CharacterServiceClient(channel);

        app.MapPost("/versus/arena/enter", async context =>
        {
            if (!TryAuthorize(context, tokens, "versus_arena_enter", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            ArenaEnterBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<ArenaEnterBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.ArenaMode))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (arenaModes.Missing)
            {
                await WriteAsync(context, 200, new { ok = false, reason = "arena_unavailable" });
                return;
            }

            var modeId = body.ArenaMode.Trim();
            if (!arenaModes.TryPlayers(modeId, out var partySize))
            {
                await WriteAsync(context, 200, new { ok = false, reason = "arena_mode_unknown" });
                return;
            }

            if (!await OwnsAsync(characters, account, body.CharacterId.Value, context))
            {
                await WriteAsync(context, 404, new { error = "character_not_found" });
                return;
            }

            var profile = await LoadProfileAsync(characters, body.CharacterId.Value, context);
            if (profile is null || !profile.Found)
            {
                await WriteAsync(context, 404, new { error = "character_not_found" });
                return;
            }

            var party = (body.Party ?? "").Trim();
            if (party.Length == 0)
            {
                party = "arena-" + Guid.NewGuid().ToString("N");
            }

            Console.WriteLine("versus_arena_enter account=" + account
                + " id=" + body.CharacterId.Value
                + " mode=" + modeId
                + " party_size=" + partySize
                + " party=" + party
                + " class=" + profile.ClassId
                + " branch=" + profile.Branch);

            await WriteAsync(context, 200, new
            {
                ok = true,
                reason = "",
                battle_path = "/battle",
                character_id = body.CharacterId.Value,
                class_id = profile.ClassId,
                branch_id = profile.Branch,
                arena_mode = modeId,
                party_size = partySize,
                party,
            });
        });

        app.MapPost("/versus/battlefield/enter", async context =>
        {
            if (!TryAuthorize(context, tokens, "versus_battlefield_enter", out var account))
            {
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

            BattlefieldEnterBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<BattlefieldEnterBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.CharacterId == 0
                || string.IsNullOrWhiteSpace(body.Match))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var team = (body.Team ?? "").Trim().ToLowerInvariant();
            if (team is not ("a" or "b"))
            {
                await WriteAsync(context, 200, new { ok = false, reason = "battlefield_team_invalid" });
                return;
            }

            if (!await OwnsAsync(characters, account, body.CharacterId.Value, context))
            {
                await WriteAsync(context, 404, new { error = "character_not_found" });
                return;
            }

            var profile = await LoadProfileAsync(characters, body.CharacterId.Value, context);
            if (profile is null || !profile.Found)
            {
                await WriteAsync(context, 404, new { error = "character_not_found" });
                return;
            }

            var matchId = body.Match.Trim();
            if (matchId.Length > 64)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            Console.WriteLine("versus_battlefield_enter account=" + account
                + " id=" + body.CharacterId.Value
                + " match=" + matchId
                + " team=" + team
                + " class=" + profile.ClassId
                + " branch=" + profile.Branch);

            await WriteAsync(context, 200, new
            {
                ok = true,
                reason = "",
                battle_path = "/battle",
                character_id = body.CharacterId.Value,
                class_id = profile.ClassId,
                branch_id = profile.Branch,
                match = matchId,
                team,
                battlefield = matchId,
            });
        });
    }

    static async Task<DescribeBattleProfileResponse?> LoadProfileAsync(
        CharacterService.CharacterServiceClient characters,
        ulong characterId,
        HttpContext context)
    {
        try
        {
            return await characters.DescribeBattleProfileAsync(
                new DescribeBattleProfileRequest { CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: context.RequestAborted);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    static async Task<bool> OwnsAsync(
        CharacterService.CharacterServiceClient characters,
        string account,
        ulong characterId,
        HttpContext context)
    {
        try
        {
            await characters.ListInventoryAsync(
                new ListInventoryRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: context.RequestAborted);
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }

    static bool TryAuthorize(HttpContext context, TokenStore tokens, string what, out string account)
    {
        var token = TokenFrom(context.Request);
        if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out account, out _))
        {
            Console.WriteLine(what + "_reject reason=token");
            return false;
        }

        return true;
    }

    static string? TokenFrom(HttpRequest request)
    {
        var fromQuery = request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(fromQuery))
        {
            return fromQuery;
        }

        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return header[prefix.Length..].Trim();
        }

        return null;
    }

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(body, Json, context.RequestAborted);
    }
}