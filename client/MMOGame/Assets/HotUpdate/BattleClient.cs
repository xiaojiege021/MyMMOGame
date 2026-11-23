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
    public sealed partial class BattleClient : MonoBehaviour
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

        /// <summary>
        /// Q2 效果快照序号。每收到一整份效果集合就 +1，界面靠它判断要不要刷日志。
        /// 只做计数，不按效果编号自己算到期或层数。
        /// </summary>
        public int EffectSeq { get; private set; }

        /// <summary>
        /// 一个单位最近一份效果快照。热更侧不写字典泛型（AOT 侧没有精确实例化，
        /// 真机一调就 MissingMethod，M2 抓到的那条），所以改成单层 List + 小类，
        /// 查找走线性（同场单位个位数）。
        /// </summary>
        sealed class EffectSlot
        {
            public ulong UnitId;
            public double StampMs;
            public readonly List<EffectSnapshot> Effects = new();
        }

        /// <summary>每个单位（含 Boss）最近一次效果快照。收到就整份替换，不做增量。</summary>
        readonly List<EffectSlot> _effects = new();

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

        /// <summary>
        /// 某个单位最近一份效果快照，只读。没有就是空表，界面直接当「没有效果」画。
        /// 服务端每次整份重发，这里也整份替换，不在本地做增量或补算。
        /// </summary>
        public IReadOnlyList<EffectSnapshot> EffectsOf(ulong unitId)
        {
            var slot = FindEffectSlot(unitId);
            return slot != null ? (IReadOnlyList<EffectSnapshot>)slot.Effects : Array.Empty<EffectSnapshot>();
        }

        /// <summary>找某个单位的快照槽，没有就是 null。</summary>
        EffectSlot FindEffectSlot(ulong unitId)
        {
            for (var i = 0; i < _effects.Count; i++)
            {
                if (_effects[i].UnitId == unitId)
                {
                    return _effects[i];
                }
            }
            return null;
        }

        /// <summary>自己身上的效果。CasterId 还没下发时是空表。</summary>
        public IReadOnlyList<EffectSnapshot> SelfEffects => EffectsOf(CasterId);

        /// <summary>Boss 身上的效果。</summary>
        public IReadOnlyList<EffectSnapshot> BossEffects => EffectsOf(BossId);

        /// <summary>
        /// 这份快照已经过了多少本地毫秒。界面用它从 remaining_ms 往下倒计时；
        /// 到 0 就停在 0，真正的到期与层数仍只认服务端的下一条快照。
        /// </summary>
        public double EffectAgeMs(ulong unitId)
        {
            var slot = FindEffectSlot(unitId);
            return slot != null ? NowMs() - slot.StampMs : 0;
        }

        /// <summary>整份替换某个单位的效果列表，并把「有新快照」的序号推一格。</summary>
        void ApplyEffectState(ulong unitId, IEnumerable<EffectSnapshot> effects)
        {
            var slot = FindEffectSlot(unitId);
            if (slot == null)
            {
                slot = new EffectSlot { UnitId = unitId };
                _effects.Add(slot);
            }
            slot.Effects.Clear();
            slot.Effects.AddRange(effects);
            slot.StampMs = NowMs();
            EffectSeq++;
        }

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
    }
}
