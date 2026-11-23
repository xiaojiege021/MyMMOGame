using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using HotUpdate.Ui;
using Mmo.Proto.Battle;
using Debug = UnityEngine.Debug;

namespace HotUpdate
{
    /// <summary>
    /// 战斗界面：血条、技能键、撤键、前摇进度、伤害飘字、拒绝提示。
    ///
    /// 按钮的判定用屏幕像素矩形，和画出来的位置是同一套坐标，
    /// 不挂 EventSystem——真机上少一层输入模块的配置，核对也好按坐标点。
    /// 这里只显示服务端回来的数，不自己算血，也不自己判命中。
    /// </summary>
    public sealed partial class BattleHud : MonoBehaviour
    {
        const int SkillButtonCount = 3;
        const int SkillButtonSize = 200;
        const int SkillButtonGap = 24;
        const int SkillButtonRight = 60;
        const int SkillButtonBottom = 60;
        const int CancelButtonHeight = 90;
        const int ManaBarWidth = 500;

        /// <summary>小队最多显示四行。超过四个人的场次只画前四个，不缩字号。</summary>
        const int PartySlots = 4;

        /// <summary>Q2：自己的 buff 条最多六格；没有效果就整条不显示。</summary>
        const int SelfEffectSlots = 6;

        /// <summary>Q2：Boss 血条附近的 debuff / dot 小条最多四格。</summary>
        const int BossEffectSlots = 4;

        const float SelfEffectSlotWidth = 150f;
        const float SelfEffectSlotHeight = 50f;
        const float SelfEffectGap = 8f;
        const float SelfEffectY = 292f;

        BattleClient _client;

        Font _font;

        RectTransform _hpFill;

        Text _hpText;

        RectTransform _manaFill;

        Text _manaText;

        RectTransform _castBar;

        CanvasGroup _castGroup;

        CanvasGroup _cancelGroup;

        readonly Image[] _skillImages = new Image[SkillButtonCount];

        readonly Text[] _skillLabels = new Text[SkillButtonCount];

        Text _classText;

        Text _classButtonLabel;

        Image _classButtonImage;

        Text _damageText;

        Text _rejectText;

        /// <summary>C01 顶部 Boss 条那一行：阶段序号与名字，只认服务端广播。</summary>
        Text _phaseText;

        /// <summary>C01 右上小队四行：名字 · 职业 · 血量百分比，全部来自同场名单回包。</summary>
        readonly Text[] _partyLabels = new Text[PartySlots];

        /// <summary>小队血条填充。宽度就是回包里的 hp / hpMax，本地不补也不预测。</summary>
        readonly RectTransform[] _partyFills = new RectTransform[PartySlots];

        /// <summary>C01 左下战斗日志。只记回包发生过什么，不写本地推断。</summary>
        Text _logText;

        string _logLine = UiBattleCodex.HudNotes[0];

        /// <summary>Q2：自己身上那条 buff 条的背景与文本，每格一个。</summary>
        readonly Image[] _selfEffectBacks = new Image[SelfEffectSlots];

        readonly Text[] _selfEffectLabels = new Text[SelfEffectSlots];

        /// <summary>Q2：Boss 血条附近的 debuff / dot 小条，只画名字和剩余秒数。</summary>
        readonly Image[] _bossEffectBacks = new Image[BossEffectSlots];

        readonly Text[] _bossEffectLabels = new Text[BossEffectSlots];

        /// <summary>
        /// 上一份自己的效果集合，按 effect_id 做差，只在自身刷战斗日志。
        /// 热更侧不用 Dictionary：AOT 侧没有精确实例化，真机一调就 MissingMethod（M2 抓到），
        /// 所以这里是单层 List + 线性查（同场效果个位数）。
        /// </summary>
        readonly List<EffectSnapshot> _selfEffectSeen = new();

        int _seenEffectSeq;

        Text _distanceText;

        Text _tipText;

        float _damageAge = 99f;

        float _rejectAge = 99f;

        int _seenDamage;

        int _seenReject;

        int _seenCancel;

        int _seenEnded;

        int _seenPhase;

        bool _wasPressed;

        /// <summary>这个屏幕点是不是落在界面的按钮上。点选那边要跳过这块。</summary>
        public static bool HitTest(Vector2 screenPoint)
        {
            if (ClassButtonRect().Contains(screenPoint))
            {
                return true;
            }

            for (var i = 0; i < SkillButtonCount; i++)
            {
                if (SkillButtonRect(i).Contains(screenPoint))
                {
                    return true;
                }
            }

            return CancelButtonRect().Contains(screenPoint);
        }

        static Rect SkillButtonRect(int index)
        {
            var x = Screen.width - SkillButtonRight - SkillButtonSize
                - (SkillButtonCount - 1 - index) * (SkillButtonSize + SkillButtonGap);
            return new Rect(x, SkillButtonBottom, SkillButtonSize, SkillButtonSize);
        }

        static Rect CancelButtonRect()
        {
            var skill = SkillButtonRect(SkillButtonCount - 1);
            return new Rect(skill.x, skill.yMax + SkillButtonGap, SkillButtonSize, CancelButtonHeight);
        }

        /// <summary>换职业按钮：左上角，职业文本下面。坐标进日志，真机核对照它点。</summary>
        static Rect ClassButtonRect()
        {
            return new Rect(40f, Screen.height - 170f, 260f, 84f);
        }

        public static BattleHud Create(BattleClient client)
        {
            var go = new GameObject("BattleHud", typeof(Canvas), typeof(CanvasScaler), typeof(BattleHud));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;

            // 像素一一对应，UI 矩形和点击矩形才是同一套坐标。
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            var hud = go.GetComponent<BattleHud>();
            hud._client = client;
            hud.Build(go.transform);
            return hud;
        }
        void Update()
        {
            if (_client == null)
            {
                return;
            }

            ReadPress();
            Refresh();
        }
        void ReadPress()
        {
            var pointer = Pointer.current;
            if (pointer == null)
            {
                _wasPressed = false;
                return;
            }

            var pressed = pointer.press.isPressed;
            if (!pressed)
            {
                _wasPressed = false;
                return;
            }

            if (_wasPressed)
            {
                return;
            }

            _wasPressed = true;

            // 上一条手指还没抬起来时不要重复出手，交给 _wasPressed 的边沿。
            var point = pointer.position.ReadValue();

            if (ClassButtonRect().Contains(point))
            {
                if (_client.Classes.Count > 0)
                {
                    var current = _client.Classes.FindIndex(c => c.ClassId == BattleClient.ClassId);
                    var next = _client.Classes[(current + 1) % _client.Classes.Count];
                    _client.SwitchClass(next.ClassId);
                }
                else
                {
                    Debug.Log("p03c_switch_skip reason=no_classes");
                }

                return;
            }

            if (CancelButtonRect().Contains(point))
            {
                _client.CancelCast();
                return;
            }

            for (var i = 0; i < SkillButtonCount; i++)
            {
                if (!SkillButtonRect(i).Contains(point))
                {
                    continue;
                }

                var info = i < _client.Skills.Count ? _client.Skills[i] : null;
                if (info != null)
                {
                    _client.Cast(info.SkillId);
                }

                return;
            }
        }
    }
}
