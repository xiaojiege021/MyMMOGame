using System.Runtime.InteropServices;

// 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
var configPath = "config/battle.json";
var arenaOnly = false;
string? arenaPath = null;
var battlefieldOnly = false;
string? battlefieldPath = null;
var siegeOnly = false;
string? siegePath = null;
var sectTechOnly = false;
string? sectTechPath = null;
var siegeTerritoryOnly = false;
string? siegeTerritoryPath = null;
var siegeViewOnly = false;
var siegeViewArgs = new List<string>();
// S1：只读入口，打印技能表的门派 / 阶 / 分支归属就退出，不起服、不连库。
var skillListOnly = false;
string? skillTablePath = null;
// Q2：只读入口，打印效果表与四段伤害的纯计算，不起服、不连库。
var effectListOnly = false;
string? effectTablePath = null;
var damagePreviewOnly = false;
var damageArgs = new Dictionary<string, double>(StringComparer.Ordinal);
var worldBossOnly = false;
string? worldBossPath = null;
var worldBossAtMs = 0L;
// Q8：分配算法的离线自检。不连库、不起服、不读配置。
var lootStateOnly = false;
// Q9：重连表的只读入口（打印三个数字），以及「这个角色还要等多久」的只读查询。
// 两者都与真实启动共用同一份 BattleConfig.Load，不另起一套配置读法。
var reconnectStateOnly = false;
var deserterStateOf = 0UL;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        // 只读入口：打印竞技场表就退出，不起服务、不连库。与真实启动共用同一份加载。
        case "--arena-table":
            arenaOnly = true;
            break;
        case "--arena-table-path" when i + 1 < args.Length:
            arenaOnly = true;
            arenaPath = args[++i];
            break;
        // 只读入口：打印战场表就退出。与真实启动共用同一个 BattlefieldTable.Load。
        case "--battlefield-table":
            battlefieldOnly = true;
            break;
        case "--battlefield-table-path" when i + 1 < args.Length:
            battlefieldOnly = true;
            battlefieldPath = args[++i];
            break;
        // 只读入口：打印城战表就退出，不起服务、不连库。与真实启动共用同一份加载。
        case "--siege-table":
            siegeOnly = true;
            break;
        case "--siege-table-path" when i + 1 < args.Length:
            siegeOnly = true;
            siegePath = args[++i];
            break;
        // 只读入口：打印宗门科技折算表就退出，不起服务、不连库、不查社交服。
        case "--sect-tech-table":
            sectTechOnly = true;
            break;
        case "--sect-tech-table-path" when i + 1 < args.Length:
            sectTechOnly = true;
            sectTechPath = args[++i];
            break;
        // P3-3a4：打印领地表就退出，不连库不起服，与真实启动共用同一份加载。
        case "--siege-territory-table":
            siegeTerritoryOnly = true;
            break;
        case "--siege-territory-table-path" when i + 1 < args.Length:
            siegeTerritoryOnly = true;
            siegeTerritoryPath = args[++i];
            break;
        // P3-5a：打印世界 Boss 表就退出，不连库不起服，与真实启动共用同一份加载。
        case "--world-boss-table":
            worldBossOnly = true;
            break;
        case "--world-boss-table-path" when i + 1 < args.Length:
            worldBossOnly = true;
            worldBossPath = args[++i];
            break;
        // 把「这一刻」定成给定的毫秒时间戳，用来验未到点 / 到点；同一时刻只算出一场。
        case "--at" when i + 1 < args.Length:
            if (!long.TryParse(args[++i], out worldBossAtMs))
            {
                Console.WriteLine("bad_arg=--at");
                return 64;
            }
            break;
        // 只读入口：不连库不起服，直接核对城战同屏档位的纯计算。
        case "--siege-view":
            siegeViewOnly = true;
            while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                siegeViewArgs.Add(args[++i]);
            }
            break;
        // S1：打印技能表的门派 / 阶 / 分支归属。与真实启动共用同一个 SkillTable.Load。
        case "--skill-list":
            skillListOnly = true;
            break;
        case "--skill-table-path" when i + 1 < args.Length:
            skillListOnly = true;
            skillTablePath = args[++i];
            break;
        // Q2：效果清单。与真实启动共用同一个 EffectTable.Load。
        case "--effect-list":
            effectListOnly = true;
            break;
        case "--effect-table-path" when i + 1 < args.Length:
            effectListOnly = true;
            effectTablePath = args[++i];
            break;
        // Q2：四段伤害纯计算。数值全部来自命令参数，不连库、不起服。
        case "--damage-preview":
            damagePreviewOnly = true;
            break;
        case "--attack" or "--multiplier" or "--defense" or "--realm-bps" or "--attack-pct"
            or "--defense-pct" or "--penetration" or "--resistance" or "--random-pct"
            or "--enhance-coef" or "--crystal-flat" when i + 1 < args.Length:
            var key = args[i][2..];
            if (!double.TryParse(args[++i], out var value))
            {
                Console.WriteLine("bad_arg=" + key);
                return 64;
            }
            damageArgs[key] = value;
            break;
        // Q8：掉落分配的离线自检（纯函数，不连库、不起服、不读配置）。
        case "--loot-state":
            lootStateOnly = true;
            break;
        // Q9：重连表只读核对。与真实启动共用同一个 BattleConfig.Load；
        // 缺表时打 reconnect_state=missing，老卡临时配置目录就是这个状态。
        case "--reconnect-state":
            reconnectStateOnly = true;
            break;
        // Q9：问角色服「这个角色还要等多久」。只读，不写库、不改任何事实值。
        case "--deserter-state" when i + 1 < args.Length:
            if (!ulong.TryParse(args[++i], out deserterStateOf))
            {
                Console.WriteLine("bad_arg=--deserter-state");
                return 64;
            }
            break;
    }
}

