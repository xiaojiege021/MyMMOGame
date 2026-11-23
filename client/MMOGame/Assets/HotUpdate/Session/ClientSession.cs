using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Mmo.Net;
using Mmo.Proto.Battle;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate
{
    /// <summary>
    /// 会话动作：换职业重连、清场、出手、撤前摇、位置上报、断开。
    /// </summary>
    public sealed partial class BattleClient : MonoBehaviour
    {
        /// <summary>
        /// 换职业：断掉当前连接，换个 class 参数重连一场。
        /// 职业的技能表、血蓝上限、出生点全部以新一场的 Opened 为准。
        /// </summary>
        public async void SwitchClass(string classId)
        {
            if (classId == ClassId)
            {
                Debug.Log("p03c_switch_skip reason=same_class class=" + classId);
                return;
            }

            ClassId = classId;
            Debug.Log("p03c_switch class=" + classId);

            if (_link != null)
            {
                try
                {
                    await _link.CloseAsync();
                }
                catch (Exception e)
                {
                    Debug.Log("p03c_close_fail " + e.GetType().Name + " " + e.Message);
                }

                _link.Dispose();
                _link = null;
            }

            ResetState();
            await ConnectAsync();
        }

        /// <summary>换职业前的清场。所有战斗状态都等新一场的 Opened 重发。</summary>
        void ResetState()
        {
            Connected = false;
            Failure = "";
            Ended = false;
            EndedSeq = 0;
            Casting = false;
            CastingSkill = 0;
            CastElapsedMs = 0;
            BossId = 0;
            BossHp = 0;
            BossHpMax = 0;
            CasterHp = 0;
            CasterHpMax = 0;
            CasterId = 0;
            ClassName = "";
            Mana = 0;
            ManaMax = 0;
            PhaseIndex = 0;
            PhaseName = "";
            LastDamage = 0;
            LastReject = "";
            EffectSeq = 0;
            _effects.Clear();
            Skills.Clear();
            Party.Clear();
            Classes.Clear();
        }
        /// <summary>按编号出手。返回 false 表示这次根本没发出去，界面不该播前摇。</summary>
        public bool Cast(uint skillId)
        {
            if (!Connected || Ended)
            {
                Debug.Log("p02b_cast_skip skill=" + skillId + " reason=not_connected");
                return false;
            }

            if (Casting)
            {
                Debug.Log("p02b_cast_skip skill=" + skillId + " reason=already_casting");
                return false;
            }

            if (TargetSelector.Current == 0)
            {
                Debug.Log("p02b_cast_skip skill=" + skillId + " reason=no_target");
                return false;
            }

            var info = FindSkill(skillId);
            if (info == null)
            {
                Debug.Log("p02b_cast_skip skill=" + skillId + " reason=unknown_skill");
                return false;
            }

            var request = TargetSelector.BuildRequest(skillId);
            _castTimeMs = request.CastTimeMs;
            Fire(_link.SendSkillAsync(request.SkillId, request.TargetId, request.CastTimeMs), "skill");

            Casting = true;
            CastingSkill = skillId;
            _castStartMs = NowMs();
            CastElapsedMs = 0;
            ForeswingMs = (int)info.ForeswingMs;
            LastReject = "";

            Debug.Log("p02b_cast_start skill=" + skillId
                + " target=" + request.TargetId
                + " foreswing=" + ForeswingMs
                + " distance=" + DistanceMm);
            return true;
        }

        /// <summary>前摇里撤掉。撤不撤得回来由服务端说了算，这里只发请求。</summary>
        public void CancelCast()
        {
            if (!Casting)
            {
                Debug.Log("p02b_cancel_skip reason=not_casting");
                return;
            }

            Fire(_link.SendCancelAsync(CastingSkill, _castTimeMs), "cancel");
        }

        /// <summary>本地先把人摆到想去的位置，再限频上报。服务端夹过之后会回校准值。</summary>
        public void ReportMove(float xMm, float yMm)
        {
            XMm = xMm;
            YMm = yMm;
        }

        void ReportMoveIfNeeded()
        {
            if (!Connected || _link == null)
            {
                return;
            }

            var now = NowMs();
            if (now - _lastMoveSentMs < 100)
            {
                return;
            }

            if (Mathf.Abs(XMm - _lastSentX) < 1 && Mathf.Abs(YMm - _lastSentY) < 1)
            {
                return;
            }

            _lastMoveSentMs = now;
            _lastSentX = XMm;
            _lastSentY = YMm;

            Fire(_link.SendMoveAsync((int)XMm, (int)YMm, (long)now), "move");
        }
        async void OnDestroy()
        {
            if (_link == null)
            {
                return;
            }

            try
            {
                await _link.CloseAsync();
            }
            catch (Exception e)
            {
                Debug.Log("p02b_close_fail " + e.GetType().Name + " " + e.Message);
            }

            _link.Dispose();
            _link = null;
            Connected = false;
        }
    }
}
