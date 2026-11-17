using Mmo.Shared.Domain;
using Npgsql;

using static Character.Cli.CliFlags;
using static Character.Cli.CliLoaders;

namespace Character.Cli;

/// <summary>配置表只读核对：副本 / 掉落 / 境界 / 经验 / 兵器 / 灵石兑换 / 城池 / 地表。</summary>
internal static class ConfigTableCli
{
    public static int? TryReadOnly()
    {
        // P2-2a 副本表只读核对：与真实启动共用同一 DungeonTable.Load，不连库也不启动进程，
        // 坏配置（高难缺字段、下限超过上限、普通本配了高难三项）能在这一步直接看出来。
        if (dungeonTableState)
        {
            try
            {
                var table = DungeonTable.Load(
                    dungeonTablePath ?? CharacterConfig.Load(configPath).DungeonTable);
                Console.WriteLine("dungeon_table_state=ok version=" + table.Version);
                foreach (var dungeon in table.All)
                {
                    Console.WriteLine("dungeon=" + dungeon.Id
                        + " tier=" + dungeon.Tier
                        + " min=" + dungeon.MinPlayers
                        + " max=" + dungeon.MaxPlayers
                        + " entries_per_week=" + dungeon.EntriesPerWeek
                        + " reward_per_player=" + dungeon.RewardPerPlayer
                        + " fatigue=" + (dungeon.ChargesFatigue ? "true" : "false")
                        + " share=" + (dungeon.TakesWeeklyShare ? "true" : "false")
                        + " realm=" + (dungeon.Realm ?? "none")
                        + " drill=" + dungeon.Drill);
                }
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

        // N7：主线交任务发奖表只读核对。
        if (mainlineDropTableState)
        {
            try
            {
                var items = ItemTable.Load("config/items.json");
                var currencies = CurrencyTable.Load("config/currency.json");
                var path = mainlineDropTablePath
                    ?? Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(Path.GetFullPath(configPath))!, "mainline_drops.json"));
                var table = MainlineDropTable.Load(path, items, currencies);
                Console.WriteLine("mainline_drop_table_state=ok version=" + table.Version
                    + " stages=" + table.StageCount
                    + " pending=" + (table.Pending ? "true" : "false"));
            }
            catch (ConfigException ex)
            {
                Console.WriteLine("config_error=" + ex.Message);
                return 2;
            }

            return 0;
        }

        // N6：掉落表只读核对，坏配置（灵石币种、表外物品）在这里直接被挡住。
        if (dungeonDropTableState)
        {
            try
            {
                var items = ItemTable.Load("config/items.json");
                var currencies = CurrencyTable.Load("config/currency.json");
                var path = dungeonDropTablePath
                    ?? Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(Path.GetFullPath(configPath))!, "dungeon_drops.json"));
                var table = DungeonDropTable.Load(
                    path,
                    items.All.Select(item => item.Id),
                    currencies.All.Select(currency => currency.Id));
                Console.WriteLine("dungeon_drop_table_state=ok version=" + table.Version
                    + " dungeons=" + table.DungeonCount);
                foreach (var id in new[] { "abyss", "lair", "world_boss" })
                {
                    var lines = table.LinesFor(id);
                    if (lines.Count == 0)
                    {
                        continue;
                    }

                    Console.WriteLine("dungeon_drop=" + id + " lines=" + lines.Count);
                }
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

        // P3-1a1 境界表只读核对：与启动共用同一 RealmTable.Load，不连库也不启动进程。
        // 坏配置（缺开关、首发段配了开关、别名指错、旧副本写了 realm）能在这一步直接看出来。
        if (realmTableState)
        {
            try
            {
                var characterConfig = CharacterConfig.Load(configPath);
                var dungeons = DungeonTable.Load(
                    realmDungeonPath ?? characterConfig.DungeonTable);
                var currencies = CurrencyTable.Load("config/currency.json");
                var table = RealmTable.Load(realmTablePath ?? characterConfig.RealmTable, dungeons, currencies);
                // R1：段内阶按境界分——先看炼体 9 阶，再看 * 默认 4 阶，其余显式配置的排后面。
                var groups = new List<string>();
                if (table.SubPhasesByRealm.ContainsKey("炼体"))
                {
                    groups.Add("炼体");
                }

                if (!groups.Contains("*"))
                {
                    groups.Add("*");
                }

                foreach (var key in table.SubPhasesByRealm.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!groups.Contains(key))
                    {
                        groups.Add(key);
                    }
                }

                foreach (var key in groups)
                {
                    if (!table.SubPhasesByRealm.TryGetValue(key, out var group))
                    {
                        continue;
                    }

                    Console.WriteLine("sub_phase_group=" + key + " count=" + group.Count
                        + " breakthrough_min_index=" + group.BreakthroughMinIndex);
                    for (var pi = 0; pi < group.Count; pi++)
                    {
                        Console.WriteLine("sub_phase=" + key + ":" + pi + " name=" + group.NameAt(pi)
                            + " passive_pct_bps=" + group.PassiveBpsAt(pi)
                            + " promote_min_level=" + group.PromoteLevelAt(pi));
                    }
                }

                Console.WriteLine("realm_table_state=ok version=" + table.Version
                    + " realms=" + table.Realms.Count
                    + " launch_until=" + table.LaunchUntil
                    + " open=" + string.Join(",", table.Realms.Where(table.IsOpen))
                    + " closed=" + string.Join(",", table.Realms.Where(r => !table.IsOpen(r))));
                for (var r = 0; r < table.Realms.Count; r++)
                {
                    Console.WriteLine("realm=" + (r + 1) + ":" + table.Realms[r]
                        + ":" + (table.IsOpen(table.Realms[r]) ? "open" : "closed"));
                }
                foreach (var pair in table.Aliases)
                {
                    Console.WriteLine("alias=" + pair.Key + "=" + pair.Value);
                    // C2：第十段按立仙 / 立魔显示；别名本身也要归一后再按分路显示。
                    Console.WriteLine("realm_display=" + pair.Value
                        + " immortal=" + table.DisplayName(pair.Value, "立仙")
                        + " demon=" + table.DisplayName(pair.Value, "立魔")
                        + " none=" + table.DisplayName(pair.Value, ""));
                    Console.WriteLine("realm_display_alias=" + pair.Key
                        + " immortal=" + table.DisplayName(pair.Key, "立仙")
                        + " demon=" + table.DisplayName(pair.Key, "立魔"));
                }
                // 非第十段不因立魔改显示名；选一段首发境界作对照。
                Console.WriteLine("realm_display_other=" + table.LaunchUntil
                    + " immortal=" + table.DisplayName(table.LaunchUntil, "立仙")
                    + " demon=" + table.DisplayName(table.LaunchUntil, "立魔"));
                // P3-1a2：每段的突破要求，数值全部来自配置。
                foreach (var requirement in table.Requirements)
                {
                    Console.WriteLine("advance_req=" + requirement.From + "->" + requirement.Next
                        + " drill=" + requirement.Drill
                        + " stone=" + requirement.Stone
                        + " stone_cost=" + requirement.StoneCost
                        + " success_pct=" + requirement.SuccessPct
                        + " main_stage=" + requirement.MainStage
                        + " side_quests=" + string.Join("+", requirement.SideQuests));
                }
                foreach (var realm in table.Realms)
                {
                    var advance = table.CheckAdvance(realm);
                    Console.WriteLine("advance=" + realm + "->"
                        + (advance.Next.Length == 0 ? "none" : advance.Next)
                        + ":" + (advance.Ok ? "ok" : advance.Reason));
                }
                foreach (var dungeon in dungeons.All)
                {
                    // 用首发那一段去试：关着时应该回 realm_locked，开着时也只是 realm_not_enough；
                    // 旧副本没写 realm，这里应当直接 ok。
                    var gate = table.CheckDungeon(dungeon.Realm, table.LaunchUntil);
                    Console.WriteLine("dungeon_gate=" + dungeon.Id
                        + " realm=" + (dungeon.Realm ?? "none")
                        + " open=" + (dungeon.Realm is null || table.IsOpen(dungeon.Realm) ? "true" : "false")
                        + " from_launch=" + (gate.Ok ? "ok" : gate.Reason));
                }
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

        // 经验表只读核对（P2-1c）：与真实启动共用同一 ExpTable.Load，不连库也不启动进程，
        // 坏配置能在这一步直接看出来。gain 三个数证明「组队加成 / 同宗门加成 / 单人」三档口径。
        if (expConfigState)
        {
            try
            {
                var characterConfig = CharacterConfig.Load(configPath);
                var table = ExpTable.Load(expConfigPath ?? characterConfig.ExpTable);
                Console.WriteLine("exp_config_state=ok version=" + table.Version
                    + " level_cap=" + table.LevelCap
                    + " base_exp=" + table.BaseExp
                    + " party_bonus_pct=" + table.PartyBonusPct
                    + " party_min_size=" + table.PartyMinSize
                    + " party_max_size=" + table.PartyMaxSize
                    + " same_sect_bonus_pct=" + table.SameSectBonusPct
                    + " exp_per_level=" + table.ExpPerLevel
                    + " gain1=" + table.Gain(1, false)
                    + " gain2=" + table.Gain(2, false)
                    + " gain2_same=" + table.Gain(2, true));
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

        // K6：NPC / 怪物两张内容表只读核对。与真实启动共用同一份 NpcTable / MonsterTable.Load：
        // 地表、势力、对话、商店、技能、掉落、刷怪点任何一处对不上都报 config_error。
        if (contentList)
        {
            try
            {
                var items = ItemTable.Load("config/items.json");
                var world = ConfigJson.Read<ContentWorldWire>("config/world.json");
                var skills = ConfigJson.Read<ContentSkillWire>("config/skills.json");
                var shops = ConfigJson.Read<ContentShopWire>("config/npc_shop.json");
                var regions = world.Regions ?? [];
                var npcs = NpcTable.Load(
                    "config/npc.json", regions, world.Factions ?? [], world.Speakers ?? [],
                    (shops.Shops ?? []).Select(s => (s.Id ?? "").Trim())
                        .Where(s => s.Length > 0).ToList());
                var monsters = MonsterTable.Load(
                    "config/monsters.json",
                    (skills.Skills ?? []).Select(s => s.Id.ToString()).ToList(),
                    items.All.Select(i => i.Id).ToList(), regions);

                Console.WriteLine("content_table=ok npcs=" + npcs.All.Count
                    + " monsters=" + monsters.All.Count
                    + " npc_version=" + npcs.Version
                    + " monster_version=" + monsters.Version);
                foreach (var npc in npcs.All.OrderBy(n => n.Id, StringComparer.Ordinal))
                {
                    Console.WriteLine("npc=" + npc.Id
                        + " name=" + npc.Name
                        + " region=" + npc.Region
                        + " role=" + npc.Role
                        + " faction=" + (npc.Faction.Length == 0 ? "-" : npc.Faction)
                        + " dialogue=" + (npc.DialogueId.Length == 0 ? "-" : npc.DialogueId)
                        + " shop=" + (npc.ShopId.Length == 0 ? "-" : npc.ShopId)
                        + " pos=" + npc.X + "," + npc.Y + "," + npc.Z
                        + " facing=" + npc.Facing);
                }
                foreach (var monster in monsters.All.OrderBy(m => m.Id, StringComparer.Ordinal))
                {
                    Console.WriteLine("monster=" + monster.Id
                        + " name=" + monster.Name
                        + " tier=" + monster.Tier
                        + " level=" + monster.Level
                        + " hp=" + monster.Hp
                        + " attack=" + monster.Attack
                        + " defense=" + monster.Defense
                        + " skills=" + (monster.Skills.Count == 0 ? "-" : string.Join(",", monster.Skills))
                        + " drops=" + (monster.Drops.Count == 0 ? "-" : string.Join(",", monster.Drops
                            .Select(d => d.ItemId + ":" + d.Count + ":" + d.ChancePct)))
                        + " spawn=" + (monster.Spawn is null
                            ? "-"
                            : monster.Spawn.Region + "@" + monster.Spawn.X + "," + monster.Spawn.Y
                              + "," + monster.Spawn.Z + "+" + monster.Spawn.RespawnS + "s"));
                }
                return 0;
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
        }

        if (itemList)
        {
            try
            {
                var table = ItemTable.Load("config/items.json");
                Console.WriteLine("item_table_state=ok version=" + table.Version
                    + " items=" + table.All.Count);
                foreach (var item in table.All.OrderBy(i => i.Id, StringComparer.Ordinal))
                {
                    Console.WriteLine("item=" + item.Id
                        + " kind=" + item.Kind
                        + " grade=" + (item.HasGrade ? item.Grade : "-")
                        + " set=" + (item.SetId.Length == 0 ? "-" : item.SetId)
                        + " name=" + item.Name
                        + " desc=" + item.Desc);
                }
                return 0;
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
        }

        if (weaponRules)
        {
            try
            {
                var itemTable = ItemTable.Load(weaponRulesItemPath ?? "config/items.json");
                var branchTable = BranchTable.Load(weaponRulesBranchPath ?? "config/branches.json");
                Console.WriteLine("weapon_lock_level=" + branchTable.WeaponLockLevel);
                foreach (var item in itemTable.All.Where(i => i.IsWeapon).OrderBy(i => i.Id, StringComparer.Ordinal))
                {
                    Console.WriteLine("weapon=" + item.Id + " kind=" + item.WeaponKind
                        + " require_level=" + item.RequireLevel);
                }
                foreach (var cls in branchTable.All.Select(b => b.ClassId)
                    .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal))
                {
                    Console.WriteLine("class=" + cls + " kinds=" + string.Join(",", branchTable.WeaponKindsOf(cls)));
                }
                return 0;
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
        }

        // P3-2a 灵石兑换表只读核对：与启动共用同一 StoneExchangeTable.Load，不连库、
        // 不启动进程。坏配置（缺字段、档位不在币种表、最高一档中文名与币种表不一致、
        // 比例产出越界、最高一档与充值货币混档）在这一步就能看出来。
        if (stoneExchangeTableState)
        {
            try
            {
                var stoneConfig = CharacterConfig.Load(configPath);
                var stoneCurrencies = CurrencyTable.Load(
                    stoneExchangeCurrencyPath ?? "config/currency.json");
                var stoneTable = StoneExchangeTable.Load(
                    stoneExchangeTablePath ?? stoneConfig.StoneExchangeTable, stoneCurrencies);
                Console.WriteLine("stone_exchange_state=ok version=" + stoneTable.Version
                    + " tiers=" + stoneTable.Tiers.Count
                    + " ratio=" + stoneTable.Ratio
                    + " gain=" + stoneTable.Gain
                    + " highest=" + stoneTable.HighestKind + "/" + stoneTable.HighestName
                    + " ingot=" + stoneTable.IngotKind
                    + " tiers_list=" + string.Join(",", stoneTable.Tiers));
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

        // P3-2a 城池表只读核对：与启动共用同一 CityTable.Load，不连库、不启动进程。
        // 坏配置（vein_id 对不上 gather.json 的 spirit_stone 节点、gather 里没有矿脉节点、
        // city 里夹带个人采集点或商品字段）在这一步就能看出来。
        if (cityTableState)
        {
            try
            {
                var cityConfig = CharacterConfig.Load(configPath);
                var cityTable = CityTable.Load(cityTablePath ?? cityConfig.CityTable);
                Console.WriteLine("city_table_state=ok version=" + cityTable.Version
                    + " cities=" + cityTable.Cities.Count
                    + " gather_table=" + cityTable.GatherTable
                    + " task_grant_city=" + cityTable.TaskGrantCityId);
                foreach (var city in cityTable.Cities)
                {
                    // K7：归属与税率在库，配置只回静态三列。
                    Console.WriteLine("city=" + city.CityId
                        + " name=" + city.Name
                        + " vein=" + city.VeinId
                        + " owner_sect=db"
                        + " tax_pct=db");
                }
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

        // P3-3a4 领地表只读核对：不连库、不启动进程，与启动共用 SiegeTerritoryPolicy.Load。
        // 坏配置（税率越界、城池编号对不上 city.json、配置里预写归属）在这一步就能看出来。
        if (siegeTerritoryTableState)
        {
            try
            {
                var cfg = CharacterConfig.Load(configPath);
                var cityTable = CityTable.Load(cfg.CityTable);
                var policy = SiegeTerritoryPolicy.Load(
                    siegeTerritoryTablePath ?? cfg.SiegeTerritoryTable, cityTable);
                Console.WriteLine("siege_territory_table_state=ok version=" + policy.Version
                    + " pending=" + (policy.Pending ? "true" : "false"));
                foreach (var city in cityTable.Cities)
                {
                    Console.WriteLine("siege_territory_city=city=" + city.CityId
                        + " tax_pct=" + policy.TaxPctOf(city.CityId));
                }
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


    sealed record ContentWorldWire
    {
        public List<string>? Regions { get; init; }
        public List<string>? Speakers { get; init; }
        public List<string>? Factions { get; init; }
    }

    sealed record ContentSkillWire
    {
        public List<ContentSkillItem>? Skills { get; init; }
    }

    sealed record ContentSkillItem
    {
        public uint Id { get; init; }
    }

    sealed record ContentShopWire
    {
        public List<ContentShopItem>? Shops { get; init; }
    }

    sealed record ContentShopItem
    {
        public string? Id { get; init; }
    }
}
