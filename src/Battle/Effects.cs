using System.Text.Json;
using Mmo.Proto.Battle;

namespace Battle;

/// <summary>
/// Q2 效果层的运行时正本。配置只在 config/effects.json（effect-table/2），
/// 这里只解释它：七类效果、叠层 / 刷新 / 独立计时、驱散 / 免疫 / 净化、
/// CC 递减与打断；伤害四段在 DamagePipeline。
///
/// 一个效果实例只属于一个单位（人 / Boss）。谁施加、叠几层来自技能表的
/// effect 块，不写在效果表里；效果表只说「是什么」。
/// </summary>
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
public static class Stats
{
    public const string AttackPct = "attack_pct";
    public const string DefensePct = "defense_pct";
    public const string HpPctPerTick = "hp_pct_per_tick";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { AttackPct, DefensePct, HpPctPerTick };
}

public sealed class InteractionConfig
{
    public int InterruptCooldownMs { get; init; } = 1000;
    public int InterruptManaRefundPct { get; init; } = 100;
    public int HitstunDefaultMs { get; init; } = 300;
    public int CcWindowMs { get; init; } = 15000;
    public List<int> CcStepsPct { get; init; } = [100];
    public bool RefreshOnStack { get; init; } = true;
}

public sealed class DamageConfig
{
    public double DefenseK { get; init; } = 100;
    public double DefenseMinPct { get; init; } = 10;
    public double CritRatePct { get; init; }
    public double CritDamagePct { get; init; } = 50;
    public double RandomMinPct { get; init; } = 100;
    public double RandomMaxPct { get; init; } = 100;
    public bool PenetrationEnabled { get; init; }
    public double PenetrationDefaultPct { get; init; }
    public double PenetrationCapPct { get; init; } = 100;
    public bool ResistanceEnabled { get; init; }
    public double ResistanceDefaultPct { get; init; }
    public double ResistanceCapPct { get; init; } = 90;
    public double ArenaDefenseDefault { get; init; }
}

/// <summary>效果作用的单位。人和 Boss 都走这一套，不各写一份计时。</summary>
public interface IStatusHost
{
    ulong UnitId { get; }
    long Hp { get; }
    long HpMax { get; }
    bool Down { get; }
    void SetHp(long value);
}

/// <summary>
/// 一个单位身上的效果集合。所有施加入口只有 <see cref="Apply"/> 一个：
/// 技能结算、以后的丹药 / 道具都走这里，不另起一套计时与叠层。
/// </summary>
public sealed class StatusSet(EffectTable table, IStatusHost host)
{
    readonly List<EffectInstance> _active = [];
    readonly List<(string Control, long AtMs)> _controlLog = [];

    public IReadOnlyList<EffectInstance> Active => _active;

    public int AttackPct => Sum(Stats.AttackPct);
    public int DefensePct => Sum(Stats.DefensePct);
    public bool Stunned => HasControl(ControlKind.Stun);
    public bool Silenced => HasControl(ControlKind.Silence);
    public bool Rooted => HasControl(ControlKind.Root) || Stunned;
    public long ShieldLeft => _active.Where(i => i.Def.Kind == EffectKind.Shield).Sum(i => i.ShieldLeft);

    /// <summary>移动速度修正百分比：减速是负数，可能叠多个；夹在 -90% 到 0。</summary>
    public int SlowPct => Math.Clamp(
        _active.Where(i => i.Def.Kind == EffectKind.Control && i.Def.Control == ControlKind.Slow)
            .Sum(i => i.Def.ValuePct), -90, 0);

    int Sum(string stat) => _active.Sum(i => i.Def.Modifiers
        .Where(m => m.Stat == stat).Sum(m => m.Value) * i.Stacks);

    bool HasControl(ControlKind kind) => _active.Any(
        i => i.Def.Kind == EffectKind.Control && i.Def.Control == kind);

    public void Clear() => _active.Clear();

    /// <summary>
    /// 施加一个效果。返回 Applied / Refreshed / Stacked / Immune。
    /// 净化在这里立即结算，不占持续槽；免疫按「目标身上已有标签」判定，
    /// 递减只对 control 且 diminishing=true 的效果生效。
    /// </summary>
    public EffectApply Apply(string effectId, ulong sourceId, long now, int stacks = 1)
    {
        if (!table.TryGet(effectId, out var def))
        {
            return EffectApply.Unknown;
        }

        if (def.Kind == EffectKind.Cleanse)
        {
            var removed = RemoveByTags(def.DispelTags);
            return removed > 0 ? EffectApply.Applied : EffectApply.Immune;
        }

        // 免疫：目标身上任何一个效果带出的标签命中这一条的 immunity_tags，整条不生效。
        var tags = _active.SelectMany(i => i.Def.DispelTags).ToHashSet(StringComparer.Ordinal);
        if (def.ImmunityTags.Any(tags.Contains))
        {
            return EffectApply.Immune;
        }

        var duration = def.DurationMs;
        if (def.Kind == EffectKind.Control && def.Diminishing)
        {
            var pct = NextDiminishingPct(TableControlName(def), now);
            if (pct <= 0)
            {
                return EffectApply.Immune;
            }
            duration = duration * pct / 100;
        }

        var same = _active.FirstOrDefault(i => i.Def.Id == def.Id);
        if (same is not null && def.StackRule != "independent")
        {
            if (def.StackRule == "stack")
            {
                same.Stacks = Math.Min(def.MaxStacks, same.Stacks + 1);
                if (table.Interaction.RefreshOnStack)
                {
                    same.ExpiresAtMs = now + duration;
                }
                return EffectApply.Stacked;
            }

            same.Stacks = Math.Min(def.MaxStacks, same.Stacks);
            same.ExpiresAtMs = now + duration;
            if (def.Kind == EffectKind.Shield)
            {
                same.ShieldLeft = ShieldAmount(def);
            }
            return EffectApply.Refreshed;
        }

        _active.Add(new EffectInstance
        {
            Def = def,
            SourceId = sourceId,
            Stacks = Math.Clamp(stacks, 1, def.MaxStacks),
            ExpiresAtMs = now + duration,
            NextTickAtMs = def.Kind is EffectKind.Hot or EffectKind.Dot ? now + def.TickMs : 0,
            ShieldLeft = def.Kind == EffectKind.Shield ? ShieldAmount(def) : 0,
        });
        return EffectApply.Applied;
    }

