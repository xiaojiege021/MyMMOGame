using System.Text.Json;
using Mmo.Proto.Battle;

namespace Battle;

public enum EffectKind
{
    Buff,
    Debuff,
    Control,
    Hot,
    Dot,
    Shield,
    Cleanse,
}

public enum ControlKind
{
    None,
    Root,
    Slow,
    Stun,
    Silence,
}

public sealed class EffectModifier
{
    public string Stat { get; init; } = "";
    public int Value { get; init; }
}

public sealed class EffectDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public EffectKind Kind { get; init; }
    public int DurationMs { get; init; }
    public int TickMs { get; init; }
    public int MaxStacks { get; init; } = 1;
    public string StackRule { get; init; } = "refresh";
    public List<EffectModifier> Modifiers { get; init; } = [];
    public int AmountPctMaxHp { get; init; }
    public ControlKind Control { get; init; }
    public int ValuePct { get; init; }
    public bool Diminishing { get; init; }
    public List<string> DispelTags { get; init; } = [];
    public List<string> ImmunityTags { get; init; } = [];
    public bool Pending { get; init; }
}

/// <summary>
/// 技能 / 丹药 / 道具共用的施加说明：引用效果编号，不抄数值。
/// target 只认 self / target，前者是施法者自己，后者是这一手本来选中的单位。
/// B1 / B15 填内容时只改配置，不改运行时。
/// </summary>
public sealed class EffectSpec
{
    public string EffectId { get; init; } = "";
    public string Target { get; init; } = "target";
    public int Stacks { get; init; } = 1;
}

/// <summary>一个效果实例。remaining 只由服务端时钟算，客户端拿不到这里的内部时刻。</summary>
public sealed class EffectInstance
{
    public required EffectDef Def { get; init; }
    public ulong SourceId { get; init; }
    public int Stacks { get; set; } = 1;
    public long ExpiresAtMs { get; set; }
    public long NextTickAtMs { get; set; }
    public long ShieldLeft { get; set; }
}

public enum EffectApply
{
    Applied,
    Refreshed,
    Stacked,
    Immune,
    Unknown,
}

/// <summary>
/// 一次 Tick 结果：血量变没变、护盾变没变、最后一个伤害来源是谁。
/// 血量变化由调用方决定发 MemberUpdated 还是 BossState；效果集合变化统一重发整份快照。
/// </summary>
public sealed record StatusTick(long HpDelta, bool EffectsChanged, ulong LastSourceId);

/// <summary>效果层配置。只读一次，运行期不改；改表重启战斗服。</summary>
public sealed class EffectTable
{
    public static readonly EffectTable Missing = new();

    readonly Dictionary<string, EffectDef> _byId;

    EffectTable()
    {
        _byId = [];
        IsMissing = true;
    }

    EffectTable(
        int version, bool pending, Dictionary<string, EffectDef> byId,
        InteractionConfig interaction, DamageConfig damage)
    {
        Version = version;
        Pending = pending;
        _byId = byId;
        Interaction = interaction;
        Damage = damage;
    }

    /// <summary>老卡临时配置目录里没有 effects.json 时为真：伤害走恒等，不施加任何效果。</summary>
    public bool IsMissing { get; }

    public int Version { get; }
    public bool Pending { get; }
    public InteractionConfig Interaction { get; } = new();
    public DamageConfig Damage { get; } = new();

    public IReadOnlyCollection<EffectDef> All => _byId.Values;

    public bool TryGet(string id, out EffectDef def) => _byId.TryGetValue(id, out def!);

