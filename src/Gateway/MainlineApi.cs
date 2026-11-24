using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// C1 主线十二场的 HTTP 入口。网关只认令牌、复核角色归属、生成事务号；
/// 十二场内容与门槛全在 config/mainline.json 和角色库里，请求带不进奖励、
/// 等级、境界、门槛数值，也带不进自己想推进到第几场之外的事实值。
/// N7：交任务须带与那场主线战斗 SettleExp 相同的 txn_id，在同一笔 grant_txn 里发奖并推进场次。
/// </summary>
public static class MainlineApi
{
    public sealed record TurnInBody(ulong? CharacterId, int? Stage, string? Alignment, string? TxnId);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        var characterChannel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var character = new CharacterService.CharacterServiceClient(characterChannel);

        // 只读：角色上的主线场次、立仙或立魔，以及服务端算出的下一场编号。
        app.MapGet("/mainline", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            var characterId = ParseId(context.Request.Query["character_id"].ToString());
            if (characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var reply = await character.DescribeMainlineAsync(
                    new DescribeMainlineRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("mainline account=" + account
                    + " id=" + characterId
                    + " main_stage=" + reply.MainStage
                    + " alignment=" + reply.Alignment
                    + " next_stage=" + reply.NextStage
                    + " stages=" + reply.StageCount
                    + " config_missing=" + (reply.ConfigMissing ? "true" : "false"));

                await WriteAsync(context, 200, new
                {
                    found = reply.Found,
                    character_id = reply.CharacterId,
                    main_stage = reply.MainStage,
                    alignment = reply.Alignment,
                    next_stage = reply.NextStage,
                    stage_count = reply.StageCount,
                    config_missing = reply.ConfigMissing,
                    level = reply.Level,
                    realm = reply.Realm,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("mainline_reject account=" + account + " detail=" + ex.Status.Detail);
                await WriteAsync(context, ex.StatusCode == StatusCode.NotFound ? 404 : 502,
                    new { error = ex.Status.Detail });
            }
        });

        // 交任务：场号、（仅第 10 场的）立仙或立魔、以及那场战斗结算的 txn_id。
        app.MapPost("/mainline/turn-in", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            TurnInBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<TurnInBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var txnId = (body?.TxnId ?? "").Trim();
            if (body?.CharacterId is null || body.CharacterId == 0 || body.Stage is null
                || txnId.Length == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                if (!await GatewayAuth.OwnsAsync(character, account, body.CharacterId.Value, context))
                {
                    await WriteAsync(context, 404, new { error = "character_not_found" });
                    return;
                }
                var reply = await character.TurnInMainlineAsync(
                    new TurnInMainlineRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        TxnId = txnId,
                        Stage = body.Stage.Value,
                        Alignment = (body.Alignment ?? "").Trim(),
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("mainline_turn_in account=" + account
                    + " id=" + body.CharacterId.Value
                    + " stage=" + body.Stage.Value
                    + " txn=" + txnId
                    + " ok=" + reply.Ok
                    + " reason=" + reply.Reason
                    + " duplicate=" + reply.Duplicate
                    + " main_stage=" + reply.MainStage
                    + " alignment=" + reply.Alignment);

                await WriteAsync(context, 200, new
                {
                    ok = reply.Ok,
                    reason = reply.Reason,
                    duplicate = reply.Duplicate,
                    stage = reply.Stage,
                    main_stage = reply.MainStage,
                    alignment = reply.Alignment,
                    txn_id = reply.TxnId,
                });
            }
            catch (RpcException ex)
            {
                Console.WriteLine("mainline_turn_in_reject account=" + account
                    + " detail=" + ex.Status.Detail);
                await WriteAsync(context, ex.StatusCode == StatusCode.NotFound ? 404 : 502,
                    new { error = ex.Status.Detail });
            }
        });
    }

    static ulong ParseId(string raw) =>
        ulong.TryParse(raw, out var value) ? value : 0;
    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
