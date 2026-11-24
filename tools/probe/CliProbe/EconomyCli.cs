using Npgsql;

using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>经济连库核对：拍卖结算 / 元宝寄售 / 灵石兑换 / 城池税 / 城战 / 挂单。</summary>
internal static class EconomyCli
{
    public static async Task<int?> TryRunDbAsync(CliContext ctx)
    {
        var dataSource = ctx.DataSource;
        if (auctionBuyerOf != 0)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var auctionItems = ItemTable.Load("config/items.json");
            var auctionCurrencies = CurrencyTable.Load("config/currency.json");
            var auctionTable = AuctionTable.Load(
                auctionConfigPath ?? characterConfig.AuctionTable, auctionCurrencies);
            // B5：成交的税率按挂单 channel 取，拍卖这条也要带上摆摊表。
            var auctionStalls = StallTable.Load(
                stallTablePath ?? characterConfig.StallTable);
            var auctions = new AuctionRepository(
                dataSource, auctionTable, auctionStalls, auctionItems, characterConfig);
            var ledger = await auctions.StateAsync(
                auctionBuyerOf, auctionSellerOf, auctionListingOf,
                auctionTxnOf ?? "", CancellationToken.None);
            Console.WriteLine("auction_state=ok buyer=" + auctionBuyerOf
                + " buyer_gold=" + ledger.BuyerGold
                + " seller=" + auctionSellerOf
                + " seller_gold=" + ledger.SellerGold
                + " total_gold=" + ledger.TotalGold
                + " listing=" + auctionListingOf
                + " state=" + ledger.ListingState
                + " price=" + ledger.Price
                + " tax=" + ledger.Tax
                + " net=" + ledger.Net
                + " settlements=" + ledger.Settlements
                + " txn=" + ledger.Txns
                + " buyer_items=" + ledger.BuyerItems
                + " seller_items=" + ledger.SellerItems
                + " total_items=" + ledger.TotalItems
                + " accounts=" + ledger.Accounts);
        }

        if (ingotBuyerOf != 0)
        {
            var characterConfig = CharacterConfig.Load(configPath);
            var ingotCurrencies = CurrencyTable.Load("config/currency.json");
            var ingotTable = AuctionTable.Load(
                auctionConfigPath ?? characterConfig.AuctionTable, ingotCurrencies);
            var ingots = new IngotRepository(dataSource, ingotTable);
            var ledger = await ingots.StateAsync(
                ingotBuyerOf, ingotSellerOf, ingotListingOf,
                ingotTxnOf ?? "", CancellationToken.None);
            Console.WriteLine("ingot_state=ok buyer=" + ingotBuyerOf
                + " buyer_gold=" + ledger.BuyerGold
                + " seller=" + ingotSellerOf
                + " seller_gold=" + ledger.SellerGold
                + " total_gold=" + ledger.TotalGold
                + " buyer_ingot=" + ledger.BuyerIngot
                + " seller_ingot=" + ledger.SellerIngot
                + " total_ingot=" + ledger.TotalIngot
                + " listing=" + ingotListingOf
                + " state=" + ledger.ListingState
                + " price=" + ledger.Price
                + " tax=" + ledger.Tax
                + " net=" + ledger.Net
                + " settlements=" + ledger.Settlements
                + " txn=" + ledger.Txns
                + " accounts=" + ledger.Accounts);
        }