if (reconnectStateOnly)
{
    try
    {
        var reconnect = Battle.BattleConfig.Load(configPath).Reconnect;
        if (reconnect.IsMissing)
        {
            Console.WriteLine("reconnect_state=missing");
            return 0;
        }

        Console.WriteLine($"reconnect_state=ok version={reconnect.Version}"
            + $" grace_ms={reconnect.GraceMs} window_ms={reconnect.WindowMs}"
            + $" penalty_ms={reconnect.PenaltyMs}");
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

if (deserterStateOf != 0)
{
    try
    {
        var config = Battle.BattleConfig.Load(configPath);
        if (string.IsNullOrEmpty(config.CharacterUpstream))
        {
            // 本机口径：没有角色库可问，惩罚只活在战斗服内存里。
            Console.WriteLine($"deserter=character={deserterStateOf} found=false remaining_ms=0"
                + " reason=upstream_off");
            return 0;
        }

        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(config.CharacterUpstream);
        var client = new Mmo.Proto.Character.CharacterService.CharacterServiceClient(channel);
        var reply = client.DescribeDeserter(
            new Mmo.Proto.Character.DescribeDeserterRequest { CharacterId = deserterStateOf },
            deadline: DateTime.UtcNow.AddSeconds(3));
        Console.WriteLine($"deserter=character={deserterStateOf}"
            + $" found={(reply.Ok && reply.RemainingMs > 0 ? "true" : "false")}"
            + $" remaining_ms={reply.RemainingMs} reason={reply.Reason}"
            + $" session={reply.SessionId}");
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

if (lootStateOnly)
{
    return Battle.LootSelfCheck.Run();
}

if (sectTechOnly)
{
    try
    {
        Battle.SectTechTable table = sectTechPath is null
            ? Battle.BattleConfig.Load(configPath).SectTech
            : Battle.SectTechTable.Load(sectTechPath);

        if (table.IsMissing)
        {
            Console.WriteLine("sect_tech=missing");
            return 0;
        }

        Console.WriteLine($"sect_tech_table_version={table.Version}"
            + $" power_pct_per_level={table.PowerPctPerLevel}"
            + $" max_level={table.MaxLevel}"
            + $" pending={(table.Pending ? "true" : "false")}");
        // 纯计算：等级 0 恒 1，等级越高系数越大；数字全部从配置现取，不写死。
        Console.WriteLine($"sect_tech_factor=level=0 {table.Factor(0):0.###}"
            + $" level=1 {table.Factor(1):0.###}"
            + $" level=max {table.Factor(table.MaxLevel):0.###}");
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

if (siegeTerritoryOnly)
{
    try
    {
        Battle.SiegeTerritoryTable table = siegeTerritoryPath is null
            ? Battle.BattleConfig.Load(configPath).SiegeTerritory
            : Battle.SiegeTerritoryTable.Load(siegeTerritoryPath);

        if (table.IsMissing)
        {
            Console.WriteLine("siege_territory=missing");
            return 0;
        }

        Console.WriteLine($"siege_territory_table_version={table.Version}"
            + $" pending={(table.Pending ? "true" : "false")}"
            + $" cities={table.Cities.Count} city_table={table.CityTable}");
        foreach (var city in table.Cities)
        {
            Console.WriteLine($"siege_territory_city=id={city.CityId}"
                + $" prefix={city.MatchPrefix} timeout_ms={city.BattleTimeoutMs}"
                + $" tax_pct={city.TaxPctOnCapture}");
        }

        // 纯计算：哪一场打哪座城只由服务端的前缀决定，匹配不上就不写回。
        var first = table.Cities[0];
        Console.WriteLine($"siege_territory_match=prefix={first.MatchPrefix}"
            + $" city={table.CityOf(first.MatchPrefix + "-probe")?.CityId ?? "-"}"
            + $" unknown={table.CityOf("not-a-city")?.CityId ?? "-"}");
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

if (worldBossOnly)
{
    try
    {
        var battleConfig = Battle.BattleConfig.Load(configPath);
        WorldBossTable table = worldBossPath is null
            ? battleConfig.WorldBoss
            : WorldBossTable.Load(worldBossPath);

        if (table.IsMissing)
        {
            Console.WriteLine("world_boss=missing");
            return 0;
        }

        Console.WriteLine($"world_boss_table_version={table.Version}"
            + $" pending={(table.Pending ? "true" : "false")}"
            + $" entrance={table.EntranceId}"
            + $" region={table.RegionId}"
            + $" cap={table.Cap}"
            + $" windows={table.Windows.Count}"
            + $" boss={table.Boss.Id}"
            + $" boss_hp_max={table.Boss.HpMax}"
            + $" boss_exp={table.Boss.ExpOnClear}");
        foreach (var window in table.Windows)
        {
            Console.WriteLine($"world_boss_window_rule=id={window.Id}"
                + $" start_minutes={window.StartMinutes} duration_s={window.DurationSeconds}");
        }

        // 纯计算：这一刻是哪一场只由服务端时钟与这张表决定；与世界服那条入口
        // 按同一个时间戳算出来的钥匙必须一样，不一样就是两份实现跑偏了。
        var now = worldBossAtMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(worldBossAtMs)
            : DateTimeOffset.UtcNow;
        var slot = table.WindowOf(now, battleConfig.Calendar);
        Console.WriteLine($"world_boss_now=at={now.ToUnixTimeMilliseconds()}"
            + $" open={(slot.Open ? "true" : "false")}"
            + $" key={(slot.Key.Length == 0 ? "none" : slot.Key)}"
            + $" starts_at_ms={slot.StartsAtMs}"
            + $" ends_at_ms={slot.EndsAtMs}"
            + $" next_starts_at_ms={slot.NextStartsAtMs}");
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

if (siegeViewOnly)
{
    return Battle.SiegeViewCli.Run(siegeViewArgs);
}

if (skillListOnly)
{
    try
    {
        Battle.SkillTable table = skillTablePath is null
            ? Battle.BattleConfig.Load(configPath).Skills
            : Battle.SkillTable.Load(skillTablePath);

        Console.WriteLine($"skill_table_version={table.Version}"
            + $" skills={table.All.Count}"
            + $" schools={table.Schools.Count}");
        foreach (var school in table.Schools)
        {
            var inSchool = table.ForSchool(school.Id);
            var branches = inSchool.Where(s => s.Branch.Length > 0)
                .Select(s => s.Branch).Distinct().OrderBy(b => b, StringComparer.Ordinal).ToList();
            Console.WriteLine($"skill_school=id={school.Id} name={school.Name} giver={school.Giver}"
                + $" tiers={Join(school.Tiers.Select(t => t.Id))}"
                + $" skills={inSchool.Count}"
                + $" branches={(branches.Count == 0 ? "-" : Join(branches))}");
        }

        foreach (var skill in table.All.OrderBy(s => s.Id))
        {
            Console.WriteLine($"skill_row=id={skill.Id} name={skill.Name} class={skill.ClassId}"
                + $" school={Dash(skill.School)} tier={Dash(skill.Tier)} branch={Dash(skill.Branch)}");
        }

        // 门派通用（不挂分支）与挂到分支的各几条：数字随配置走，这里只汇总。
        var common = table.All.Count(s => s.Branch.Length == 0);
        var branched = table.All.Where(s => s.Branch.Length > 0).Select(s => s.Branch).Distinct().Count();
        Console.WriteLine($"skill_branch_total={branched} common={common}");
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

if (effectListOnly)
{
    try
    {
        Battle.EffectTable table = effectTablePath is null
            ? Battle.BattleConfig.Load(configPath).Effects
            : Battle.EffectTable.Load(effectTablePath);

        if (table.IsMissing)
        {
            Console.WriteLine("effects=missing");
            return 0;
        }

        Console.WriteLine($"effect_table=schema=effect-table/2 version={table.Version}"
            + $" pending={(table.Pending ? "true" : "false")} effects={table.All.Count}");
        foreach (var effect in table.All.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            Console.WriteLine($"effect=id={effect.Id} name={effect.Name}"
                + $" kind={Battle.StatusSet.KindName(effect.Kind)}"
                + $" duration_ms={effect.DurationMs} tick_ms={effect.TickMs}"
                + $" max_stacks={effect.MaxStacks} stack_rule={effect.StackRule}"
                + $" control={Dash(Battle.EffectTable.ControlName(effect.Control))}"
                + $" dispel={Join(effect.DispelTags)} immunity={Join(effect.ImmunityTags)}");
        }

        var interaction = table.Interaction;
        Console.WriteLine($"effect_interaction=cc_window_ms={interaction.CcWindowMs}"
            + $" steps={Join(interaction.CcStepsPct.Select(p => p.ToString()))}"
            + $" interrupt_ms={interaction.InterruptCooldownMs}"
            + $" mana_refund_pct={interaction.InterruptManaRefundPct}"
            + $" hitstun_ms={interaction.HitstunDefaultMs}"
            + $" refresh_on_stack={(interaction.RefreshOnStack ? "true" : "false")}");

        var damage = table.Damage;
        Console.WriteLine("effect_damage=stages=base,enhance,skill,final"
            + $" defense_k={damage.DefenseK} min_pct={damage.DefenseMinPct}"
            + $" crit_rate_pct={damage.CritRatePct} crit_damage_pct={damage.CritDamagePct}"
            + $" random={damage.RandomMinPct}..{damage.RandomMaxPct}"
            + $" penetration={(damage.PenetrationEnabled ? "on" : "off")}/{damage.PenetrationDefaultPct}"
            + $" resistance={(damage.ResistanceEnabled ? "on" : "off")}/{damage.ResistanceDefaultPct}"
            + " rounding=final_round_half_up");
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

if (damagePreviewOnly)
{
    try
    {
        Battle.EffectTable table = Battle.BattleConfig.Load(configPath).Effects;
        if (table.IsMissing)
        {
            Console.WriteLine("effects=missing");
            return 0;
        }

        double Value(string key, double fallback) => damageArgs.GetValueOrDefault(key, fallback);
        var input = new Battle.DamageInput(
            Attack: Value("attack", 200),
            SkillMultiplier: Value("multiplier", 1),
            TargetDefense: Value("defense", 0),
            RealmBonusPct: Value("realm-bps", 0),
            AttackPct: Value("attack-pct", 0),
            DefensePct: Value("defense-pct", 0),
            PenetrationPct: Value("penetration", 0),
            ResistancePct: Value("resistance", 0),
            EnhanceCoef: Value("enhance-coef", 0),
            CrystalFlatAttack: Value("crystal-flat", 0),
            ForcedRandomPct: (int)Value("random-pct", 100));
        var stages = Battle.DamagePipeline.Compute(table, input, new Random(0));
        Console.WriteLine($"damage_stage=base value={stages.Base:0.####}");
        Console.WriteLine($"damage_stage=enhance value={stages.Enhanced:0.####}");
        Console.WriteLine($"damage_stage=skill value={stages.Skill:0.####}");
        Console.WriteLine($"damage_stage=final value={stages.Final:0.####}"
            + $" mitigation={stages.DefenseMitigation:0.####} crit={(stages.Crit ? "true" : "false")}");
        Console.WriteLine($"damage_result=rounded={stages.Rounded}");
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

static string Join(IEnumerable<string> parts)
{
    var text = string.Join(",", parts);
    return text.Length == 0 ? "-" : text;
}

static string Dash(string text) => text.Length == 0 ? "-" : text;

if (battlefieldOnly)
{
    try
    {
        Battle.BattlefieldTable table = battlefieldPath is null
            ? Battle.BattleConfig.Load(configPath).Battlefield
            : Battle.BattlefieldTable.Load(battlefieldPath);

        if (table.IsMissing)
        {
            Console.WriteLine("battlefield=missing");
            return 0;
        }

        Console.WriteLine($"battlefield_table_version={table.Version} mode={table.ModeId}"
            + $" team_size={table.TeamSize} teams={table.Teams} players={table.Players}"
            + $" ratio={table.Ratio} fill_timeout_ms={table.FillTimeoutMs}");
        Console.WriteLine($"battlefield_equipment=attack_per_level={table.AttackPerLevel}"
            + $" hp_per_level={table.HpPerLevel} mana_per_level={table.ManaPerLevel}"
            + $" heal_per_level={table.HealPerLevel}");
        Console.WriteLine($"battlefield_power=attack_weight={table.AttackWeight}"
            + $" hp_weight={table.HpWeight} mana_weight={table.ManaWeight}"
            + $" heal_weight={table.HealWeight}");
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

if (siegeOnly)
{
    try
    {
        Battle.SiegeTable table = siegePath is null
            ? Battle.BattleConfig.Load(configPath).Siege
            : Battle.SiegeTable.Load(siegePath);

        if (table.IsMissing)
        {
            Console.WriteLine("siege=missing");
            return 0;
        }

        Console.WriteLine($"siege_table_version={table.Version} mode={table.ModeId}"
            + $" team_size={table.TeamSize} teams={table.Teams}"
            + $" players={table.Players} balance=none fill_timeout_ms={table.FillTimeoutMs}"
            + $" view_full_model_limit={table.ViewFullModelLimit}");
        Console.WriteLine($"siege_equipment=attack_per_level={table.AttackPerLevel}"
            + $" hp_per_level={table.HpPerLevel} mana_per_level={table.ManaPerLevel}"
            + $" heal_per_level={table.HealPerLevel}");
        Console.WriteLine($"siege_power=attack_weight={table.AttackWeight}"
            + $" hp_weight={table.HpWeight} mana_weight={table.ManaWeight}"
            + $" heal_weight={table.HealWeight}");
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

if (arenaOnly)
{
    try
    {
        Battle.ArenaMatchTable table = arenaPath is null
            ? Battle.BattleConfig.Load(configPath).ArenaMatch
            : Battle.ArenaMatchTable.Load(arenaPath);

        Console.WriteLine($"arena_table_version={table.Version}"
            + $" modes={table.Modes.Count} templates={table.Templates.Count}");
        foreach (var mode in table.Modes.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            Console.WriteLine($"arena_mode={mode.Id} name={mode.Name}"
                + $" team_size={mode.TeamSize} teams={mode.Teams} players={mode.Players}");
        }

        foreach (var template in table.Templates
            .OrderBy(t => t.ClassId, StringComparer.Ordinal)
            .ThenBy(t => t.BranchId, StringComparer.Ordinal))
        {
            Console.WriteLine($"arena_template=class={template.ClassId} branch={template.BranchId}"
                + $" attack={template.Attack} hp_max={template.HpMax} mana_max={template.ManaMax}"
                + $" heal_power={template.HealPower} threat={template.ThreatStability}");
        }

        return 0;
    }
    catch (ConfigException ex)
    {
        Console.WriteLine("config_error=" + ex.Message);
        return 2;
    }
}

using var stop = new CancellationTokenSource();
PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.Cancel();
});
PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    stop.Cancel();
});

return await new Battle.BattleProcess(configPath).RunAsync(stop.Token);
