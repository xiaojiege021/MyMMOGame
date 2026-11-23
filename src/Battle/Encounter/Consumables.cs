using System.Diagnostics;
using Mmo.Proto.Battle;
using Mmo.Proto.Character;
using TalismanPool = Mmo.Proto.Character.TalismanPool;

namespace Battle;

// 14.8 D3c：Encounter 按域拆出的分片，纯搬代码。
public sealed partial class Encounter
{
    // ---- 用符 ----

    /// <summary>
    /// 用一张符（红符回血、蓝符回蓝）。方案 12.1 的边界都落在这里：
    ///   · 只回复自己——请求里没有目标编号，这里也恒拿 member 自己当目标，
    ///     队友只能靠牧师的技能治，符这条路指不过去；
    ///   · 不复活——倒地（Hp ≤ 0）时直接拒绝，死了就点不了符，也就没有「把自己拉起来」；
    ///   · 回复不超过同级药——回复值由角色服按配置给，配置校验里已经卡死上限，
    ///     这里只按池上限截断，不自己算第二套数；
    ///   · 蓝符不回血、不提高精神、不增加伤害——只写 Mana，另一个池与属性一律不碰。
    ///
    /// 扣符交给角色服（背包只有它能让写），扣不掉就不回：宁可这一手没做成，
    /// 也不做「本地凭空变出一张符」或「回了但符没扣」。
    /// 红符与牧师治疗共用治疗组冷却，蓝符用蓝量组，两组互不占用。
    /// </summary>
    public ServerMessage UseTalisman(Member member, UseTalismanRequest request)
    {
        var itemId = (request.ItemId ?? "").Trim();
        if (itemId.Length == 0)
        {
            return RejectTalisman(itemId, "not_talisman", 0, member.Hp, member.Mana);
        }

        // 只读查询：这个编号回血还是回蓝，只有角色服读配置说了算。不扣、不写库。
        // 先在锁外问清楚，才能在扣符之前判「池满没满」「哪一组冷却到没到」。
        var info = _talismans.Describe(member.Id, itemId);
        if (!info.Ok)
        {
            return RejectTalisman(itemId, info.Reason, 0, member.Hp, member.Mana);
        }

        string precheck;
        long now;
        int seq;
        lock (_gate)
        {
            now = NowMs();
            precheck = TalismanPrecheck(member, now, info.Pool);
            seq = ++_talismanUses;
        }

        if (precheck.Length > 0)
        {
            var retry = 0u;
            if (precheck == "cooldown")
            {
                lock (_gate)
                {
                    var readyAt = info.Pool == TalismanPool.Mana
                        ? member.ManaGroupReadyAtMs
                        : member.HealGroupReadyAtMs;
                    retry = (uint)Math.Max(0, readyAt - NowMs());
                }
            }

            return RejectTalisman(itemId, precheck, retry, member.Hp, member.Mana);
        }

        // 事务号在这一场里唯一：同一场同一个人第几张，重放不会扣两次。
        var txnId = "talisman:" + SessionId.ToString("x16")
            + ":" + member.Id.ToString("x16") + ":" + seq.ToString();

        // 网络调用放在锁外面：一次扣符最长等五秒，不能把整场的结算一起挂住。
        var spend = _talismans.Spend(member.Id, itemId, txnId);
        if (!spend.Ok)
        {
            return RejectTalisman(itemId, spend.Reason, 0, member.Hp, member.Mana);
        }

        lock (_gate)
        {
            // 扣符这一下里这场可能已经结束了、人倒了，或者池被别人加满了：
            // 正常不会走到这里（上面预判过），真撞上就按对应原因拒绝，不回另一个池。
            if (member.Down)
            {
                return RejectTalisman(itemId, "caster_down", 0, member.Hp, member.Mana);
            }

            // 回包里的池是权威：配置改了要重启才生效，两次调用之间不会变。
            if (spend.Pool == TalismanPool.Mana && member.Mana < member.ManaMax)
            {
                var restored = Math.Min(spend.ManaRestore, member.ManaMax - member.Mana);
                member.Mana += restored;
                if (spend.CooldownMs > 0)
                {
                    // 只写蓝量组：和蓝药共用，和治疗组、红符都不共用。
                    member.ManaGroupReadyAtMs = NowMs() + spend.CooldownMs;
                }

                return TalismanApplied(member, itemId, spend, 0, restored);
            }

            if (spend.Pool == TalismanPool.Hp && member.Hp < member.HpMax)
            {
                var healed = Math.Min(spend.HpRestore, member.HpMax - member.Hp);
                member.Hp += healed;
                if (spend.CooldownMs > 0)
                {
                    // 只写治疗组：和牧师治疗共用，和蓝符不共用。
                    member.HealGroupReadyAtMs = NowMs() + spend.CooldownMs;
                }

                return TalismanApplied(member, itemId, spend, healed, 0);
            }

            return RejectTalisman(itemId,
                spend.Pool == TalismanPool.Mana ? "mana_full" : "hp_full",
                0, member.Hp, member.Mana);
        }
    }

