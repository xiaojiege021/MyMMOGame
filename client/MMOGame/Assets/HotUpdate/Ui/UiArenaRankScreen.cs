using System.Collections.Generic;
using System.Globalization;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 竞技场赛季段位屏（B11 的界面欠项）。分数、当前档、下一档门槛、每档够没够、
    /// 这一季领没领全照抄 `GET /arena/rank`；领外观只 POST `/arena/appearance/claim`
    /// （请求体只有角色号与档位键，事务号由网关生成）。
    ///
    /// 客户端不比分数门槛、不推算「还差多少分」、不按分数重排档位，也不读段位配置表。
    /// 段位只影响荣誉与专属外观，不改竞技场模板属性。
    /// </summary>
    public static class UiArenaRankScreen
    {
        public const string ReadyLog = "u19_rank_screen=ready";

        public static void Build(UiShell shell, UiSocialFlow flow)
        {
            UiUpgradeViews.Note(shell, UiSocialCodex.RankNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(16f);
            var leftW = (m.Width - pad * 2f - m.Px(40f)) * 0.42f;
            var rightX = pad + leftW + m.Px(40f);
            var rightW = m.Width - pad - rightX;

            var rank = flow.LastRank;
            var shown = flow.HasRank;
            var notYet = UiStrings.UiArenaRankScreen.T008;

            // ---------------------------------------------------------- 左：本季分数与当前档

            var leftH = m.Px(600f);
            var current = shell.Panel(UiStrings.UiArenaRankScreen.T001,
                m.Band(pad, top + m.Px(56f), leftW, leftH), shell.Size(34f));

            // 六行全是回包事实：季、分数、档名、胜场、下一档、下一档门槛。
            // 下一档为空就写「已是最高档」——这是回包字段的有无，不是本地算出来的结论。
            var nextKey = rank.NextTierKey;
            var nextText = string.IsNullOrEmpty(nextKey)
                ? UiStrings.UiArenaRankScreen.T021
                : ArenaRank.TierLabel(nextKey, string.Empty);
            UiUpgradeViews.Rows(shell, current,
                new[]
                {
                    UiStrings.UiArenaRankScreen.T002,
                    UiStrings.UiArenaRankScreen.T003,
                    UiStrings.UiArenaRankScreen.T004,
                    UiStrings.UiArenaRankScreen.T005,
                    UiStrings.UiArenaRankScreen.T006,
                    UiStrings.UiArenaRankScreen.T007,
                },
                new[]
                {
                    shown ? rank.SeasonId.ToString(CultureInfo.InvariantCulture) : notYet,
                    shown ? rank.Score.ToString(CultureInfo.InvariantCulture) : notYet,
                    shown ? ArenaRank.TierLabel(rank.TierKey, rank.TierName) : notYet,
                    shown ? rank.Wins.ToString(CultureInfo.InvariantCulture) : notYet,
                    shown ? nextText : notYet,
                    shown && !string.IsNullOrEmpty(nextKey)
                        ? ScoreText(rank.NextTierMinScore)
                        : (shown ? UiStrings.UiArenaRankScreen.T022 : notYet),
                },
                m.Px(72f), m.Px(28f));

            shell.ButtonIn(current.Root,
                new Rect(m.Px(28f), m.Px(36f), m.Px(260f), m.Px(76f)),
                UiStrings.UiArenaRankScreen.T009,
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.LoadArenaRank(shell.RefreshPage);
                }, UiButtonKind.Primary);

            // ---------------------------------------------------------- 右：档位与专属外观

            var tiers = shown ? rank.Tiers : new List<ArenaTier>();
            var tierRowH = m.Px(104f);
            var ladderH = m.Px(120f) + Mathf.Max(tiers.Count, 1) * tierRowH;
            var ladder = shell.Panel(UiStrings.UiArenaRankScreen.T010,
                m.Band(rightX, top + m.Px(56f), rightW, ladderH), shell.Size(34f));

            if (tiers.Count == 0)
            {
                shell.LabelIn(ladder.Root, new Rect(m.Px(28f), ladderH - m.Px(160f), rightW - m.Px(56f), m.Px(40f)),
                    notYet, UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);
            }

            for (var i = 0; i < tiers.Count; i++)
            {
                var tier = tiers[i];
                var y = ladderH - m.Px(120f) - (i + 1) * tierRowH + m.Px(24f);

                // 档名与门槛都照抄；顺序就是回包顺序，不按分数重排。
                shell.LabelIn(ladder.Root, new Rect(m.Px(28f), y + m.Px(56f), rightW - m.Px(56f), m.Px(34f)),
                    ArenaRank.TierLabel(tier.Key, tier.Name), UiTextRole.Body, shell.Size(26f),
                    TextAnchor.MiddleLeft, false);
                shell.LabelIn(ladder.Root, new Rect(m.Px(28f), y + m.Px(14f), rightW * 0.34f, m.Px(34f)),
                    UiStrings.UiArenaRankScreen.T011 + ScoreText(tier.MinScore), UiTextRole.Muted,
                    shell.Size(21f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(ladder.Root,
                    new Rect(m.Px(28f) + rightW * 0.34f, y + m.Px(14f), rightW * 0.34f, m.Px(34f)),
                    UiStrings.UiArenaRankScreen.T013 + tier.AppearanceItemId, UiTextRole.Faint,
                    shell.Size(20f), TextAnchor.MiddleLeft, false);

                // 领没领、够没够只认回包的两个布尔，客户端不拿分数跟门槛比。
                var rect = new Rect(rightW - m.Px(28f) - m.Px(160f), y + m.Px(24f), m.Px(160f), m.Px(56f));
                if (tier.Claimed)
                {
                    shell.Chip(ladder.Root, rect, UiStrings.UiArenaRankScreen.T015, UiChipKind.Success);
                }
                else if (!tier.Reached)
                {
                    shell.Chip(ladder.Root, rect, UiStrings.UiArenaRankScreen.T016, UiChipKind.Mute);
                }
                else
                {
                    var key = tier.Key;
                    shell.ButtonIn(ladder.Root, rect, UiStrings.UiArenaRankScreen.T014,
                        () =>
                        {
                            if (flow.Busy)
                            {
                                return;
                            }

                            flow.ClaimAppearance(key, shell.RefreshPage);
                        }, UiButtonKind.Primary);
                }
            }

            // ---------------------------------------------------------- 右下：领取回包

            var claim = flow.LastClaim;
            var resultH = m.Px(320f);
            var result = shell.Panel(UiStrings.UiArenaRankScreen.T017,
                m.Band(rightX, top + m.Px(56f) + ladderH + gap, rightW, resultH), shell.Size(34f));
            UiUpgradeViews.Result(shell, result, m.Px(216f), UiStrings.UiArenaRankScreen.T018,
                flow.HasClaim ? claim.DisplayText : notYet);
            UiUpgradeViews.Result(shell, result, m.Px(160f), UiStrings.UiArenaRankScreen.T013,
                flow.HasClaim && claim.Ok ? claim.ItemId : UiStrings.UiArenaRankScreen.T022);
            UiUpgradeViews.Result(shell, result, m.Px(104f), UiStrings.UiArenaRankScreen.T019,
                flow.HasClaim && claim.Ok
                    ? claim.BagSlot.ToString(CultureInfo.InvariantCulture)
                    : UiStrings.UiArenaRankScreen.T022);
            UiUpgradeViews.Result(shell, result, m.Px(48f), UiStrings.UiArenaRankScreen.T020,
                flow.HasClaim && claim.Duplicate
                    ? UiStrings.UiArenaRankScreen.T023
                    : UiStrings.UiArenaRankScreen.T022);

            Debug.Log(ReadyLog + " shown=" + (shown ? "true" : "false")
                + " season=" + rank.SeasonId
                + " score=" + rank.Score
                + " tier=" + rank.TierKey
                + " tiers=" + tiers.Count
                + " reached=" + ReachedCount(tiers)
                + " claim=" + (flow.HasClaim ? (claim.Ok ? "ok" : "reject") : "none"));
        }

        /// <summary>「300 分」。单位进文案表，数值只从回包来。</summary>
        static string ScoreText(int score) =>
            score.ToString(CultureInfo.InvariantCulture) + UiStrings.UiArenaRankScreen.T012;

        static int ReachedCount(List<ArenaTier> tiers)
        {
            var count = 0;
            for (var i = 0; i < tiers.Count; i++)
            {
                if (tiers[i].Reached)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
