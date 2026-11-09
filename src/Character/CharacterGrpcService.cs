using Grpc.Core;
using Mmo.Proto.Character;
using Npgsql;

namespace Character;

/// <summary>
/// 建角色的 gRPC 入口。校验收在服务端：种族要在表里、职业要在表里、
/// 分支必须属于这个职业。种族与外观原样落库，不参与任何数值计算。
/// </summary>
public sealed class CharacterGrpcService(
    CharacterRepository characters,
    RaceTable races,
    BranchTable branches,
    ClassIds classes,
    ILogger<CharacterGrpcService> logger) : CharacterService.CharacterServiceBase
{
    public override async Task<CreateCharacterResponse> CreateCharacter(
        CreateCharacterRequest request,
        ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var name = (request.Name ?? "").Trim();
        var race = (request.Race ?? "").Trim();
        var classId = (request.ClassId ?? "").Trim();
        var branch = (request.Branch ?? "").Trim();
        var appearance = string.IsNullOrWhiteSpace(request.Appearance) ? "{}" : request.Appearance;

        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (name.Length == 0 || name.Length > 16)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "name_invalid"));
        }

        if (races.Find(race) is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown_race"));
        }

        if (!classes.Contains(classId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown_class"));
        }

        // 分支可以不填（15 级一转才定）；填了就必须属于这个职业。
        if (branch.Length > 0 && !branches.BelongsTo(branch, classId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "branch_mismatch"));
        }

        if (!await characters.AccountExistsAsync(account, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "account_not_found"));
        }

        if (await characters.NameTakenAsync(account, name, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, "name_taken"));
        }

        try
        {
            var created = await characters.CreateAsync(
                account,
                name,
                race,
                appearance,
                classId,
                branch.Length == 0 ? null : branch,
                context.CancellationToken);

            logger.LogInformation(
                "character_created id={Id} account={Account} race={Race} class={Class} branch={Branch}",
                created.Id, account, created.Race, created.ClassId, created.Branch);

            return new CreateCharacterResponse
            {
                CharacterId = (ulong)created.Id,
                Name = created.Name,
                Race = created.Race,
                Appearance = created.Appearance,
                ClassId = created.ClassId,
                Branch = created.Branch,
                Level = created.Level,
                Realm = created.Realm,
                MainStageEmpty = created.MainStageEmpty,
                AlignmentEmpty = created.AlignmentEmpty,
            };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 并发下两个同名一起进来，唯一索引是最后一道闸。
            throw new RpcException(new Status(StatusCode.AlreadyExists, "name_taken"));
        }
    }
}
