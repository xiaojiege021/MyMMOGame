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
    /// 宗门七条动作（建 / 加 / 升级 / 任免 / 传位 / 退出 / 名册）与世界 Boss 入口。
    /// </summary>
    public sealed partial class UiSocialFlow : MonoBehaviour
    {
        // ------------------------------------------------------------------ 宗门

        public void Create(string name, Action done = null)
        {
            Send("/sect/create", SectGuild.CreateBody(_characterId, name),
                SectGuild.ParseSect, outcome => { LastSect = outcome; HasSect = true; },
                "u7_sect_create", done);
        }

        public void Join(ulong sectId, Action done = null)
        {
            Send("/sect/join", SectGuild.JoinBody(_characterId, sectId),
                SectGuild.ParseSect, outcome => { LastSect = outcome; HasSect = true; },
                "u7_sect_join", done);
        }

        public void Upgrade(ulong sectId, Action done = null)
        {
            Send("/sect/upgrade", SectGuild.UpgradeBody(_characterId, sectId),
                SectGuild.ParseUpgrade, outcome => { LastUpgrade = outcome; HasUpgrade = true; },
                "u7_sect_upgrade", done);
        }

        public void Appoint(ulong sectId, ulong targetId, string position, Action done = null)
        {
            Send("/sect/appoint", SectGuild.PositionBody(_characterId, sectId, targetId, position),
                SectGuild.ParsePosition, outcome => { LastPosition = outcome; HasPosition = true; },
                "u7_sect_appoint", done);
        }

        public void Dismiss(ulong sectId, ulong targetId, string position, Action done = null)
        {
            Send("/sect/dismiss", SectGuild.PositionBody(_characterId, sectId, targetId, position),
                SectGuild.ParsePosition, outcome => { LastPosition = outcome; HasPosition = true; },
                "u7_sect_dismiss", done);
        }

        public void Transfer(ulong sectId, ulong targetId, Action done = null)
        {
            Send("/sect/transfer", SectGuild.TransferBody(_characterId, sectId, targetId),
                SectGuild.ParseLeadership, outcome => { LastLeadership = outcome; HasLeadership = true; },
                "u7_sect_transfer", done);
        }

        public void Leave(ulong sectId, Action done = null)
        {
            Send("/sect/leave", SectGuild.LeaveBody(_characterId, sectId),
                SectGuild.ParseLeadership, outcome => { LastLeadership = outcome; HasLeadership = true; },
                "u7_sect_leave", done);
        }

        /// <summary>
        /// 读名册。只带宗门编号，走 GET /sect/roster。不任免、不改人数上限、不写库。
        /// 非 200 不算一份名册回包。
        /// </summary>
        public void LoadRoster(ulong sectId, Action done = null)
        {
            if (sectId == 0)
            {
                return;
            }

            Busy = true;
            Requests++;
            _send("/sect/roster?sect_id=" + sectId.ToString(CultureInfo.InvariantCulture),
                string.Empty, _token, (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastRoster = SectGuild.ParseRoster(text);
                        HasRoster = true;
                        Error = string.Empty;
                        var rows = LastRoster.Members != null ? LastRoster.Members.Count : 0;
                        Debug.Log("n3_roster=ok rows=" + rows.ToString(CultureInfo.InvariantCulture)
                            + " reason=" + LastRoster.Reason);
                    }
                    else
                    {
                        HasRoster = false;
                        Error = "名册没读到：" + status.ToString(CultureInfo.InvariantCulture);
                        Debug.Log("n3_roster=reject status=" + status.ToString(CultureInfo.InvariantCulture));
                    }

                    if (done != null) done();
                });
        }

        /// <summary>读世界 Boss 入口的只读事实值。请求体为空，走 GET。</summary>
        public void LoadBoss(Action done = null)
        {
            Busy = true;
            Requests++;
            _send(WorldBossEntry.RequestPath, string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastBoss = WorldBossEntry.Parse(text);
                    HasBoss = true;
                    Debug.Log("u7_boss=ok visible=" + LastBoss.Visible
                        + " owner=" + (LastBoss.OwnerSectName.Length == 0 ? "none" : LastBoss.OwnerSectName));
                }
                else
                {
                    Error = "世界 Boss 入口没读到：" + status;
                    Debug.Log("u7_boss=reject status=" + status);
                }

                if (done != null) done();
            });
        }
    }
}
