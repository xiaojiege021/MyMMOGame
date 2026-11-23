using System.Diagnostics;
using Mmo.Proto.Battle;
using Mmo.Proto.Character;
using TalismanPool = Mmo.Proto.Character.TalismanPool;

namespace Battle;

// 14.8 D3c：Encounter 按域拆出的分片，纯搬代码。
public sealed partial class Encounter
{
    // ---- 用卷轴 ----

    /// <summary>
    /// 用一张复活卷轴。方案 12.1 写定「只拉起自己，不能拉队友」——请求里没有
    /// 目标编号，这里也只把发起者本人的血拉起来，队友只能靠牧师的生息。
    /// 「结算后或人已离开就拒绝」「不满血、不回蓝、不加属性」都落在这几行：
    /// 生命按角色服回包里的比例回到一小段，蓝量与属性一个字段都不写。
    ///
    /// 冷却是这个人的全局冷却，存在角色库：先只读查到期时刻，本地判不过就
    /// 根本不扣卷轴；判过了才让角色服在一笔事务里扣卷、扣当前级经验、写冷却。
    /// 这一段不开始生息技能的冷却，也不占用治疗组、蓝量组——卷轴是第三段计时。
    /// 扣成功之后必须把人拉起来（卷轴、经验、冷却都已经落库），不能白扣。
    /// </summary>
    public ServerMessage UseReviveScroll(Member member, UseReviveScrollRequest request)
    {
        var itemId = (request.ItemId ?? "").Trim();
        if (itemId.Length == 0)
        {
            return RejectScroll(itemId, "not_scroll", 0, member.Hp, member.Mana);
        }

        // 只读查询：编号对不对、冷却到没到、背包有几张、回血比例多少。
        // 冷却到期来自角色库那一行，不是本场的计时器，所以换一场仍然读到它。
        var info = _scrolls.Describe(member.Id, itemId);
        if (!info.Ok)
        {
            return RejectScroll(itemId, info.Reason, 0, member.Hp, member.Mana);
        }

        string precheck;
        int seq;
        uint retry = 0;
        lock (_gate)
        {
            precheck = ScrollPrecheck(member, info);
            seq = ++_scrollUses;
            if (precheck == "cooldown")
            {
                var left = Math.Max(0L, info.ReadyAtMs - info.NowMs);
                retry = (uint)Math.Min(left, (long)uint.MaxValue);
            }
        }

        if (precheck.Length > 0)
        {
            return RejectScroll(itemId, precheck, retry, member.Hp, member.Mana);
        }

        // 事务号这一场唯一：同一场同一个人第几张，重放不会扣两次。
        var txnId = "scroll:" + SessionId.ToString("x16")
            + ":" + member.Id.ToString("x16") + ":" + seq.ToString();

        // 网络调用放在锁外面：一次扣卷最长等五秒，不能把整场的结算一起挂住。
        var used = _scrolls.Use(member.Id, itemId, txnId);
        if (!used.Ok)
        {
            // 角色服那笔事务自己再核一次冷却（两个战斗进程同时点也只过一笔）。
            return RejectScroll(itemId, used.Reason, 0, member.Hp, member.Mana);
        }

        lock (_gate)
        {
            // 按回包里的比例回到一小段生命：不满血、不回蓝、不加属性。
            var restore = Math.Max(1L, member.HpMax * used.HpRestorePct / 100);
            if (restore >= member.HpMax)
            {
                restore = Math.Max(1L, member.HpMax - 1);
            }

            member.Hp = restore;
            return new ServerMessage
            {
                ScrollUsed = new ReviveScrollUsed
                {
                    CasterId = member.Id,
                    ItemId = itemId,
                    HpAfter = member.Hp,
                    ManaAfter = member.Mana,
                    ExpCost = used.ExpCost,
                    Level = used.Level,
                    ExpAfter = used.ExpAfter,
                    CooldownMs = (uint)Math.Max(0, used.CooldownMs),
                    ReadyAtMs = used.ReadyAtMs,
                    CountLeft = used.CountLeft,
                },
            };
        }
    }

    /// <summary>
    /// 用卷轴的本地前置判定，全部在扣卷之前。冷却先于背包空判：还在冷却时
    /// 第二张该回「还差多久」，而不是「没卷轴了」让人以为买一张就能再用。
    /// </summary>
    string ScrollPrecheck(Member member, ReviveScrollInfo info)
    {
        if (_closed || _bossHp <= 0)
        {
            // 这场已经结算：方案 8.3 写定结算后拒绝。
            return "not_in_battle";
        }

        if (!_members.ContainsKey(member.Id))
        {
            // 人已经离开这场，同样拒绝。
            return "left_battle";
        }

        if (!member.Down)
        {
            // 还活着点不了自救卷轴；拉队友只能走生息。
            return "not_down";
        }

        if (info.NowMs < info.ReadyAtMs)
        {
            return "cooldown";
        }

        if (info.CountLeft <= 0)
        {
            return "no_scroll";
        }

        return "";
    }

    static ServerMessage RejectScroll(
        string itemId, string reason, uint retryAfterMs, long hpAfter, long manaAfter) =>
        new()
        {
            ScrollRejected = new ReviveScrollRejected
            {
                ItemId = itemId,
                Reason = reason,
                RetryAfterMs = retryAfterMs,
                HpAfter = hpAfter,
                ManaAfter = manaAfter,
            },
        };
}
