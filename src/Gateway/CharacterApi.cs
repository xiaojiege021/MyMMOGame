using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// 建角色的入口。账号从令牌里取，不信请求体——客户端改不了自己是谁。
/// 真正的检查与落库在角色服（角色的唯一写入），这里只做鉴权、转发和错误码翻译。
/// </summary>
public static class CharacterApi
{
    public sealed record CreateBody(string? Name, string? Race, string? Appearance, string? ClassId, string? Branch);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app, TokenStore tokens, GatewayConfig config)
    {
        // 通道建一次复用；地址在启动配置里定死，运行期不变。
        var channel = GrpcChannel.ForAddress(config.CharacterUpstream);
        var client = new CharacterService.CharacterServiceClient(channel);

        app.MapPost("/character", async context =>
        {
            var token = TokenFrom(context.Request);
            if (!tokens.TryResolve(token, DateTimeOffset.UtcNow, out var account, out _))
            {
                Console.WriteLine("character_reject reason=token");
                await WriteAsync(context, 401, new { error = "invalid_token" });
                return;
            }

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
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}
