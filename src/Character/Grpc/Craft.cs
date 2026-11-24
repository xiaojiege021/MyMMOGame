using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

// 14.8 D2a–d：CharacterGrpcService 按域拆出的分片，纯搬代码，判定与回包不变。
public sealed partial class CharacterGrpcService
{

    /// <summary>
    /// K3：配方目录。材料 / 产出 / 费用 / 成功率 / 耗时 / 解锁等级全部从
    /// config/recipes.json 读；unlocked 只是按角色等级算的展示事实，判定仍在服务端。
    /// </summary>
    public override async Task<DescribeRecipesResponse> DescribeRecipes(
        DescribeRecipesRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        var level = await inventory.LevelAsync(
            account, (long)request.CharacterId, context.CancellationToken);
        if (level is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var response = new DescribeRecipesResponse
        {
            Ok = true,
            TableVersion = recipeTable.Version,
            Pending = recipeTable.Pending,
            Level = level.Value,
        };

        foreach (var profession in recipeTable.Professions)
        {
            response.Professions.Add(new RecipeProfession
            {
                Id = profession.Id,
                Name = profession.Name,
            });
        }

        foreach (var recipe in recipeTable.Recipes)
        {
            var fact = new RecipeFact
            {
                Id = recipe.Id,
                Profession = recipe.Profession,
                UnlockLevel = recipe.UnlockLevel,
                Unlocked = level.Value >= recipe.UnlockLevel,
                SuccessPct = recipe.SuccessPct,
                CraftTimeS = recipe.CraftTimeS,
                Note = recipe.Note,
                Output = new RecipeOutput
                {
                    ItemId = recipe.OutputItemId,
                    Count = recipe.OutputCount,
                },
            };
            foreach (var input in recipe.Inputs)
            {
                fact.Inputs.Add(new RecipeInput { ItemId = input.ItemId, Count = input.Count });
            }

            foreach (var fee in recipe.Fee)
            {
                fact.Fee.Add(new RecipeFeeLine { Kind = fee.Kind, Amount = fee.Amount });
            }

            response.Recipes.Add(fact);
        }

        return response;
    }


    /// <summary>
    /// K3：开始制作。材料 / 费用 / 解锁等级 / 产出 / 耗时都只在服务端读配置；
    /// 请求只带配方编号，事务号由网关生成。拒绝是 ok=false + reason，数值一个字节不动。
    /// </summary>
    public override async Task<StartCraftResponse> StartCraft(
        StartCraftRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var recipeId = (request.RecipeId ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        if (recipeId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "recipe_missing"));
        }

        if (txnId.Length == 0 || txnId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        if (!await inventory.OwnsAsync(
                account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        try
        {
            var job = await crafts.StartAsync(
                (long)request.CharacterId, recipeId, txnId, context.CancellationToken);
            logger.LogInformation(
                "craft_start account={Account} id={Id} recipe={Recipe} txn={Txn} "
                + "ready_at={ReadyAt} remaining_s={Remaining}",
                account, request.CharacterId, job.RecipeId, txnId, job.ReadyAt, job.RemainingSeconds);
            return new StartCraftResponse
            {
                Ok = true,
                RecipeId = job.RecipeId,
                Profession = job.Profession,
                CraftTimeS = job.CraftTimeS,
                StartedAtUnix = job.StartedAt.ToUnixTimeSeconds(),
                ReadyAtUnix = job.ReadyAt.ToUnixTimeSeconds(),
                RemainingS = job.RemainingSeconds,
            };
        }
        catch (CraftException ex)
        {
            logger.LogInformation(
                "craft_start_reject account={Account} id={Id} recipe={Recipe} txn={Txn} reason={Reason}",
                account, request.CharacterId, recipeId, txnId, ex.Code);
            if (string.Equals(ex.Code, "character_not_found", StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.NotFound, ex.Code));
            }

            // 未生效的事实值一律留 0 / 空串，客户端不能拿拒绝回包当成一次制作。
            return new StartCraftResponse
            {
                Ok = false,
                Reason = ex.Code,
                Duplicate = string.Equals(ex.Code, "duplicate", StringComparison.Ordinal),
            };
        }
    }


    /// <summary>
    /// K3：收取。过了 ready_at 才掷 success_pct；成功才入包，失败材料照扣、产出为 0。
    /// 真正的掷点失败是 ok=true + success=false，与「被规则拒绝」分开。
    /// </summary>
    public override async Task<FinishCraftResponse> FinishCraft(
        FinishCraftRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        var txnId = (request.TxnId ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        if (txnId.Length == 0 || txnId.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "txn_missing"));
        }

        if (!await inventory.OwnsAsync(
                account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        CraftFinishOutcome outcome;
        try
        {
            outcome = await crafts.FinishAsync(
                (long)request.CharacterId, txnId, context.CancellationToken);
        }
        catch (CraftException ex)
        {
            logger.LogInformation(
                "craft_finish_reject account={Account} id={Id} txn={Txn} reason={Reason}",
                account, request.CharacterId, txnId, ex.Code);
            return new FinishCraftResponse
            {
                Ok = false,
                Reason = ex.Code,
            };
        }

        if (outcome.Duplicate)
        {
            logger.LogInformation(
                "craft_finish_replay account={Account} id={Id} txn={Txn}",
                account, request.CharacterId, txnId);
            return new FinishCraftResponse
            {
                Ok = false,
                Reason = "duplicate",
                Duplicate = true,
            };
        }

        // B3：掷点只在这里露给客户端一次（同一件不再掷，也没有重掷入口）。
        logger.LogInformation(
            "craft_finish account={Account} id={Id} recipe={Recipe} txn={Txn} "
            + "success={Success} output={Output}x{Count} attr={Attr} extras={Extras}",
            account, request.CharacterId, outcome.RecipeId, txnId,
            outcome.Success, outcome.OutputItemId, outcome.OutputCount,
            outcome.Roll?.Attr ?? 0, outcome.Roll?.Extras.Count ?? 0);

        var response = new FinishCraftResponse
        {
            Ok = true,
            RecipeId = outcome.RecipeId,
            Profession = outcome.Profession,
            Success = outcome.Success,
            OutputItemId = outcome.OutputItemId,
            OutputCount = outcome.OutputCount,
            SuccessPct = outcome.SuccessPct,
            CraftTimeS = outcome.CraftTimeS,
            ReadyAtUnix = outcome.ReadyAt.ToUnixTimeSeconds(),
            CraftAttr = outcome.Roll?.Attr ?? 0,
        };
        if (outcome.Roll is not null)
        {
            foreach (var extra in outcome.Roll.Extras)
            {
                response.CraftExtras.Add(new CraftExtra { Attr = extra.Attr, Value = extra.Value });
            }
        }

        return response;
    }


    /// <summary>K3：当前有没有活、还差几秒。只读，不掷点、不改库。</summary>
    public override async Task<DescribeCraftResponse> DescribeCraft(
        DescribeCraftRequest request, ServerCallContext context)
    {
        var account = (request.Account ?? "").Trim();
        if (account.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_missing"));
        }

        if (request.CharacterId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "character_missing"));
        }

        if (!await inventory.OwnsAsync(
                account, (long)request.CharacterId, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "character_not_found"));
        }

        var job = await crafts.DescribeAsync(
            (long)request.CharacterId, context.CancellationToken);
        if (job is null)
        {
            return new DescribeCraftResponse { Ok = true, Active = false };
        }

        return new DescribeCraftResponse
        {
            Ok = true,
            Active = true,
            RecipeId = job.RecipeId,
            Profession = job.Profession,
            CraftTimeS = job.CraftTimeS,
            StartedAtUnix = job.StartedAt.ToUnixTimeSeconds(),
            ReadyAtUnix = job.ReadyAt.ToUnixTimeSeconds(),
            RemainingS = job.RemainingSeconds,
        };
    }
}