    /// <summary>
    /// 符真扣掉了，把这一个池的增量回给本人。另一个池的数值原样回、不改动——
    /// 「蓝符不回血」「红符不回蓝」在这里也落死：healed 与 mana_restored 只有一个非零。
    /// 伤害、精神、职业属性一个都不碰（方案 12.1「不提高精神，不增加伤害」）。
    /// </summary>
    static ServerMessage TalismanApplied(
        Member member, string itemId, TalismanSpendResult spend, long healed, long restored) =>
        new()
        {
            TalismanUsed = new TalismanUsed
            {
                CasterId = member.Id,
                ItemId = itemId,
                Healed = healed,
                HpAfter = member.Hp,
                CooldownMs = (uint)Math.Max(0, spend.CooldownMs),
                CooldownGroup = spend.CooldownGroup,
                CountLeft = spend.CountLeft,
                Pool = spend.Pool.ToString().ToLowerInvariant(),
                Restored = healed + restored,
                ManaAfter = member.Mana,
                ManaRestored = restored,
            },
        };

    /// <summary>
    /// 用符的本地前置判定。池由角色服的只读查询给，所以满池与冷却都查对的那一个：
    /// 红符看血与治疗组，蓝符看蓝与蓝量组。返回空串表示能进下一步（去扣符）。
    /// 这一步在扣符之前，判不过一个字都不写库。
    /// </summary>
    string TalismanPrecheck(Member member, long now, TalismanPool pool)
    {
        if (_closed || _bossHp <= 0)
        {
            return "not_in_battle";
        }

        // 倒地不能点符：倒地只能靠复活卷轴或牧师的生息，符不复活。
        if (member.Down)
        {
            return "caster_down";
        }

        if (pool == TalismanPool.Mana)
        {
            // 冷却先于池满：两张连着用时，客户端该拿到「还差多久」，
            // 而不是「蓝满了」——后者会让人以为等蓝耗掉就能再用。
            if (now < member.ManaGroupReadyAtMs)
            {
                return "cooldown";
            }

            if (member.Mana >= member.ManaMax)
            {
                // 满蓝用了也是白烧一张，直接挡下，别让玩家白扣。
                return "mana_full";
            }

            return "";
        }

        if (pool != TalismanPool.Hp)
        {
            // 只有查得到池的符能走到这里；Unspecified 不该出现。
            return "not_talisman";
        }

        if (now < member.HealGroupReadyAtMs)
        {
            return "cooldown";
        }

        if (member.Hp >= member.HpMax)
        {
            // 满血用了也是白烧一张，直接挡下，别让玩家白扣。
            return "hp_full";
        }

        return "";
    }

    static ServerMessage RejectTalisman(
        string itemId, string reason, uint retryAfterMs, long hpAfter, long manaAfter) =>
        new()
        {
            TalismanRejected = new TalismanRejected
            {
                ItemId = itemId,
                Reason = reason,
                RetryAfterMs = retryAfterMs,
                HpAfter = hpAfter,
                ManaAfter = manaAfter,
            },
        };

    // ---- 吃丹 ----

