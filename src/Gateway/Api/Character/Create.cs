using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：建角色与写等级。</summary>
public static partial class CharacterApi
{
    static void RegisterCreate(WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        app.MapPost("/character", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            CreateBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CreateBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var request = new CreateCharacterRequest
            {
                Account = account,
                Name = (body.Name ?? "").Trim(),
                Race = (body.Race ?? "").Trim(),
                Appearance = body.Appearance ?? "",
                ClassId = (body.ClassId ?? "").Trim(),
                Branch = (body.Branch ?? "").Trim(),
            };

            try
            {
                var created = await client.CreateCharacterAsync(
                    request,
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("character_created account=" + account
                    + " id=" + created.CharacterId
                    + " race=" + created.Race
                    + " class=" + created.ClassId
                    + " branch=" + created.Branch
                    + " main_stage_empty=" + created.MainStageEmpty
                    + " alignment_empty=" + created.AlignmentEmpty);

                await WriteAsync(context, 200, new
                {
                    character_id = created.CharacterId,
                    name = created.Name,
                    race = created.Race,
                    appearance = created.Appearance,
                    @class = created.ClassId,
                    branch = created.Branch,
                    level = created.Level,
                    realm = created.Realm,
                    main_stage_empty = created.MainStageEmpty,
                    alignment_empty = created.AlignmentEmpty,
                });
            }
            catch (RpcException ex)
            {
                var code = ex.Status.Detail;
                var http = ex.StatusCode switch
                {
                    StatusCode.InvalidArgument => 400,
                    StatusCode.NotFound => 404,
                    StatusCode.AlreadyExists => 409,
                    StatusCode.Unavailable => 503,
                    _ => 502,
                };

                Console.WriteLine("character_reject account=" + account
                    + " code=" + code + " http=" + http);
                await WriteAsync(context, http, new { error = code });
            }
        });

        // 写等级。上限 100 由角色服判，网关只转；越界是 400，不是静默改成 100。
        app.MapPost("/character/level", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            LevelBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<LevelBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body is null || body.CharacterId is null || body.Level is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var updated = await client.SetLevelAsync(
                    new SetLevelRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        Level = body.Level.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("level_updated account=" + account
                    + " id=" + updated.CharacterId
                    + " level=" + updated.Level);

                await WriteAsync(context, 200, new
                {
                    character_id = updated.CharacterId,
                    level = updated.Level,
                });
            }
            catch (RpcException ex)
            {
                var code = ex.Status.Detail;
                var http = ex.StatusCode switch
                {
                    StatusCode.InvalidArgument => 400,
                    StatusCode.NotFound => 404,
                    StatusCode.Unavailable => 503,
                    _ => 502,
                };

                Console.WriteLine("level_reject account=" + account
                    + " code=" + code + " http=" + http);
                await WriteAsync(context, http, new { error = code });
            }
        });
    }
}