    /// <summary>
    /// 校验与加载。规则写在 docs/配置设计.md 13.5：schema / 七类 / 控件 / 叠层 /
    /// 每跳 / 四段顺序 / 穿透抗性上限 / 边界里不许出现第二份 1.3，全部起不来就报 config_error。
    /// </summary>
    public static EffectTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"效果表不存在：{path}");
        }

        var file = ConfigJson.Read<EffectFile>(path);
        if (file.Schema != "effect-table/2")
        {
            throw new ConfigException($"{path}：schema 必须是 effect-table/2，现在是「{file.Schema}」");
        }
        if (file.Version < 3)
        {
            throw new ConfigException($"{path}：version 必须 >= 3，现在是 {file.Version}");
        }
        var effectDefs = file.Effects ?? [];
        if (effectDefs.Count == 0)
        {
            throw new ConfigException($"{path}：effects 不能是空表");
        }

        var controlIds = (file.ControlTypes ?? []).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var byId = new Dictionary<string, EffectDef>(StringComparer.Ordinal);
        foreach (var effect in effectDefs)
        {
            if (string.IsNullOrWhiteSpace(effect.Id))
            {
                throw new ConfigException($"{path}：有效果缺 id");
            }
            if (string.IsNullOrWhiteSpace(effect.Name))
            {
                throw new ConfigException($"{path}：效果 {effect.Id} 没有名字");
            }
            if (!byId.TryAdd(effect.Id, effect))
            {
                throw new ConfigException($"{path}：效果编号 {effect.Id} 重复");
            }
            if (effect.StackRule is not ("refresh" or "stack" or "independent"))
            {
                throw new ConfigException(
                    $"{path}：效果 {effect.Id} 的 stack_rule「{effect.StackRule}」不在 refresh / stack / independent 里");
            }
            if (effect.MaxStacks < 1)
            {
                throw new ConfigException($"{path}：效果 {effect.Id} 的 max_stacks 必须 >= 1");
            }
            if (effect.StackRule == "stack" && effect.MaxStacks < 2)
            {
                throw new ConfigException($"{path}：效果 {effect.Id} 用 stack 时 max_stacks 必须 > 1");
            }
            if (effect.Kind == EffectKind.Cleanse)
            {
                if (effect.DispelTags.Count == 0)
                {
                    throw new ConfigException($"{path}：净化 {effect.Id} 没有 dispel_tags");
                }
                continue;
            }
            if (effect.DurationMs <= 0)
            {
                throw new ConfigException($"{path}：效果 {effect.Id} 的 duration_ms 必须 > 0");
            }
            if (effect.Kind is EffectKind.Hot or EffectKind.Dot)
            {
                if (effect.TickMs <= 0)
                {
                    throw new ConfigException($"{path}：{effect.Id} 的 tick_ms 必须 > 0");
                }
                if (effect.DurationMs % effect.TickMs != 0)
                {
                    throw new ConfigException(
                        $"{path}：{effect.Id} 的 duration_ms 必须能被 tick_ms 整除（最后一跳不截断）");
                }
            }
            if (effect.Kind is EffectKind.Hot or EffectKind.Shield
                && effect.AmountPctMaxHp is <= 0 or > 100)
            {
                throw new ConfigException($"{path}：{effect.Id} 的 amount_pct_max_hp 必须在 1–100");
            }
            if (effect.Kind is EffectKind.Buff or EffectKind.Debuff or EffectKind.Dot)
            {
                if (effect.Modifiers.Count == 0)
                {
                    throw new ConfigException($"{path}：{effect.Id} 没有 modifiers");
                }
                foreach (var modifier in effect.Modifiers)
                {
                    if (!Stats.All.Contains(modifier.Stat))
                    {
                        throw new ConfigException(
                            $"{path}：{effect.Id} 的 stat「{modifier.Stat}」不在固定集合 [{string.Join(", ", Stats.All)}] 里");
                    }
                }
            }
            if (effect.Kind == EffectKind.Control)
            {
                if (!controlIds.Contains(ControlName(effect.Control)))
                {
                    throw new ConfigException(
                        $"{path}：{effect.Id} 的 control「{ControlName(effect.Control)}」不在 control_types 里");
                }
                if (effect.Control == ControlKind.Slow && effect.ValuePct >= 0)
                {
                    throw new ConfigException($"{path}：{effect.Id} 是 slow，value_pct 必须是负数");
                }
            }
        }

        var interaction = file.Interaction ?? new InteractionDef();
        if (interaction.CcDiminishing.StepsPct.Count == 0 || interaction.CcDiminishing.WindowMs <= 0)
        {
            throw new ConfigException($"{path}：cc_diminishing 要有 window_ms > 0 与至少一档 steps_pct");
        }
        if (interaction.Interrupt.CooldownMs < 0 || interaction.Interrupt.ManaRefundPct is < 0 or > 100)
        {
            throw new ConfigException($"{path}：interrupt 的 cooldown_ms / mana_refund_pct 不合法");
        }

        var damage = file.Damage ?? throw new ConfigException($"{path}：缺 damage 段");
        if (damage.Stages.Count != 4
            || damage.Stages[0].Id != "base" || damage.Stages[1].Id != "enhance"
            || damage.Stages[2].Id != "skill" || damage.Stages[3].Id != "final")
        {
            throw new ConfigException($"{path}：damage.stages 必须是 base → enhance → skill → final 四段");
        }
        if (damage.Penetration.CapPct is < 0 or > 100 || damage.Penetration.DefaultPct < 0)
        {
            throw new ConfigException($"{path}：penetration 的 cap_pct 必须在 0–100、default_pct 不能为负");
        }
        if (damage.Resistance.CapPct is < 0 or > 100 || damage.Resistance.DefaultPct < 0)
        {
            throw new ConfigException($"{path}：resistance 的 cap_pct 必须在 0–100、default_pct 不能为负");
        }
        if (damage.Defense.K < 0 || damage.Defense.MinPct is < 0 or > 100)
        {
            throw new ConfigException($"{path}：defense 的 k 不能为负、min_pct 必须在 0–100");
        }
        if (damage.Crit.RatePct is < 0 or > 100 || damage.Crit.DamagePct < 0)
        {
            throw new ConfigException($"{path}：crit 的 rate_pct 必须在 0–100、damage_pct 不能为负");
        }
        if (damage.Random.MinPct <= 0 || damage.Random.MaxPct < damage.Random.MinPct)
        {
            throw new ConfigException($"{path}：random 的 min_pct / max_pct 不合法");
        }
        if (damage.Rounding != "final_round_half_up")
        {
            throw new ConfigException($"{path}：rounding 只认 final_round_half_up");
        }
        ValidateBoundaries(path, damage);

        return new EffectTable(
            file.Version,
            file.Pending,
            byId,
            new InteractionConfig
            {
                InterruptCooldownMs = interaction.Interrupt.CooldownMs,
                InterruptManaRefundPct = interaction.Interrupt.ManaRefundPct,
                HitstunDefaultMs = interaction.Hitstun.DefaultMs,
                CcWindowMs = interaction.CcDiminishing.WindowMs,
                CcStepsPct = interaction.CcDiminishing.StepsPct,
                RefreshOnStack = interaction.Stacking.RefreshOnStack,
            },
            new DamageConfig
            {
                DefenseK = damage.Defense.K,
                DefenseMinPct = damage.Defense.MinPct,
                CritRatePct = damage.Crit.RatePct,
                CritDamagePct = damage.Crit.DamagePct,
                RandomMinPct = damage.Random.MinPct,
                RandomMaxPct = damage.Random.MaxPct,
                PenetrationEnabled = damage.Penetration.Enabled,
                PenetrationDefaultPct = damage.Penetration.DefaultPct,
                PenetrationCapPct = damage.Penetration.CapPct,
                ResistanceEnabled = damage.Resistance.Enabled,
                ResistanceDefaultPct = damage.Resistance.DefaultPct,
                ResistanceCapPct = damage.Resistance.CapPct,
                ArenaDefenseDefault = damage.Boundaries.Arena.DefenseDefault,
            });
    }

    /// <summary>
    /// 边界段只许引用来源，不许再抄一份 1.3 / ratio。数字 1.3 在 battlefield 段里出现就起不来；
    /// 注释里写「1.3」不算，因为这里只看 JSON 的数字。
    /// </summary>
    static void ValidateBoundaries(string path, DamageDef damage)
    {
        var rule = damage.Boundaries.Rule;
        if (string.IsNullOrWhiteSpace(rule))
        {
            throw new ConfigException($"{path}：damage.boundaries.rule 不能为空");
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("damage", out var damageNode)
            || !damageNode.TryGetProperty("boundaries", out var boundaries))
        {
            return;
        }

        if (HasSecondRatio(boundaries))
        {
            throw new ConfigException(
                $"{path}：damage.boundaries 里出现了第二份 1.3 / ratio；只许引用 config/battlefield.json");
        }
    }

    static bool HasSecondRatio(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name == "ratio" || HasSecondRatio(property.Value))
                    {
                        return true;
                    }
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    if (HasSecondRatio(item))
                    {
                        return true;
                    }
                }
                return false;
            case JsonValueKind.Number:
                return node.TryGetDouble(out var value) && Math.Abs(value - 1.3) < 1e-9;
            default:
                return false;
        }
    }

    internal static string ControlName(ControlKind kind) => kind switch
    {
        ControlKind.Root => "root",
        ControlKind.Slow => "slow",
        ControlKind.Stun => "stun",
        ControlKind.Silence => "silence",
        _ => "",
    };
    // ---- 配置读取形状（只给 Load 用） ----

    sealed class EffectFile
    {
        public string Schema { get; init; } = "";
        public int Version { get; init; }
        public bool Pending { get; init; }
        public List<ControlTypeDef>? ControlTypes { get; init; } = [];
        public InteractionDef? Interaction { get; init; }
        public DamageDef? Damage { get; init; }
        public List<EffectDef>? Effects { get; init; } = [];
    }

    sealed class ControlTypeDef
    {
        public string Id { get; init; } = "";
    }

    sealed class InteractionDef
    {
        public InterruptDef Interrupt { get; init; } = new();
        public HitstunDef Hitstun { get; init; } = new();
        public CcDef CcDiminishing { get; init; } = new();
        public StackingDef Stacking { get; init; } = new();
    }

    sealed class InterruptDef
    {
        public int CooldownMs { get; init; }
        public int ManaRefundPct { get; init; } = 100;
    }

    sealed class HitstunDef
    {
        public int DefaultMs { get; init; } = 300;
    }

    sealed class CcDef
    {
        public int WindowMs { get; init; }
        public List<int> StepsPct { get; init; } = [];
    }

    sealed class StackingDef
    {
        public bool RefreshOnStack { get; init; } = true;
    }

    sealed class DamageDef
    {
        public string Schema { get; init; } = "";
        public List<StageDef> Stages { get; init; } = [];
        public DefenseDef Defense { get; init; } = new();
        public CritDef Crit { get; init; } = new();
        public RandomDef Random { get; init; } = new();
        public PenetrationDef Penetration { get; init; } = new();
        public ResistanceDef Resistance { get; init; } = new();
        public string Rounding { get; init; } = "";
        public BoundaryDef Boundaries { get; init; } = new();
    }

    sealed class StageDef
    {
        public string Id { get; init; } = "";
        public string Formula { get; init; } = "";
    }

    sealed class DefenseDef
    {
        public double K { get; init; }
        public double MinPct { get; init; }
    }

    sealed class CritDef
    {
        public double RatePct { get; init; }
        public double DamagePct { get; init; }
    }

    sealed class RandomDef
    {
        public double MinPct { get; init; }
        public double MaxPct { get; init; }
    }

    sealed class PenetrationDef
    {
        public bool Enabled { get; init; }
        public double DefaultPct { get; init; }
        public double CapPct { get; init; } = 100;
    }

    sealed class ResistanceDef
    {
        public bool Enabled { get; init; }
        public double DefaultPct { get; init; }
        public double CapPct { get; init; } = 90;
        public List<string> Types { get; init; } = [];
    }

    sealed class BoundaryDef
    {
        public string Rule { get; init; } = "";
        public ArenaBoundaryDef Arena { get; init; } = new();
    }

    sealed class ArenaBoundaryDef
    {
        public double DefenseDefault { get; init; }
    }
}

/// <summary>固定属性名集合。效果表只认这三个，代码里不留第二套字符串。</summary>
