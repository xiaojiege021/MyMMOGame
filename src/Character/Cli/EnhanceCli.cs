using Npgsql;

using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>强化 / 转移 / 结晶的只读与连库核对。</summary>
internal static class EnhanceCli
{
    public static int? TryReadOnly()
    {
        // 强化表只读核对：与启动共用同一套校验（三档材料要在物品表里、金币要在币种表里），
        // 不连库也不启动进程，坏配置能在这一步直接看出来。
        if (enhanceState)
        {
            try
            {
                var characterConfig = CharacterConfig.Load(configPath);
                var items = ItemTable.Load("config/items.json");
                var currencies = CurrencyTable.Load("config/currency.json");
                var table = EnhanceTable.Load(enhanceConfigPath ?? characterConfig.EnhanceTable, items, currencies);
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                Console.WriteLine("enhance_table_state=ok version=" + table.Version
                    + " max_level=" + table.MaxLevel
                    + " gold_kind=" + table.GoldKind
                    + " tiers=" + string.Join(",", table.Tiers.Select(
                        t => t.Id + ":" + t.FromLevel + "-" + t.ToLevel + ":" + t.StoneId))
                    + " rates=" + string.Join(",", table.Tiers.Select(
                        t => t.SuccessRate.ToString("0.##", culture)))
                    + " gold=" + string.Join(",", table.Tiers.Select(t => t.GoldCost))
                    + " stones=" + string.Join(",", table.Tiers.Select(t => t.StoneCost))
                    + " coupon=" + table.ProtectItemId
                    + " coupon_bind=always");
                Console.WriteLine("enhance_gate=ok target=7 tier=" + table.TierFor(7).Id
                    + " stone=" + table.TierFor(7).StoneId
                    + " target=12 tier=" + table.TierFor(12).Id);
            }
            catch (ConfigException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }
            catch (IOException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }

            return 0;
        }

        // 转移表只读核对：与启动共用同一套校验（转移石要在物品表里、费用表要照
        // 强化上限与递增关系），不连库也不启动进程，坏配置能在这一步直接看出来。
        if (transferState)
        {
            try
            {
                var characterConfig = CharacterConfig.Load(configPath);
                var items = ItemTable.Load("config/items.json");
                var currencies = CurrencyTable.Load("config/currency.json");
                var enhanceTable = EnhanceTable.Load(
                    enhanceConfigPath ?? characterConfig.EnhanceTable, items, currencies);
                var table = TransferTable.Load(
                    transferConfigPath ?? characterConfig.TransferTable, items, currencies, enhanceTable);
                Console.WriteLine("transfer_table_state=ok version=" + table.Version
                    + " max_level=" + table.MaxLevel
                    + " transfer_stone=" + table.StoneItemId
                    + " transfer_stone_cost=" + table.StoneCost
                    + " transfer_gold_kind=" + table.GoldKind
                    + " transfer_gold=" + string.Join(",", table.GoldCosts));
                Console.WriteLine("transfer_gate=ok level=1 gold=" + table.GoldCostFor(1)
                    + " level=" + table.MaxLevel + " gold=" + table.GoldCostFor(table.MaxLevel));
            }
            catch (ConfigException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }
            catch (IOException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }

            return 0;
        }

        // 结晶表只读核对：与启动共用同一套校验（三类写定、装备部位不能漏配、
        // 结晶要在物品表里），不连库也不启动进程，坏配置能在这一步直接看出来。
        if (crystalState)
        {
            try
            {
                var characterConfig = CharacterConfig.Load(configPath);
                var items = ItemTable.Load("config/items.json");
                var table = CrystalTable.Load(crystalConfigPath ?? characterConfig.CrystalTable, items);
                Console.WriteLine("crystal_table_state=ok version=" + table.Version
                    + " crystal_sockets=" + table.SocketCount
                    + " crystal_categories=" + string.Join(",", CrystalTable.WrittenCategories)
                    + " crystal_parts=" + string.Join(",", table.EquipmentParts
                        .OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value))
                    + " crystal_allowed=" + string.Join(",", CrystalTable.WrittenCategories
                        .Select(c => c + ":" + string.Join("+", table.CategoryParts[c])))
                    + " crystal_items=" + string.Join(",", table.Crystals
                        .OrderBy(c => c.Id).Select(c => c.Id + ":" + c.Category + ":" + c.FixedValue)));
                Console.WriteLine("crystal_gate=ok " + string.Join(" ", CrystalTable.WrittenCategories
                    .Select(c => c + "=" + string.Join("+", table.CategoryParts[c]))));
            }
            catch (ConfigException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }
            catch (IOException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }

            return 0;
        }