        // P3-2a：直接走一次相邻档兑换，不经过网关、也不经过拍卖。
        if (exchangeStoneOf != 0)
        {
            var exchangeConfig = CharacterConfig.Load(configPath);
            var exchangeCurrencies = CurrencyTable.Load("config/currency.json");
            var exchangeTable = StoneExchangeTable.Load(
                exchangeConfig.StoneExchangeTable, exchangeCurrencies);
            var exchangeRepo = new StoneExchangeRepository(
                dataSource, exchangeCurrencies, exchangeTable, exchangeConfig);
            var outcome = await exchangeRepo.ExchangeAsync(
                exchangeStoneOf, exchangeStoneKind ?? "", exchangeStoneTxn ?? "",
                CancellationToken.None);
            if (outcome.Ok)
            {
                Console.WriteLine("stone_exchange=ok id=" + exchangeStoneOf
                    + " from=" + outcome.FromKind
                    + " to=" + outcome.ToKind
                    + " from_cost=" + outcome.FromCost
                    + " to_gain=" + outcome.ToGain
                    + " from_amount=" + outcome.FromAmount
                    + " to_amount=" + outcome.ToAmount
                    + " ratio=" + outcome.Ratio
                    + " highest=" + outcome.HighestName
                    + " duplicate=" + (outcome.Duplicate ? "true" : "false"));
            }
            else
            {
                Console.WriteLine("stone_exchange=" + outcome.Reason);
            }
        }

