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
    /// 战斗服回包的分派：Opened / Result / Rejected / Cancelled / Moved / Ended …
    /// </summary>
    public sealed partial class BattleClient : MonoBehaviour
    {
        void Handle(ServerMessage message)
        {
            switch (message.BodyCase)
            {
                case ServerMessage.BodyOneofCase.Opened:
                {
                    var opened = message.Opened;
                    BossId = opened.BossId;
                    BossHp = opened.BossHp;
                    BossHpMax = opened.BossHpMax;
                    CasterId = opened.CasterId;
                    CasterHp = opened.CasterHp;
                    CasterHpMax = opened.CasterHpMax;
                    ClassName = opened.ClassName;
                    Mana = opened.Mana;
                    ManaMax = opened.ManaMax;
                    DistanceMm = opened.DistanceMm;
                    ArenaRadiusMm = opened.ArenaRadiusMm;
                    MoveSpeedMmPerS = opened.MoveSpeedMmPerS;

                    // 新一场从零阶段开始。有没有阶段、几时切，全听服务端广播。
                    PhaseIndex = 0;
                    PhaseName = "";
                    PhaseSeq = 0;

                    // Q2：新一场的效果集合也在开场包里。先清掉上一场的，
                    // 再按每一条 EffectState 整份收进来（正常开场是空的，Q9 重连才有）。
                    _effects.Clear();
                    foreach (var state in opened.Effects)
                    {
                        ApplyEffectState(state.UnitId, state.Effects);
                    }

                    Debug.Log("q2_opened_effects units=" + opened.Effects.Count
                        + " self=" + SelfEffects.Count
                        + " boss=" + BossEffects.Count
                        + " seq=" + EffectSeq);

                    // 出生点由服务端给。不设的话本地默认 (0,0)，一上报就把自己
                    // 报成站在 Boss 脚下，服务端还得按速度拉回去。
                    XMm = opened.CasterXMm;
                    YMm = opened.CasterYMm;
                    _lastSentX = float.NaN;
                    _lastSentY = float.NaN;

                    Skills.Clear();
                    Skills.AddRange(opened.Skills);
                    Party.Clear();
                    Party.AddRange(opened.Party);
                    Classes.Clear();
                    Classes.AddRange(opened.Classes);

                    Debug.Log("p02b_opened session=" + opened.SessionId
                        + " boss=" + opened.BossId
                        + " boss_hp=" + opened.BossHp + "/" + opened.BossHpMax
                        + " caster_hp=" + opened.CasterHp + "/" + opened.CasterHpMax
                        + " distance=" + opened.DistanceMm
                        + " skills=" + opened.Skills.Count
                        + " arena=" + opened.ArenaRadiusMm
                        + " speed=" + opened.MoveSpeedMmPerS);

                    Debug.Log("p03c_opened class=" + opened.ClassId
                        + " class_name=" + opened.ClassName
                        + " mana=" + opened.Mana + "/" + opened.ManaMax
                        + " caster_hp=" + opened.CasterHp + "/" + opened.CasterHpMax
                        + " party=" + opened.Party.Count
                        + " classes=" + opened.Classes.Count
                        + " skills="
                        + string.Join(",", opened.Skills.Select(s => s.SkillId + ":" + s.Name).ToArray()));
                    break;
                }

                case ServerMessage.BodyOneofCase.Result:
                {
                    var result = message.Result;

                    // 打 Boss 的结算才动 Boss 血条；治疗打在队友身上，血量走 MemberUpdated。
                    if (result.TargetId == BossId)
                    {
                        BossHp = result.TargetHpAfter;
                    }

                    // 蓝和仇恨都是施法者自己的账，别人的结算只拿来看。
                    if (result.CasterId == CasterId)
                    {
                        Mana = result.ManaAfter;
                    }

                    LastDamage = result.Damage;
                    DamageSeq++;
                    FinishCast("hit", "");
                    Debug.Log("p02b_cast_end skill=" + result.SkillId
                        + " result=hit"
                        + " damage=" + result.Damage
                        + " hp_after=" + result.TargetHpAfter
                        + " stagger=" + result.StaggerMs);
                    Debug.Log("p03c_cast skill=" + result.SkillId
                        + " caster=" + result.CasterId
                        + " damage=" + result.Damage
                        + " healing=" + result.Healing
                        + " mana=" + result.ManaAfter
                        + " threat=" + result.ThreatAfter);
                    break;
                }

                case ServerMessage.BodyOneofCase.Rejected:
                {
                    var rejected = message.Rejected;
                    LastReject = rejected.Reason.ToString();
                    RejectSeq++;
                    FinishCast("rejected", LastReject);
                    Debug.Log("p02b_cast_end skill=" + rejected.SkillId
                        + " result=rejected"
                        + " reason=" + LastReject
                        + " retry_after=" + rejected.RetryAfterMs);

                    // Q2：眩晕 / 沉默走的是同一套拒绝码，界面按 ToString() 原样显示。
                    // 这里只多留一行，方便在 logcat 里分清是哪种控制挡的。
                    if (rejected.Reason == SkillRejectReason.SkillRejectStunned
                        || rejected.Reason == SkillRejectReason.SkillRejectSilenced)
                    {
                        Debug.Log("q2_reject_control skill=" + rejected.SkillId
                            + " reason=" + LastReject);
                    }

                    break;
                }

                case ServerMessage.BodyOneofCase.Cancelled:
                {
                    var cancelled = message.Cancelled;
                    CancelSeq++;
                    if (cancelled.Accepted)
                    {
                        FinishCast("cancelled", "");
                    }

                    Debug.Log("p02b_cancel skill=" + cancelled.SkillId + " accepted=" + cancelled.Accepted);
                    break;
                }

                case ServerMessage.BodyOneofCase.Moved:
                {
                    var moved = message.Moved;
                    DistanceMm = moved.DistanceMm;

                    // 偏差小就不动，免得每 100 毫秒把画面抖一下；差得多说明被夹了，吸附过去。
                    if (moved.Blocked)
                    {
                        // Q2：被定身 / 眩晕挡住时，服务端给的是权威原地。
                        // 这一步没生效就不能再按 500mm 阈值赌，位置无条件贴回去。
                        XMm = moved.XMm;
                        YMm = moved.YMm;
                        _lastLoggedDistance = moved.DistanceMm;
                        Debug.Log("q2_move_blocked x=" + moved.XMm
                            + " y=" + moved.YMm
                            + " distance=" + moved.DistanceMm
                            + " clamped=" + moved.Clamped);
                    }
                    else if (Mathf.Abs(moved.XMm - XMm) > 500 || Mathf.Abs(moved.YMm - YMm) > 500)
                    {
                        XMm = moved.XMm;
                        YMm = moved.YMm;
                    }

                    // 每 100 毫秒一条会把 logcat 刷满，只在距离跳得明显时留一行。
                    if (Mathf.Abs(moved.DistanceMm - _lastLoggedDistance) >= 250 || moved.Clamped)
                    {
                        _lastLoggedDistance = moved.DistanceMm;
                        Debug.Log("p02b_move x=" + moved.XMm
                            + " y=" + moved.YMm
                            + " distance=" + moved.DistanceMm
                            + " clamped=" + moved.Clamped);
                    }

                    break;
                }

                case ServerMessage.BodyOneofCase.Ended:
                {
                    Ended = true;
                    EndedSeq++;
                    BossHp = 0;
                    Debug.Log("p02b_ended cleared=" + message.Ended.Cleared
                        + " settlement=" + message.Ended.SettlementId
                        + " exp=" + message.Ended.ExpGain);
                    break;
                }

                case ServerMessage.BodyOneofCase.MemberUpdated:
                {
                    // 同场所有人的血量变化。自己被打、被奶、被拉起来都从这走。
                    var member = message.MemberUpdated.Member;
                    var index = Party.FindIndex(p => p.MemberId == member.MemberId);
                    if (index >= 0)
                    {
                        Party[index] = member;
                    }
                    else
                    {
                        Party.Add(member);
                    }

                    if (member.MemberId == CasterId)
                    {
                        CasterHp = member.Hp;
                        CasterHpMax = member.HpMax;
                    }

                    Debug.Log("p03c_member id=" + member.MemberId
                        + " class=" + member.ClassId
                        + " hp=" + member.Hp + "/" + member.HpMax
                        + " down=" + member.Down);
                    break;
                }

                case ServerMessage.BodyOneofCase.BossPhase:
                {
                    var phase = message.BossPhase;
                    PhaseIndex = phase.PhaseIndex;
                    PhaseName = phase.Name;
                    PhaseSeq++;
                    Debug.Log("p03c_phase index=" + phase.PhaseIndex
                        + " name=" + phase.Name
                        + " boss_hp=" + BossHp + "/" + BossHpMax);
                    break;
                }

                // Q2：某个单位身上的效果集合变了。服务端整份重发，这里整份替换；
                // 施加 / 到期 / 驱散 / 清空都走这一条，不做增量，也不本地补算。
                case ServerMessage.BodyOneofCase.Effects:
                {
                    var state = message.Effects;
                    ApplyEffectState(state.UnitId, state.Effects);

                    Debug.Log("q2_effects unit=" + state.UnitId
                        + " count=" + state.Effects.Count
                        + " seq=" + EffectSeq
                        + " names=" + string.Join(",",
                            state.Effects.Select(e => e.EffectId + ":" + e.Stacks).ToArray()));
                    break;
                }

                // Q2：Boss 血量和效果一起回。DoT 跳、debuff 到期这类没有施法的变化走这条，
                // 普攻 / 技能命中仍走 SkillResult。
                case ServerMessage.BodyOneofCase.BossState:
                {
                    var boss = message.BossState;
                    BossHp = boss.Hp;
                    BossHpMax = boss.HpMax;

                    // 正常 boss_id 与开场一致；万一是 0 就退回开场那个编号，不往 0 号身上挂。
                    var unitId = boss.BossId != 0 ? boss.BossId : BossId;
                    ApplyEffectState(unitId, boss.Effects);

                    Debug.Log("q2_boss_state boss=" + unitId
                        + " hp=" + boss.Hp + "/" + boss.HpMax
                        + " effects=" + boss.Effects.Count
                        + " seq=" + EffectSeq);
                    break;
                }

                // 城战同屏档位（P3-3a2 的 SiegeView）。只有城战实例发这条消息，
                // 副本 / 竞技场 / 战场都不发；收到就交给社交屏，没开屏只记一行。
                // 这里不按距离或人数自己重排——画全几个由服务端按上限算。
                case ServerMessage.BodyOneofCase.SiegeView:
                {
                    Ui.UiSocialFlow.Feed(message.SiegeView);
                    break;
                }

                // 掉落归属（Q8 的 LootAssigned）：personal 模式不发这条，need_greed 才发。
                // 归属与点数都在战斗服，这里只把回包交给掷点屏，不补算、不改归属。
                case ServerMessage.BodyOneofCase.LootAssigned:
                {
                    Ui.UiLootFlow.Feed(message.LootAssigned);
                    break;
                }
            }
        }
    }
}
