using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        public bool Connected { get; private set; }

        public string Failure { get; private set; } = "";

        public bool Ended { get; private set; }

        public ulong BossId { get; private set; }

        public long BossHp { get; private set; }

        public long BossHpMax { get; private set; }

        public long CasterHp { get; private set; }

        public long CasterHpMax { get; private set; }

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

            try
            {
                _link = await BattleLink.ConnectAsync(Url, TimeSpan.FromSeconds(3));
                Connected = true;
                Debug.Log("p02b_connect=ok url=" + Url);
            }
            catch (Exception e)
            {
                Failure = e.Message;
                Debug.Log("p02b_connect=fail " + e.GetType().Name + " " + e.Message);
            }
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
                    CasterHp = opened.CasterHp;
                    CasterHpMax = opened.CasterHpMax;
                    DistanceMm = opened.DistanceMm;
                    ArenaRadiusMm = opened.ArenaRadiusMm;
                    MoveSpeedMmPerS = opened.MoveSpeedMmPerS;

                    // 出生点由服务端给。不设的话本地默认 (0,0)，一上报就把自己
                    // 报成站在 Boss 脚下，服务端还得按速度拉回去。
                    XMm = opened.CasterXMm;
                    YMm = opened.CasterYMm;
                    _lastSentX = float.NaN;
                    _lastSentY = float.NaN;

                    Skills.Clear();
                    Skills.AddRange(opened.Skills);

                    Debug.Log("p02b_opened session=" + opened.SessionId
                        + " boss=" + opened.BossId
                        + " boss_hp=" + opened.BossHp + "/" + opened.BossHpMax
                        + " caster_hp=" + opened.CasterHp + "/" + opened.CasterHpMax
                        + " distance=" + opened.DistanceMm
                        + " skills=" + opened.Skills.Count
                        + " arena=" + opened.ArenaRadiusMm
                        + " speed=" + opened.MoveSpeedMmPerS);
                    break;
                }

                case ServerMessage.BodyOneofCase.Result:
                {
                    var result = message.Result;
                    BossHp = result.TargetHpAfter;
                    LastDamage = result.Damage;
                    DamageSeq++;
                    FinishCast("hit", "");
                    Debug.Log("p02b_cast_end skill=" + result.SkillId
                        + " result=hit"
                        + " damage=" + result.Damage
                        + " hp_after=" + result.TargetHpAfter
                        + " stagger=" + result.StaggerMs);
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