        // P3-2a：从库里独立读回五档余额与兑换笔数，证「兑换不经过拍卖」。
        if (stoneExchangeStateOf != 0)
        {
            var stateMoney = new CurrencyRepository(dataSource);
            var stateBalances = await stateMoney.AllAsync(
                stoneExchangeStateOf, CancellationToken.None);
            var stateConfig = CharacterConfig.Load(configPath);
            var stateTable = StoneExchangeTable.Load(
                stateConfig.StoneExchangeTable, CurrencyTable.Load("config/currency.json"));
            var tiers = string.Join(" ", stateTable.Tiers.Select(
                t => t + "=" + stateBalances.GetValueOrDefault(t, 0L)));
            long exchangeTxns;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"角色\".stone_exchange_txn where character_id = $1"))
            {
                read.Parameters.AddWithValue(stoneExchangeStateOf);
                exchangeTxns = Convert.ToInt64(
                    await read.ExecuteScalarAsync(CancellationToken.None));
            }

            Console.WriteLine("stone_exchange_state=ok id=" + stoneExchangeStateOf
                + " " + tiers
                + " exchange_txns=" + exchangeTxns);
        }

        // P3-2a：城池税收账本与拍卖销毁税是分开的两张表，各数各的。
        if (cityState)
        {
            long cityRows;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"社交\".city"))
            {
                cityRows = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }

            long cityTaxRows;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"社交\".city_tax_ledger"))
            {
                cityTaxRows = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }

            long auctionTaxRows;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"交易\".auction_settlement"))
            {
                auctionTaxRows = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }

            long auctionTaxSum;
            await using (var read = dataSource.CreateCommand(
                "select coalesce(sum(tax_gold), 0) from \"交易\".auction_settlement"))
            {
                auctionTaxSum = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }

            long stoneListingRows;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"交易\".stone_listing"))
            {
                stoneListingRows = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }

            Console.WriteLine("city_state=ok city_rows=" + cityRows
                + " city_tax_rows=" + cityTaxRows
                + " auction_tax_rows=" + auctionTaxRows
                + " auction_tax_sum=" + auctionTaxSum
                + " stone_listing_rows=" + stoneListingRows);
        }

        if (cityTaxRowTxn is not null)
        {
            var cfg = CharacterConfig.Load(configPath);
            var cityTbl = CityTable.Load(cfg.CityTable);
            var auctionTbl = AuctionTable.Load(
                cfg.AuctionTable, CurrencyTable.Load("config/currency.json"));
            var cityTaxRepo = new CityTaxRepository(dataSource, cityTbl, auctionTbl.GoldKind);
            var row = await cityTaxRepo.ReadRowAsync(cityTaxRowTxn, CancellationToken.None);
            if (row is null)
            {
                Console.WriteLine("city_tax_row=missing txn=" + cityTaxRowTxn);
            }
            else
            {
                Console.WriteLine("city_tax_row=ok txn=" + row.TxnId
                    + " city=" + row.CityId
                    + " source=" + row.Source
                    + " gross=" + row.GrossGold
                    + " tax=" + row.TaxGold);
            }
        }

        // P3-3a4：与真实服务共用同一个 Repository，验重放幂等与各类拒绝路径，
        // 不必每验一次都打一场城战。事务号、宗门与队伍都由核对脚本给，
        // 与生产链路走的是同一笔事务，不是另一套写入。
        if (applySiegeResultTxn != null)
        {
            var cfg = CharacterConfig.Load(configPath);
            var cityTable = CityTable.Load(cfg.CityTable);
            var policy = SiegeTerritoryPolicy.Load(cfg.SiegeTerritoryTable, cityTable);
            var repo = new SiegeTerritoryRepository(dataSource, cityTable, policy);
            var outcome = await repo.ApplyAsync(
                applySiegeResultTxn, applySiegeResultCity, applySiegeResultMatch,
                applySiegeResultTeam, applySiegeResultSect, CancellationToken.None);
            Console.WriteLine("siege_result_apply=txn=" + applySiegeResultTxn
                + " city=" + outcome.CityId
                + " ok=" + (outcome.Ok ? "true" : "false")
                + " reason=" + outcome.Reason
                + " duplicate=" + (outcome.Duplicate ? "true" : "false")
                + " owner=" + outcome.OwnerSect
                + " tax_pct=" + outcome.TaxPct);
        }

        // P3-3a4：写回之后从库里独立读回城池事实值，不看回包。
        if (siegeResultState)
        {
            long rows;
            await using (var read = dataSource.CreateCommand(
                "select count(*) from \"社交\".siege_result"))
            {
                rows = Convert.ToInt64(await read.ExecuteScalarAsync(CancellationToken.None));
            }
            Console.WriteLine("siege_result_rows=" + rows);

            await using (var read = dataSource.CreateCommand(
                "select city_id, coalesce(owner_sect, ''), tax_pct from \"社交\".city order by city_id"))
            {
                await using var reader = await read.ExecuteReaderAsync(CancellationToken.None);
                while (await reader.ReadAsync(CancellationToken.None))
                {
                    Console.WriteLine("siege_result_city=city=" + reader.GetString(0)
                        + " owner=" + reader.GetString(1)
                        + " tax_pct=" + reader.GetInt32(2));
                }
            }

            await using (var read = dataSource.CreateCommand(
                "select txn_id, match_id, winner_team, winner_sect, tax_pct "
                + "from \"社交\".siege_result order by recorded_at desc, txn_id desc limit 1"))
            {
                await using var reader = await read.ExecuteReaderAsync(CancellationToken.None);
                if (await reader.ReadAsync(CancellationToken.None))
                {
                    Console.WriteLine("siege_result_last=txn=" + reader.GetString(0)
                        + " match=" + reader.GetString(1)
                        + " winner_team=" + reader.GetInt32(2)
                        + " winner_sect=" + reader.GetString(3)
                        + " tax_pct=" + reader.GetInt32(4));
                }
            }
        }

        // P3-2a：给核对脚本看「兑换不经过拍卖」——兑换前后挂单数不变。
        if (stoneListingState)
        {
            long listed = 0;
            long sold = 0;
            await using (var read = dataSource.CreateCommand(
                "select state, count(*) from \"交易\".stone_listing group by state"))
            {
                await using var reader = await read.ExecuteReaderAsync(CancellationToken.None);
                while (await reader.ReadAsync(CancellationToken.None))
                {
                    var state = reader.GetString(0);
                    var count = reader.GetInt64(1);
                    if (string.Equals(state, "listed", StringComparison.Ordinal))
                    {
                        listed = count;
                    }
                    else if (string.Equals(state, "sold", StringComparison.Ordinal))
                    {
                        sold = count;
                    }
                }
            }

            Console.WriteLine("stone_listing=listed listed=" + listed + " sold=" + sold);
        }

        return null;
    }

}