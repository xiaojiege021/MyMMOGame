using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：B6 觉醒（觉醒一次 / 只读状态）。</summary>
public static partial class CharacterApi
{
    static void RegisterAwaken(
        WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // B6：觉醒。请求只有角色与分支——技能编号由角色服按 config/awaken.json 现取，
        // 客户端带不进编号、带不进等级。业务拒绝一律 200 + ok=false
        // （not_enough_level / branch_missing / branch_locked / skill_not_found /
        // already_awakened）；重复觉醒只回 duplicate=true。
        app.MapPost("/character/awaken", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            AwakenBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<AwakenBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || string.IsNullOrWhiteSpace(body.BranchId))
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var reply = await client.AwakenAsync(
                    new AwakenRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        BranchId = body.BranchId.Trim(),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("character_awaken account=" + account
                    + " id=" + body.CharacterId.Value
                    + " branch=" + body.BranchId.Trim()
                    + " skill=" + reply.SkillId
                    + " level=" + reply.Level
                    + " ok=" + (reply.Ok ? "true" : "false")
                    + " reason=" + (reply.Reason.Length == 0 ? "none" : reply.Reason)
                    + " duplicate=" + (reply.Duplicate ? "true" : "false"));

                await WriteAsync(context, 200, new
                {
                    ok = reply.Ok,
                    reason = reply.Reason,
                    branch = reply.Branch,
                    skill_id = reply.SkillId,
                    level = reply.Level,
                    duplicate = reply.Duplicate,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "character_awaken", account, ex);
            }
        });

        // B6：只读觉醒状态。没觉醒也是 200（skill_id = 0），只有角色不存在才 404。
        app.MapGet("/character/awaken", async context =>
        {
            var account = GatewayAuth.AccountOf(context);
            if (!ulong.TryParse(context.Request.Query["character_id"], out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var reply = await client.DescribeAwakenAsync(
                    new DescribeAwakenRequest { CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                if (!reply.Found)
                {
                    await WriteAsync(context, 404, new { error = "character_missing" });
                    return;
                }

                Console.WriteLine("character_awaken_state account=" + account
                    + " id=" + characterId
                    + " branch=" + reply.Branch
                    + " skill=" + reply.SkillId);

                await WriteAsync(context, 200, new
                {
                    found = true,
                    branch = reply.Branch,
                    skill_id = reply.SkillId,
                    level = reply.Level,
                    awaken_at_ms = reply.AwakenAtMs,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "character_awaken_state", account, ex);
            }
        });
    }
}