        return null;
    }

    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        var dataSource = ctx.DataSource;
        if (enhanceLevelOf != 0 || enhanceTxnCount != null || couponCountOf != 0
            || rawEnhanceId != 0)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var enhanceTable = EnhanceTable.Load(
                enhanceConfigPath ?? characterConfig.EnhanceTable,
                items, CurrencyTable.Load("config/currency.json"));
            var enhances = new EnhanceRepository(dataSource, enhanceTable, items);

            if (rawEnhanceId != 0)
            {
                try
                {
                    await enhances.RawWriteLevelAsync(
                        rawEnhanceId, rawEnhanceSlot, rawEnhanceLevel, CancellationToken.None);
                    Console.WriteLine("raw_enhance=written id=" + rawEnhanceId
                        + " slot=" + rawEnhanceSlot + " level=" + rawEnhanceLevel);
                }
                catch (PostgresException ex)
                {
                    // 核对要看到的就是这个：库层 CHECK 把越界的强化等级挡住了。
                    Console.WriteLine("raw_enhance=rejected id=" + rawEnhanceId
                        + " level=" + rawEnhanceLevel + " sqlstate=" + ex.SqlState);
                }
            }

            if (enhanceLevelOf != 0)
            {
                var state = await enhances.StateAsync(enhanceLevelOf, enhanceSlotOf, CancellationToken.None);
                Console.WriteLine("enhance_state=ok id=" + enhanceLevelOf
                    + " slot=" + enhanceSlotOf
                    + " level=" + state.Level
                    + " gold=" + state.Gold
                    + " low=" + state.Low
                    + " mid=" + state.Mid
                    + " high=" + state.High);
            }

            if (enhanceTxnCount != null)
            {
                Console.WriteLine("enhance_txn_count="
                    + await enhances.TxnCountAsync(enhanceTxnCount, CancellationToken.None)
                    + " txn=" + enhanceTxnCount);
            }

            if (couponCountOf != 0)
            {
                Console.WriteLine("coupon_count="
                    + await enhances.CouponCountAsync(couponCountOf, CancellationToken.None)
                    + " id=" + couponCountOf
                    + " item=" + enhanceTable.ProtectItemId);
            }
        }

        if (transferStateOf != 0 || transferTxnCount != null)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var currencies = CurrencyTable.Load("config/currency.json");
            var enhanceTable = EnhanceTable.Load(
                enhanceConfigPath ?? characterConfig.EnhanceTable, items, currencies);
            var transferTable = TransferTable.Load(
                transferConfigPath ?? characterConfig.TransferTable,
                items, currencies, enhanceTable);
            var transfers = new TransferRepository(dataSource, transferTable, items);

            if (transferStateOf != 0)
            {
                var state = await transfers.StateAsync(
                    transferStateOf, transferFromSlot, transferToSlot, CancellationToken.None);
                Console.WriteLine("transfer_state=ok id=" + transferStateOf
                    + " from=" + transferFromSlot + " to=" + transferToSlot
                    + " from_level=" + state.FromLevel
                    + " to_level=" + state.ToLevel
                    // 显式输出小写，和网关回包以及核对脚本读到的布尔形状一致。
                    + " from_bound=" + (state.FromBound ? "true" : "false")
                    + " to_bound=" + (state.ToBound ? "true" : "false")
                    + " gold=" + state.Gold
                    + " stone=" + state.StoneLeft);
            }

            if (transferTxnCount != null)
            {
                Console.WriteLine("transfer_txn_count="
                    + await transfers.TxnCountAsync(transferTxnCount, CancellationToken.None)
                    + " txn=" + transferTxnCount);
            }
        }

        if (crystalStateOf != 0 || crystalTxnCount != null)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var items = ItemTable.Load("config/items.json");
            var crystalTable = CrystalTable.Load(
                crystalConfigPath ?? characterConfig.CrystalTable, items);
            var crystals = new CrystalRepository(dataSource, crystalTable, items, characterConfig);

            if (crystalStateOf != 0)
            {
                var state = await crystals.StateAsync(
                    crystalStateOf, crystalSlotOf, crystalItemOf ?? "", CancellationToken.None);
                Console.WriteLine("crystal_state=ok id=" + crystalStateOf
                    + " slot=" + crystalSlotOf
                    + " filled=" + state.Filled
                    + " installed=" + state.Installed
                    + " left=" + state.CrystalLeft
                    + " gold=" + state.Gold
                    + " enhance=" + state.EnhanceLevel
                    // 显式输出小写，和网关回包以及核对脚本读到的布尔形状一致。
                    + " bound=" + (state.Bound ? "true" : "false"));
            }

            if (crystalTxnCount != null)
            {
                Console.WriteLine("crystal_txn_count="
                    + await crystals.TxnCountAsync(crystalTxnCount, CancellationToken.None)
                    + " txn=" + crystalTxnCount);
            }
        }

        return null;
    }

}