    /// <summary>
    /// 吃一颗丹（回春丹 / 气血丹回血，凝神丹 / 回灵丹回蓝）。方案 12.1 的边界：
    ///   · 只回复自己——请求里没有目标编号，这里恒拿 member 自己当目标；
    ///   · 不复活——倒地（Down）直接拒绝，丹不能把人拉起来；
    ///   · 回复不超过同级符那条线由配置定，这里只按池上限截断，不自己算第二套数；
    ///   · 蓝药不回血、不提高精神、不增加伤害——只写 Mana，另一个池与属性一律不碰。
    ///
    /// 扣丹交给角色服（背包只有它能写），扣不掉就不回。
    /// 红药和红符、牧师治疗共用 heal 组，蓝药和蓝符共用 mana 组——组名由角色服回包给，
    /// 这里照着写本场的计时器，所以「符和药共用冷却」在代码里也只有一个来源。
    /// 突破丹不在这条路上：角色服的 DescribePill 会把它判成 not_pill。
    /// </summary>
    public ServerMessage UsePill(Member member, UsePillRequest request)
    {
        var itemId = (request.ItemId ?? "").Trim();
        if (itemId.Length == 0)
        {
            return RejectPill(itemId, "not_pill", 0, member.Hp, member.Mana);
        }

        var info = _pills.Describe(member.Id, itemId);
        if (!info.Ok)
        {
            return RejectPill(itemId, info.Reason, 0, member.Hp, member.Mana);
        }

        string precheck;
        int seq;
        lock (_gate)
        {
            var now = NowMs();
            precheck = PillPrecheck(member, now, info.Pool);
            seq = ++_pillUses;
        }

        if (precheck.Length > 0)
        {
            var retry = 0u;
            if (precheck == "cooldown")
            {
                lock (_gate)
                {
                    var readyAt = info.Pool == PillPool.Mana
                        ? member.ManaGroupReadyAtMs
                        : member.HealGroupReadyAtMs;
                    retry = (uint)Math.Max(0, readyAt - NowMs());
                }
            }

            return RejectPill(itemId, precheck, retry, member.Hp, member.Mana);
        }

        // 事务号在这一场里唯一：同一场同一个人第几颗，重放不会扣两次。
        var txnId = "pill:" + SessionId.ToString("x16")
            + ":" + member.Id.ToString("x16") + ":" + seq.ToString();

        // 网络调用放在锁外面：一次扣丹最长等五秒，不能把整场的结算一起挂住。
        var spend = _pills.Spend(member.Id, itemId, txnId);
        if (!spend.Ok)
        {
            return RejectPill(itemId, spend.Reason, 0, member.Hp, member.Mana);
        }

        lock (_gate)
        {
            if (member.Down)
            {
                return RejectPill(itemId, "caster_down", 0, member.Hp, member.Mana);
            }

            if (spend.Pool == PillPool.Mana && member.Mana < member.ManaMax)
            {
                var restored = Math.Min(spend.ManaRestore, member.ManaMax - member.Mana);
                member.Mana += restored;
                if (spend.CooldownMs > 0)
                {
                    // 和蓝符同一个组：符刚用过，这一颗也要等同一段冷却。
                    member.ManaGroupReadyAtMs = NowMs() + spend.CooldownMs;
                }

                return PillApplied(member, itemId, spend, 0, restored);
            }

            if (spend.Pool == PillPool.Hp && member.Hp < member.HpMax)
            {
                var healed = Math.Min(spend.HpRestore, member.HpMax - member.Hp);
                member.Hp += healed;
                if (spend.CooldownMs > 0)
                {
                    // 和红符、牧师治疗同一个组。
                    member.HealGroupReadyAtMs = NowMs() + spend.CooldownMs;
                }

                return PillApplied(member, itemId, spend, healed, 0);
            }

            return RejectPill(itemId,
                spend.Pool == PillPool.Mana ? "mana_full" : "hp_full",
                0, member.Hp, member.Mana);
        }
    }

    /// <summary>
    /// 丹真扣掉了，把这一个池的增量回给本人。另一个池的数值原样回、不改动——
    /// 「蓝药不回血」「红药不回蓝」在这里落死：healed 与 mana_restored 只有一个非零。
    /// </summary>
    static ServerMessage PillApplied(
        Member member, string itemId, PillSpendResult spend, long healed, long restored) =>
        new()
        {
            PillUsed = new PillUsed
            {
                CasterId = member.Id,
                ItemId = itemId,
                Healed = healed,
                HpAfter = member.Hp,
                CooldownMs = (uint)Math.Max(0, spend.CooldownMs),
                CooldownGroup = spend.CooldownGroup,
                CountLeft = spend.CountLeft,
                Pool = spend.Pool.ToString().ToLowerInvariant(),
                Restored = healed + restored,
                ManaAfter = member.Mana,
                ManaRestored = restored,
            },
        };

    /// <summary>
    /// 吃丹的本地前置判定。池由角色服的只读查询给，所以满池与冷却都查对的那一个：
    /// 红药看血与治疗组，蓝药看蓝与蓝量组。返回空串表示能进下一步（去扣丹）。
    /// </summary>
    string PillPrecheck(Member member, long now, PillPool pool)
    {
        if (_closed || _bossHp <= 0)
        {
            return "not_in_battle";
        }

        if (member.Down)
        {
            return "caster_down";
        }

        if (pool == PillPool.Mana)
        {
            if (now < member.ManaGroupReadyAtMs)
            {
                return "cooldown";
            }

            if (member.Mana >= member.ManaMax)
            {
                return "mana_full";
            }

            return "";
        }

        if (pool != PillPool.Hp)
        {
            return "not_pill";
        }

        if (now < member.HealGroupReadyAtMs)
        {
            return "cooldown";
        }

        if (member.Hp >= member.HpMax)
        {
            return "hp_full";
        }

        return "";
    }

    static ServerMessage RejectPill(
        string itemId, string reason, uint retryAfterMs, long hpAfter, long manaAfter) =>
        new()
        {
            PillRejected = new PillRejected
            {
                ItemId = itemId,
                Reason = reason,
                RetryAfterMs = retryAfterMs,
                HpAfter = hpAfter,
                ManaAfter = manaAfter,
            },
        };
}
