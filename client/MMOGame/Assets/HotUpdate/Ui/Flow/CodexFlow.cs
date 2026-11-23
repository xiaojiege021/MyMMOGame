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
    /// 英灵图鉴只读入口（U11 / N12）。
    /// </summary>
    public sealed partial class UiSocialFlow : MonoBehaviour
    {
        // ------------------------------------------------------------------ 英灵图鉴（U11 / N12）

        /// <summary>
        /// 只读图鉴。走 `GET /hero-codex`，只带角色号；九位名字、收没收到、每位与合计的被动
        /// 万分比全来自角色服，屏上照抄不推算。非 200 不算一份图鉴回包。
        /// </summary>
        public void LoadHeroCodex(ulong ownerCharacterId = 0, Action done = null)
        {
            var owner = ownerCharacterId != 0 ? ownerCharacterId : _characterId;
            if (owner == 0)
            {
                LastCodex = HeroCodexOutcome.Failure("no_character");
                HasCodex = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            CodexTried = true;
            _send(HeroCodexEntry.RequestPath(owner), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastCodex = HeroCodexEntry.Parse(text);
                    HasCodex = true;
                    Error = string.Empty;
                    Debug.Log("u11_codex=ok entries=" + LastCodex.EntryCount
                        + " collected=" + LastCodex.CollectedCount
                        + " total_passive_pct_bps=" + LastCodex.TotalPassivePctBps
                        + " passive_kind=" + LastCodex.PassiveKind
                        + " main_stage=" + LastCodex.MainStage);
                }
                else
                {
                    LastCodex = HeroCodexOutcome.Failure("http_" + status);
                    HasCodex = false;
                    Error = "图鉴没读到：" + status;
                    Debug.Log("u11_codex=reject status=" + status);
                }

                if (done != null) done();
            });
        }
    }
}
