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
    /// 热更侧的战斗会话：连战斗进程、把回包摆给人看、按服务端下发的表播前摇。
    /// 命中、伤害、冷却一律不在这里算——这里只把回包里的数拿出来用。
    ///
    /// 收包在后台线程，落进 BattleLink 的队列；这里每帧在主线程取，
    /// 不直接碰 Unity 对象，省掉一类只在真机上才出现的崩溃。
    /// </summary>
    public sealed class BattleClient : MonoBehaviour
    {
        public static BattleClient Instance { get; private set; }

        /// <summary>真机上用 adb reverse 把这个端口映射到本机的战斗进程。</summary>
        public static string Url = "ws://127.0.0.1:60001/battle";

        /// <summary>
        /// 当前职业。作为 query 参数跟在连接地址后面，服务端按它发技能表。
        /// 换职业就是换这个值再重连一场，客户端不自己算职业数值。
        /// </summary>
        public static string ClassId = "warrior";

        public bool Connected { get; private set; }

        public string Failure { get; private set; } = "";

        public bool Ended { get; private set; }

        public ulong BossId { get; private set; }

        public long BossHp { get; private set; }

        public long BossHpMax { get; private set; }

        public long CasterHp { get; private set; }

        public long CasterHpMax { get; private set; }

        /// <summary>自己的成员编号。MemberUpdated 靠它认出哪条是自己。</summary>
        public ulong CasterId { get; private set; }

        public string ClassName { get; private set; } = "";

        public long Mana { get; private set; }

        public long ManaMax { get; private set; }

        /// <summary>当前 Boss 阶段。切阶段只认服务端广播，不自己看血线。</summary>
        public int PhaseIndex { get; private set; }

        public string PhaseName { get; private set; } = "";

        /// <summary>阶段变化序号。界面靠它判断「来了一条新的」。</summary>
        public int PhaseSeq { get; private set; }

        public uint DistanceMm { get; private set; }

        public uint ArenaRadiusMm { get; private set; }

        public uint MoveSpeedMmPerS { get; private set; }

        /// <summary>本地推算的位置，毫米，Boss 为原点。服务端回包会把它校准。</summary>
        public float XMm { get; private set; }

        public float YMm { get; private set; }

        public bool Casting { get; private set; }

        public uint CastingSkill { get; private set; }

        /// <summary>这次前摇已经等了多久。前摇长度以服务端下发的那份为准。</summary>
        public double CastElapsedMs { get; private set; }

        public int ForeswingMs { get; private set; }

        public long LastDamage { get; private set; }

        public string LastReject { get; private set; } = "";

        /// <summary>回包序号。界面靠它判断「来了一条新的」，不用订事件。</summary>
        public int DamageSeq { get; private set; }

        public int RejectSeq { get; private set; }

        public int CancelSeq { get; private set; }

        public int EndedSeq { get; private set; }

        public readonly List<SkillInfo> Skills = new();

        /// <summary>同一场里的人，含自己。界面照这个显示队友血条。</summary>
        public readonly List<PartyMember> Party = new();

        /// <summary>服务端下发的可选职业目录。换职业按钮按这个循环。</summary>
        public readonly List<ClassInfo> Classes = new();

        BattleLink _link;

        double _castStartMs;

        long _castTimeMs;

        double _lastMoveSentMs;

        float _lastSentX = float.NaN;

        float _lastSentY = float.NaN;

        float _lastLoggedDistance = -1;

        public SkillInfo FindSkill(uint id) => Skills.Find(s => s.SkillId == id);

        /// <summary>单调时钟毫秒。和服务端一样，不受系统时间跳变影响。</summary>
        static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        async void Start()
        {
            Instance = this;
            await ConnectAsync();
        }

        /// <summary>连接地址带上职业参数。服务端认不出职业会回 400，这里不猜、让失败浮上来。</summary>
        static string BuildUrl()
        {
            return Url + (Url.Contains('?') ? "&" : "?") + "class=" + ClassId;
        }

        async Task ConnectAsync()
        {
            try
            {
                _link = await BattleLink.ConnectAsync(BuildUrl(), TimeSpan.FromSeconds(3));
                Connected = true;
                Debug.Log("p02b_connect=ok url=" + BuildUrl());
            }
            catch (Exception e)
            {
                Failure = e.Message;
                Debug.Log("p02b_connect=fail " + e.GetType().Name + " " + e.Message);
            }
        }

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
            Skills.Clear();
            Party.Clear();
            Classes.Clear();
        }

        void Update()
        {
            if (_link != null)
            {
                while (_link.TryReceive(out var message))
                {
                    Handle(message);
                }

                if (Failure.Length == 0 && !string.IsNullOrEmpty(_link.Failure))
                {
                    Failure = _link.Failure;
                    Debug.Log("p02b_recv_fail " + Failure);
                }
            }

            if (Casting)
            {
                CastElapsedMs = NowMs() - _castStartMs;

                // 回包一直不来就把这次收掉，界面不能永远卡在前摇上。
                if (CastElapsedMs > ForeswingMs + 2000)
                {
                    FinishCast("timeout", LastReject);
                }
            }

            ReportMoveIfNeeded();
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
                    if (Mathf.Abs(moved.XMm - XMm) > 500 || Mathf.Abs(moved.YMm - YMm) > 500)
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
            }
        }

        void FinishCast(string outcome, string detail)
        {
            if (!Casting)
            {
                return;
            }

            Casting = false;
            CastElapsedMs = 0;
            LastReject = detail;
        }

        async void Fire(Task task, string what)
        {
            try
            {
                await task;
            }
            catch (Exception e)
            {
                Debug.Log("p02b_send_fail what=" + what + " " + e.GetType().Name + " " + e.Message);
            }
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
