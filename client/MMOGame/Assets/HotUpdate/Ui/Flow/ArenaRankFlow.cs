using System;
using Mmo.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 竞技场赛季段位与排行榜（U19 / B11 的界面欠项）。
    ///
    /// 三条口全是只读加一个领取：段位 `GET /arena/rank`、排行 `GET /arena/leaderboard`、
    /// 领档位外观 `POST /arena/appearance/claim`。分数怎么加、档位门槛、够不够领、
    /// 这一季领没领过全在角色服判；这里只发角色号与要领的档位键，别的一个数都不带。
    /// </summary>
    public sealed partial class UiSocialFlow : MonoBehaviour
    {
        // ------------------------------------------------------------------ 段位与排行榜（U19 / B11）

        /// <summary>最近一次 `GET /arena/rank` 的回包。分数、档位、下一档门槛全从这里取。</summary>
        public ArenaRankOutcome LastRank { get; private set; } = ArenaRankOutcome.Failure("not_loaded");

        public bool HasRank { get; private set; }

        /// <summary>段位这条口发过请求没有。发过但没拿到 200 时，屏上写回执而不是「尚未请求」。</summary>
        public bool RankTried { get; private set; }

        /// <summary>最近一次 `GET /arena/leaderboard` 的回包。行顺序就是服务端顺序。</summary>
        public ArenaBoardOutcome LastBoard { get; private set; } = ArenaBoardOutcome.Failure("not_loaded");

        public bool HasBoard { get; private set; }

        public bool BoardTried { get; private set; }

        /// <summary>最近一次领段位外观的回包。被拒时原因原样带出。</summary>
        public ArenaClaimOutcome LastClaim { get; private set; } = ArenaClaimOutcome.Failure("not_loaded");

        public bool HasClaim { get; private set; }

        /// <summary>读一次本季段位。进段位屏时调；档位表与领取状态都按回包重画。</summary>
        public void LoadArenaRank(Action done = null)
        {
            if (_characterId == 0)
            {
                LastRank = ArenaRankOutcome.Failure("no_character");
                HasRank = false;
                RankTried = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            RankTried = true;
            _send(ArenaRank.RankQuery(_characterId), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastRank = ArenaRank.ParseRank(text);
                    HasRank = LastRank.Ok;
                    Error = LastRank.Ok ? string.Empty : LastRank.Reason;
                    Debug.Log("u19_rank=" + (LastRank.Ok ? "ok" : "reject")
                        + " season=" + LastRank.SeasonId
                        + " score=" + LastRank.Score
                        + " tier=" + LastRank.TierKey
                        + " wins=" + LastRank.Wins
                        + " next_min=" + LastRank.NextTierMinScore
                        + " tiers=" + LastRank.Tiers.Count
                        + " reason=" + LastRank.Reason);
                }
                else
                {
                    LastRank = ArenaRankOutcome.Failure("http_" + status);
                    HasRank = false;
                    Error = UiStrings.ArenaRankFlow.T001 + status;
                    Debug.Log("u19_rank=reject status=" + status);
                }

                if (done != null) done();
            });
        }

        /// <summary>读一次本季排行。不带 limit：多少名由角色服按配置截断。</summary>
        public void LoadLeaderboard(Action done = null)
        {
            if (_characterId == 0)
            {
                LastBoard = ArenaBoardOutcome.Failure("no_character");
                HasBoard = false;
                BoardTried = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            BoardTried = true;
            _send(ArenaRank.BoardPath, string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastBoard = ArenaRank.ParseBoard(text);
                    HasBoard = LastBoard.Ok;
                    Error = LastBoard.Ok ? string.Empty : LastBoard.Reason;
                    Debug.Log("u19_board=" + (LastBoard.Ok ? "ok" : "reject")
                        + " season=" + LastBoard.SeasonId
                        + " limit=" + LastBoard.Limit
                        + " rows=" + LastBoard.Rows.Count
                        + " reason=" + LastBoard.Reason);
                }
                else
                {
                    LastBoard = ArenaBoardOutcome.Failure("http_" + status);
                    HasBoard = false;
                    Error = UiStrings.ArenaRankFlow.T002 + status;
                    Debug.Log("u19_board=reject status=" + status);
                }

                if (done != null) done();
            });
        }

        /// <summary>
        /// 领这一档的专属外观。只发角色号与档位键（事务号网关生成）；
        /// 够不够门槛、季度对不对、这一档领没领过全在角色服判，界面照回包写。
        /// </summary>
        public void ClaimAppearance(string tierKey, Action done = null)
        {
            if (_characterId == 0)
            {
                LastClaim = ArenaClaimOutcome.Failure("no_character");
                HasClaim = true;
                Debug.Log("u19_claim=reject reason=no_character");
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(ArenaRank.ClaimPath, ArenaRank.ClaimBody(_characterId, tierKey), _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastClaim = ArenaRank.ParseClaim(text);
                        Error = LastClaim.Ok ? string.Empty : LastClaim.DisplayText;
                        Debug.Log("u19_claim=" + (LastClaim.Ok ? "ok" : "reject")
                            + " tier=" + LastClaim.TierKey
                            + " item=" + LastClaim.ItemId
                            + " bag_slot=" + LastClaim.BagSlot
                            + " duplicate=" + (LastClaim.Duplicate ? "true" : "false")
                            + " reason=" + LastClaim.Reason);
                    }
                    else
                    {
                        LastClaim = ArenaClaimOutcome.Failure("http_" + status);
                        Error = UiStrings.ArenaRankFlow.T003 + status;
                        Debug.Log("u19_claim=reject status=" + status);
                    }

                    HasClaim = true;

                    // 领成功之后档位表要跟着变（claimed 由服务端写定）：再读一次只读口，
                    // 不自己把这一档标成已领。
                    if (LastClaim.Ok)
                    {
                        LoadArenaRank(done);
                        return;
                    }

                    if (done != null) done();
                });
        }
    }
}
