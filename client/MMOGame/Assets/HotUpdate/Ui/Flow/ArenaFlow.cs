using System;
using System.Collections.Generic;
using System.Globalization;
using Mmo.Net;
using Mmo.Proto.Battle;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 竞技场与战场进场（U12 / N5）与城战同屏档位翻译（P3-3a2）。
    /// </summary>
    public sealed partial class UiSocialFlow : MonoBehaviour
    {
        // ------------------------------------------------------------------ 竞技场与战场进场（U12 / N5）

        /// <summary>
        /// 竞技场进场。走 `POST /versus/arena/enter`，请求体只带角色号与模式编号；
        /// 进场路径、这一场多少人、场次号全认回包，客户端不按 1v1 / 3v3 自己乘。
        /// </summary>
        public void EnterArena(string arenaMode, Action done = null)
        {
            if (_characterId == 0)
            {
                LastArena = VersusOutcome.Failure("no_character");
                HasArena = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            ArenaTried = true;
            _send(VersusEntry.ArenaPath, VersusEntry.ArenaBody(_characterId, arenaMode), _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastArena = VersusEntry.Parse(text);
                        HasArena = true;
                        Error = string.Empty;
                        Debug.Log("u12_arena=ok mode=" + LastArena.ArenaMode
                            + " party_size=" + LastArena.PartySize
                            + " battle_path=" + LastArena.BattlePath
                            + " reason=" + LastArena.Reason);
                    }
                    else
                    {
                        LastArena = VersusOutcome.Failure("http_" + status);
                        HasArena = false;
                        Error = "竞技场进场没回应：" + status;
                        Debug.Log("u12_arena=reject status=" + status);
                    }

                    if (done != null) done();
                });
        }

        /// <summary>
        /// 战场进场。走 `POST /versus/battlefield/enter`，`match` 是这一场的场次号、
        /// `team` 是 a / b，两个都只作请求参数：能不能进、和谁同队由网关与战斗服判。
        /// </summary>
        public void EnterBattlefield(string match, string team, Action done = null)
        {
            if (_characterId == 0)
            {
                LastBattlefield = VersusOutcome.Failure("no_character");
                HasBattlefield = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            BattlefieldTried = true;
            _send(VersusEntry.BattlefieldPath,
                VersusEntry.BattlefieldBody(_characterId, match, team), _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastBattlefield = VersusEntry.Parse(text);
                        HasBattlefield = true;
                        Error = string.Empty;
                        Debug.Log("u12_battlefield=ok match=" + LastBattlefield.Battlefield
                            + " team=" + LastBattlefield.Team
                            + " battle_path=" + LastBattlefield.BattlePath
                            + " reason=" + LastBattlefield.Reason);
                    }
                    else
                    {
                        LastBattlefield = VersusOutcome.Failure("http_" + status);
                        HasBattlefield = false;
                        Error = "战场进场没回应：" + status;
                        Debug.Log("u12_battlefield=reject status=" + status);
                    }

                    if (done != null) done();
                });
        }
        // ------------------------------------------------------------------ 城战同屏

        /// <summary>
        /// 把战斗服下发的城战同屏快照翻译成展示规格。档位只有服务端给，
        /// 这里按 <see cref="SiegeViewTiers"/> 翻一次，不按距离或人数重排。
        /// </summary>
        public void ApplySiegeView(SiegeView view)
        {
            if (view == null)
            {
                return;
            }

            SiegeFullLimit = view.FullModelLimit;
            SiegeFullCount = view.FullModelCount;
            SiegeUnitCount = view.UnitCount;
            SiegeRows.Clear();

            var markers = 0;
            for (var i = 0; i < view.Units.Count; i++)
            {
                var unit = view.Units[i];
                var visual = SiegeViewTiers.For(unit);
                SiegeRows.Add(new SiegeRow
                {
                    MemberId = unit.MemberId,
                    Team = unit.Team,
                    Hp = unit.Hp,
                    HpMax = unit.HpMax,
                    Visual = visual,
                });

                if (visual.Kind == SiegeVisualKind.MarkerOnly)
                {
                    markers++;
                }
            }

            HasSiege = true;
            Debug.Log("u7_siege_view units=" + SiegeUnitCount
                + " full=" + SiegeFullCount + "/" + SiegeFullLimit
                + " markers=" + markers);
        }

        /// <summary>战斗进程收到城战同屏快照时调这里，由它交给屏。没有屏就只记一行。</summary>
        public static void Feed(SiegeView view)
        {
            if (view == null)
            {
                return;
            }

            var flow = FindObjectOfType<UiSocialFlow>();
            if (flow == null)
            {
                Debug.Log("u7_siege_view units=" + view.UnitCount + " (no_screen)");
                return;
            }

            flow.ApplySiegeView(view);
        }

        /// <summary>超限那些行：只有队伍颜色和血条，没有完整模型与名字牌。</summary>
        public List<SiegeRow> Markers()
        {
            var rows = new List<SiegeRow>();
            for (var i = 0; i < SiegeRows.Count; i++)
            {
                if (SiegeRows[i].Visual.Kind == SiegeVisualKind.MarkerOnly)
                {
                    rows.Add(SiegeRows[i]);
                }
            }

            return rows;
        }
    }
}