    /// <summary>推进服务端时钟：跳 DoT / HoT、到期移除、破盾清掉。只在这里动血量。</summary>
    public StatusTick Tick(long now)
    {
        long hpDelta = 0;
        ulong lastSource = 0;
        var changed = false;

        for (var index = _active.Count - 1; index >= 0; index--)
        {
            var instance = _active[index];

            // 每跳按服务端时钟对齐，落后多少补多少；最后一跳不越过持续时间。
            while (instance.Def.Kind is EffectKind.Hot or EffectKind.Dot
                   && instance.NextTickAtMs > 0
                   && instance.NextTickAtMs <= now
                   && instance.NextTickAtMs <= instance.ExpiresAtMs)
            {
                var pct = instance.Def.Kind == EffectKind.Hot
                    ? instance.Def.AmountPctMaxHp
                    : instance.Def.Modifiers.FirstOrDefault(m => m.Stat == Stats.HpPctPerTick)?.Value ?? 0;
                var amount = (long)Math.Round(host.HpMax * pct * instance.Stacks / 100.0);
                if (instance.Def.Kind == EffectKind.Hot)
                {
                    var before = host.Hp;
                    var healed = Math.Min(host.HpMax - before, amount);
                    if (healed > 0)
                    {
                        host.SetHp(before + healed);
                        hpDelta += healed;
                    }
                }
                else
                {
                    var remaining = Math.Max(0, -amount);
                    if (remaining > 0)
                    {
                        var afterShield = Absorb(remaining);
                        if (afterShield > 0)
                        {
                            host.SetHp(Math.Max(0, host.Hp - afterShield));
                            hpDelta -= afterShield;
                        }
                        lastSource = instance.SourceId;
                    }
                }

                instance.NextTickAtMs += instance.Def.TickMs;
                changed = true;
            }

            if (now >= instance.ExpiresAtMs)
            {
                _active.RemoveAt(index);
                changed = true;
            }
        }

        return new StatusTick(hpDelta, changed, lastSource);
    }

    /// <summary>伤害先吃护盾。返回没有被吸收、要落到血上的那一截。</summary>
    public long Absorb(long damage)
    {
        if (damage <= 0)
        {
            return damage;
        }

        var remaining = damage;
        foreach (var instance in _active.Where(i => i.Def.Kind == EffectKind.Shield).ToList())
        {
            if (remaining <= 0)
            {
                break;
            }
            var used = Math.Min(instance.ShieldLeft, remaining);
            instance.ShieldLeft -= used;
            remaining -= used;
            if (instance.ShieldLeft <= 0)
            {
                _active.Remove(instance);
            }
        }
        return remaining;
    }

    long ShieldAmount(EffectDef def) =>
        (long)Math.Round(host.HpMax * def.AmountPctMaxHp / 100.0);

    int RemoveByTags(IReadOnlyList<string> tags)
    {
        var removed = 0;
        for (var index = _active.Count - 1; index >= 0; index--)
        {
            if (_active[index].Def.DispelTags.Any(tags.Contains))
            {
                _active.RemoveAt(index);
                removed++;
            }
        }
        return removed;
    }

    int NextDiminishingPct(string control, long now)
    {
        _controlLog.RemoveAll(entry => now - entry.AtMs > table.Interaction.CcWindowMs);
        var count = _controlLog.Count(entry => entry.Control == control);
        _controlLog.Add((control, now));
        var steps = table.Interaction.CcStepsPct;
        return steps[Math.Min(count, steps.Count - 1)];
    }

    static string TableControlName(EffectDef def) => EffectTable.ControlName(def.Control);

    /// <summary>整份快照。cleanse 不占槽，不会出现在这里。</summary>
    public List<EffectSnapshot> Snapshots(long now) => _active
        .OrderBy(i => i.Def.Id, StringComparer.Ordinal)
        .Select(i => new EffectSnapshot
        {
            EffectId = i.Def.Id,
            Name = i.Def.Name,
            Kind = KindName(i.Def.Kind),
            SourceId = i.SourceId,
            RemainingMs = (uint)Math.Max(0, i.ExpiresAtMs - now),
            Stacks = (uint)Math.Max(1, i.Stacks),
            ShieldLeft = i.Def.Kind == EffectKind.Shield ? i.ShieldLeft : 0,
        })
        .ToList();

    internal static string KindName(EffectKind kind) => kind switch
    {
        EffectKind.Buff => "buff",
        EffectKind.Debuff => "debuff",
        EffectKind.Control => "control",
        EffectKind.Hot => "hot",
        EffectKind.Dot => "dot",
        EffectKind.Shield => "shield",
        EffectKind.Cleanse => "cleanse",
        _ => "",
    };

    /// <summary>效果集合变了就整份重发；key 是单位编号，人 / Boss 共用一条消息形状。</summary>
    public EffectState State(long now) => new()
    {
        UnitId = host.UnitId,
        Effects = { Snapshots(now) },
    };
}
