using System.Text.Json;
using Mmo.Proto.Battle;

namespace Battle;

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

