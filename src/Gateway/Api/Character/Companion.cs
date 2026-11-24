using System.Text.Json;
using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>CharacterApi HTTP：B4 坐骑（骑上 / 下 / 读）与灵兽（只读收集）。</summary>
public static partial class CharacterApi
{
    static void RegisterCompanion(
        WebApplication app, CharacterService.CharacterServiceClient client, TokenStore tokens)
    {
        // B4：骑上某一格背包里的坐骑。请求只给格号——档位与 speed_bps 在
        // config/mount.json，客户端带不进数值；业务拒绝一律 200 + ok=false
        // （slot_empty / not_a_mount / already_mounted / mount_occupied）。
        app.MapPost("/character/mount", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            MountUpBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<MountUpBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null || body.Slot is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var reply = await client.MountUpAsync(
                    new MountUpRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                        BagSlot = body.Slot.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("character_mount account=" + account
                    + " id=" + body.CharacterId.Value
                    + " bag_slot=" + body.Slot.Value
                    + " item=" + reply.ItemId
                    + " tier=" + reply.Tier
                    + " speed_bps=" + reply.SpeedBps
                    + " ok=" + reply.Ok
                    + " reason=" + reply.Reason);

                await WriteMountAsync(context, reply);
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "character_mount", account, ex);
            }
        });

        // B4：下坐骑。背包行一直没动过，回包里 bag_slot 就是它原来那一格；
        // 没骑时 reason=not_mounted，不算错误。
        app.MapPost("/character/dismount", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            DismountBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<DismountBody>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            if (body?.CharacterId is null)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var reply = await client.DismountAsync(
                    new DismountRequest
                    {
                        Account = account,
                        CharacterId = body.CharacterId.Value,
                    },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                Console.WriteLine("character_dismount account=" + account
                    + " id=" + body.CharacterId.Value
                    + " item=" + reply.ItemId
                    + " bag_slot=" + reply.BagSlot
                    + " ok=" + reply.Ok
                    + " reason=" + reply.Reason);

                await WriteMountAsync(context, reply);
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "character_dismount", account, ex);
            }
        });

        // B4：当前骑的是哪一只、背包里一共有哪几只。只读，不改背包。
        app.MapGet("/character/mounts", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var reply = await client.DescribeMountAsync(
                    new DescribeMountRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                var owned = new List<object>(reply.Owned.Count);
                foreach (var row in reply.Owned)
                {
                    owned.Add(new
                    {
                        item_id = row.ItemId,
                        name = row.Name,
                        tier = row.Tier,
                        speed_bps = row.SpeedBps,
                        bag_slot = row.BagSlot,
                        mounted = row.Mounted,
                    });
                }

                await WriteAsync(context, 200, new
                {
                    ok = true,
                    character_id = characterId,
                    equipped_item_id = reply.EquippedItemId,
                    equipped_speed_bps = reply.EquippedSpeedBps,
                    owned,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "character_mounts", account, ex);
            }
        });

        // B4：灵兽收集度与生活协助合计。只做收集 / 外观 / 生活协助，不进伤害公式。
        app.MapGet("/character/spirit-beasts", async context =>
        {
            var account = GatewayAuth.AccountOf(context);

            if (!ulong.TryParse(context.Request.Query["character_id"].ToString(), out var characterId)
                || characterId == 0)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            try
            {
                var reply = await client.DescribeSpiritBeastAsync(
                    new DescribeSpiritBeastRequest { Account = account, CharacterId = characterId },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: context.RequestAborted);

                var entries = new List<object>(reply.Entries.Count);
                foreach (var row in reply.Entries)
                {
                    entries.Add(new
                    {
                        item_id = row.ItemId,
                        name = row.Name,
                        tier = row.Tier,
                        collected = row.Collected,
                        life_assist_pct_bps = row.LifeAssistPctBps,
                    });
                }

                await WriteAsync(context, 200, new
                {
                    ok = true,
                    character_id = characterId,
                    collected_count = reply.CollectedCount,
                    entry_count = reply.EntryCount,
                    collection_pct_bps = reply.CollectionPctBps,
                    total_life_assist_pct_bps = reply.TotalLifeAssistPctBps,
                    passive_kind = reply.PassiveKind,
                    entries,
                });
            }
            catch (RpcException ex)
            {
                await WriteRpcErrorAsync(context, "character_spirit_beasts", account, ex);
            }
        });
    }

    static Task WriteMountAsync(HttpContext context, MountResponse reply) =>
        WriteAsync(context, 200, new
        {
            ok = reply.Ok,
            reason = reply.Reason,
            item_id = reply.ItemId,
            tier = reply.Tier,
            speed_bps = reply.SpeedBps,
            bag_slot = reply.BagSlot,
        });
}
