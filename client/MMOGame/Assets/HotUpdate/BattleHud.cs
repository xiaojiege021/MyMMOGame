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
    public sealed class BattleHud : MonoBehaviour
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

        /// <summary>上一份自己的效果集合，按 effect_id 做差，只在自身刷战斗日志。</summary>
        readonly Dictionary<string, EffectSnapshot> _selfEffectSeen = new();

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

        void Build(Transform root)
        {
            _font = LoadFont();

            var width = Screen.width;

            // Boss 血条：顶部居中，红条。
            var barWidth = width * 0.6f;
            var barX = (width - barWidth) / 2f;
            var barY = Screen.height - 90f;
            MakeRect("HpBack", new Rect(barX, barY, barWidth, 36f), new Color(0f, 0f, 0f, 0.55f), root);
            _hpFill = MakeRect("HpFill", new Rect(barX, barY, barWidth, 36f), new Color(0.78f, 0.16f, 0.16f, 0.95f), root);

            _hpText = MakeText("HpText", new Rect(barX, barY + 40f, barWidth, 40f), 26, TextAnchor.MiddleCenter, root);

            _distanceText = MakeText("DistanceText", new Rect(width - 460f, Screen.height - 150f, 400f, 44f), 26,
                TextAnchor.MiddleRight, root);

            // 左上角：当前职业和 Boss 阶段。换职业后、切阶段后都立刻变。
            _classText = MakeText("ClassText", new Rect(40f, Screen.height - 68f, 600f, 44f), 30,
                TextAnchor.MiddleLeft, root);
            _classText.text = "";

            // 换职业按钮：点了按服务端下发的职业目录循环，重连一场。
            var classRect = ClassButtonRect();
            _classButtonImage = MakeRect("ClassButton", classRect, new Color(0.32f, 0.22f, 0.55f, 0.85f), root)
                .GetComponent<Image>();
            _classButtonLabel = MakeText("ClassButtonLabel", classRect, 30, TextAnchor.MiddleCenter, root);
            _classButtonLabel.text = "换职业";

            // 玩家血量：左下角。蓝条在它上面，颜色分开，一眼分得清。
            _tipText = MakeText("TipText", new Rect(40f, 40f, 700f, 48f), 26, TextAnchor.MiddleLeft, root);

            MakeRect("ManaBack", new Rect(40f, 98f, ManaBarWidth, 24f), new Color(0f, 0f, 0f, 0.55f), root);
            _manaFill = MakeRect("ManaFill", new Rect(40f, 98f, ManaBarWidth, 24f), new Color(0.2f, 0.45f, 0.95f, 0.95f), root);
            _manaText = MakeText("ManaText", new Rect(40f + ManaBarWidth + 16f, 98f, 360f, 24f), 24,
                TextAnchor.MiddleLeft, root);

            // 前摇进度：屏幕下方正中，填充随等待时间走。整层挂一起淡入淡出。
            var castWidth = width * 0.4f;
            var castX = (width - castWidth) / 2f;
            var castY = Screen.height * 0.22f;
            var castLayer = NewLayer("CastLayer", root);
            _castGroup = castLayer.gameObject.AddComponent<CanvasGroup>();
            _castGroup.alpha = 0f;
            MakeRect("CastBack", new Rect(castX, castY, castWidth, 20f), new Color(0f, 0f, 0f, 0.5f), castLayer);
            _castBar = MakeRect("CastFill", new Rect(castX, castY, 0f, 20f), new Color(0.95f, 0.78f, 0.2f, 0.95f), castLayer);

            // 技能键。字用服务端下发的技能名，五个职业在手机上才分得清。
            for (var i = 0; i < SkillButtonCount; i++)
            {
                var rect = SkillButtonRect(i);
                var image = MakeRect("Skill" + (i + 1), rect, new Color(0.16f, 0.36f, 0.62f, 0.85f), root)
                    .GetComponent<Image>();
                _skillImages[i] = image;
                _skillLabels[i] = MakeText("SkillLabel" + (i + 1), rect, 34, TextAnchor.MiddleCenter, root);
                _skillLabels[i].text = "S" + (i + 1);
            }

            // 撤键：只在有前摇时出现。
            var cancelRect = CancelButtonRect();
            var cancelLayer = NewLayer("CancelLayer", root);
            _cancelGroup = cancelLayer.gameObject.AddComponent<CanvasGroup>();
            _cancelGroup.alpha = 0f;
            MakeRect("Cancel", cancelRect, new Color(0.55f, 0.2f, 0.2f, 0.9f), cancelLayer);
            var cancelLabel = MakeText("CancelLabel", cancelRect, 30, TextAnchor.MiddleCenter, cancelLayer);
            cancelLabel.text = "CANCEL";

            _damageText = MakeText("DamageText", new Rect(0f, Screen.height * 0.55f, width, 80f), 64,
                TextAnchor.MiddleCenter, root);
            _damageText.text = "";
            _damageText.color = new Color(1f, 0.85f, 0.3f, 1f);

            _rejectText = MakeText("RejectText", new Rect(0f, Screen.height * 0.42f, width, 60f), 40,
                TextAnchor.MiddleCenter, root);
            _rejectText.text = "";
            _rejectText.color = new Color(1f, 0.45f, 0.4f, 1f);

            // ------------------------------------------------------------------ C01
            // 下面三块都是 U3 补的，只做加法：不挪既有按钮、不改既有文案、
            // 也不进 HitTest()，所以点选与技能键的判定跟 P0-3c 验收时一模一样。

            // Boss 条那一行：阶段名与序号只认服务端广播，不看血线猜。
            _phaseText = MakeText("PhaseText",
                new Rect(barX, Screen.height - 140f, barWidth * 0.72f, 34f), 24,
                TextAnchor.MiddleLeft, root);
            _phaseText.text = UiBattleCodex.PhaseLine(0, "");

            // 小队四条：名字 · 职业 · 血量百分比，条宽就是回包里的 hp / hpMax。
            var partyX = width - 380f;
            for (var i = 0; i < PartySlots; i++)
            {
                var y = Screen.height - 250f - i * 54f;
                MakeRect("PartyBack" + i, new Rect(partyX, y, 340f, 8f), new Color(0f, 0f, 0f, 0.45f), root);
                _partyFills[i] = MakeRect("PartyFill" + i, new Rect(partyX, y, 0f, 8f),
                    new Color(0.36f, 0.72f, 0.55f, 0.95f), root);
                _partyLabels[i] = MakeText("PartyLabel" + i, new Rect(partyX, y + 12f, 340f, 32f), 22,
                    TextAnchor.MiddleRight, root);
                _partyLabels[i].text = "";
            }

            // Q2：自己的 buff 条。最多六格，一条横排，位置在战斗日志上方；
            // 没有效果时全部关掉，整条不显示。只照服务端快照写名字 / 层数 / 剩余 / 护盾，
            // 本地不读 effects.json、不算到期与层数。
            for (var i = 0; i < SelfEffectSlots; i++)
            {
                var rect = new Rect(40f + i * (SelfEffectSlotWidth + SelfEffectGap), SelfEffectY,
                    SelfEffectSlotWidth, SelfEffectSlotHeight);
                _selfEffectBacks[i] = MakeRect("SelfEffectBack" + i, rect,
                    new Color(0f, 0f, 0f, 0.55f), root).GetComponent<Image>();
                _selfEffectLabels[i] = MakeText("SelfEffectLabel" + i,
                    new Rect(rect.x + 6f, rect.y, rect.width - 12f, rect.height), 19,
                    TextAnchor.MiddleLeft, root);
                _selfEffectLabels[i].text = "";
                _selfEffectBacks[i].enabled = false;
                _selfEffectLabels[i].enabled = false;
            }

            // Q2：Boss 身上的 debuff / dot 小条，贴在 Boss 血条下面一行，最多四格。
            // 只画名字与剩余秒数，不刷战斗日志，免得 DoT 每跳一行把屏刷满。
            var bossEffectSlotWidth = barWidth / BossEffectSlots;
            for (var i = 0; i < BossEffectSlots; i++)
            {
                var rect = new Rect(barX + i * bossEffectSlotWidth, Screen.height - 184f,
                    bossEffectSlotWidth - 8f, 28f);
                _bossEffectBacks[i] = MakeRect("BossEffectBack" + i, rect,
                    new Color(0f, 0f, 0f, 0.5f), root).GetComponent<Image>();
                _bossEffectLabels[i] = MakeText("BossEffectLabel" + i,
                    new Rect(rect.x + 6f, rect.y, rect.width - 12f, rect.height), 20,
                    TextAnchor.MiddleLeft, root);
                _bossEffectLabels[i].text = "";
                _bossEffectBacks[i].enabled = false;
                _bossEffectLabels[i].enabled = false;
            }

            // 战斗日志：写的是回包口径，不是本地编的战报。
            _logText = MakeText("LogText", new Rect(40f, 150f, 660f, 132f), 22, TextAnchor.UpperLeft, root);
            _logText.text = UiBattleCodex.LogHeader + "\n" + _logLine + "\n"
                + UiBattleCodex.HudNotes[1] + "\n" + UiBattleCodex.HudNotes[2];

            Debug.Log("p02b_hud=ready screen=" + Screen.width + "x" + Screen.height
                + " skill_rect=" + SkillButtonRect(0).x + "," + SkillButtonRect(0).y
                + " cancel_rect=" + CancelButtonRect().x + "," + CancelButtonRect().y);
            Debug.Log("p03c_hud=ready screen=" + Screen.width + "x" + Screen.height
                + " class_rect=" + ClassButtonRect().x + "," + ClassButtonRect().y
                + "," + ClassButtonRect().width + "," + ClassButtonRect().height
                + " skill_rect=" + SkillButtonRect(0).x + "," + SkillButtonRect(0).y
                + " cancel_rect=" + CancelButtonRect().x + "," + CancelButtonRect().y);

            Debug.Log("u3_hud=ready boss_bar=1 party_rows=" + PartySlots
                + " phase_rect=" + _phaseText.rectTransform.anchoredPosition.x + ","
                + _phaseText.rectTransform.anchoredPosition.y
                + " log=1 hit_rects_unchanged=1");

            Debug.Log("q2_hud=ready self_slots=" + SelfEffectSlots
                + " boss_slots=" + BossEffectSlots
                + " self_rect=" + 40f + "," + SelfEffectY
                + "," + SelfEffectSlotWidth + "," + SelfEffectSlotHeight
                + " boss_rect=" + barX + "," + (Screen.height - 184f)
                + "," + bossEffectSlotWidth + ",28"
                + " hit_rects_unchanged=1");
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

        void Refresh()
        {
            // 血条只认回包里带回来的那个剩余血量。
            var ratio = _client.BossHpMax > 0 ? Mathf.Clamp01((float)_client.BossHp / _client.BossHpMax) : 0f;
            var fullWidth = Screen.width * 0.6f;
            _hpFill.sizeDelta = new Vector2(fullWidth * ratio, 36f);
            _hpText.text = "BOSS " + _client.BossHp + " / " + _client.BossHpMax;

            _distanceText.text = "DIST " + _client.DistanceMm + " mm";
            _tipText.text = "HP " + _client.CasterHp + " / " + _client.CasterHpMax
                + "   TARGET " + TargetSelector.Current;

            // 蓝条只认服务端发回来的蓝。换职业后上限变了，也等新一场的数。
            var manaRatio = _client.ManaMax > 0 ? Mathf.Clamp01((float)_client.Mana / _client.ManaMax) : 0f;
            _manaFill.sizeDelta = new Vector2(ManaBarWidth * manaRatio, 24f);
            _manaText.text = "MP " + _client.Mana + " / " + _client.ManaMax;

            // 左上角职业 + 阶段。没广播过阶段就只显示职业。
            _classText.text = _client.ClassName
                + (_client.PhaseName.Length > 0 ? " · " + _client.PhaseName : "");

            // Boss 条上的阶段那行：序号与名字都照广播写，服务端没下发总数就不写总数。
            _phaseText.text = UiBattleCodex.PhaseLine(_client.PhaseIndex, _client.PhaseName);

            // 小队四条：名单里几个人画几个，血条宽度就是回包里的比例。
            for (var i = 0; i < PartySlots; i++)
            {
                if (i < _client.Party.Count)
                {
                    var member = _client.Party[i];
                    var memberRatio = member.HpMax > 0 ? Mathf.Clamp01((float)member.Hp / member.HpMax) : 0f;
                    _partyLabels[i].text = (member.Down ? "倒地 · " : "")
                        + member.Name + " · " + UiBattleCodex.ClassName(member.ClassId)
                        + "  " + Mathf.RoundToInt(memberRatio * 100f) + "%";
                    _partyFills[i].sizeDelta = new Vector2(340f * memberRatio, 8f);
                }
                else
                {
                    _partyLabels[i].text = "";
                    _partyFills[i].sizeDelta = new Vector2(0f, 8f);
                }
            }

            // Q2：两条效果条。日志也在这一步补，所以排在 _logText 重写之前。
            RefreshSelfEffects();
            RefreshBossEffects();

            _logText.text = UiBattleCodex.LogHeader + "\n" + _logLine + "\n"
                + UiBattleCodex.HudNotes[1] + "\n" + UiBattleCodex.HudNotes[2];

            // 换职业按钮上写下一个职业的名字，按之前就知道会切到谁。
            if (_client.Classes.Count > 0)
            {
                var current = _client.Classes.FindIndex(c => c.ClassId == BattleClient.ClassId);
                var next = _client.Classes[(current + 1) % _client.Classes.Count];
                _classButtonLabel.text = "切换 " + next.Name;
            }
            else
            {
                _classButtonLabel.text = "换职业";
            }

            // 技能键写字用技能名，跟着当前职业的技能表走。
            for (var i = 0; i < SkillButtonCount; i++)
            {
                var info = i < _client.Skills.Count ? _client.Skills[i] : null;
                _skillLabels[i].text = info != null ? info.Name : "S" + (i + 1);
            }

            // 前摇：进度按服务端下发的时长走，走满就停在那儿等回包。
            if (_client.Casting)
            {
                _castGroup.alpha = 1f;
                var progress = _client.ForeswingMs > 0
                    ? Mathf.Clamp01((float)(_client.CastElapsedMs / _client.ForeswingMs))
                    : 0f;
                _castBar.sizeDelta = new Vector2(Screen.width * 0.4f * progress, 20f);
                _cancelGroup.alpha = progress < 1f ? 1f : 0.35f;
            }
            else
            {
                _castGroup.alpha = 0f;
                _castBar.sizeDelta = new Vector2(0f, 20f);
                _cancelGroup.alpha = 0f;
            }

            if (_seenCancel != _client.CancelSeq)
            {
                _seenCancel = _client.CancelSeq;
            }

            if (_seenDamage != _client.DamageSeq)
            {
                _seenDamage = _client.DamageSeq;
                _damageText.text = "-" + _client.LastDamage;
                _damageAge = 0f;
                _logLine = "回包：命中 -" + _client.LastDamage;
            }

            if (_seenReject != _client.RejectSeq)
            {
                _seenReject = _client.RejectSeq;
                _rejectText.text = _client.LastReject;
                _rejectAge = 0f;
                _logLine = "被拒：" + _client.LastReject;
            }

            if (_seenEnded != _client.EndedSeq)
            {
                _seenEnded = _client.EndedSeq;
                _rejectText.text = "CLEARED";
                _rejectAge = 0f;
                _logLine = "回包：这一场结束";
            }

            // 切阶段飘一行字。血条位置不动，阶段文本只在左上角常驻。
            if (_seenPhase != _client.PhaseSeq)
            {
                _seenPhase = _client.PhaseSeq;
                _rejectText.text = "PHASE " + _client.PhaseName;
                _rejectAge = 0f;
                _logLine = "阶段：" + _client.PhaseName;
            }

            _damageAge += Time.unscaledDeltaTime;
            _rejectAge += Time.unscaledDeltaTime;

            // 飘字：往上走一点，再淡掉。
            var damageAlpha = Mathf.Clamp01(1f - _damageAge / 1.2f);
            _damageText.color = new Color(1f, 0.85f, 0.3f, damageAlpha);
            _damageText.rectTransform.anchoredPosition = new Vector2(
                _damageText.rectTransform.anchoredPosition.x,
                Screen.height * 0.55f + Mathf.Min(_damageAge, 1.2f) * 80f);

            var rejectAlpha = Mathf.Clamp01(1f - _rejectAge / 2f);
            var rejectColor = _rejectText.color;
            _rejectText.color = new Color(rejectColor.r, rejectColor.g, rejectColor.b, rejectAlpha);

            // 结束后技能键变灰，按下去也不再发。换职业按钮在重连中也没得点。
            var enabled = _client.Connected && !_client.Ended;
            for (var i = 0; i < SkillButtonCount; i++)
            {
                _skillImages[i].color = enabled
                    ? new Color(0.16f, 0.36f, 0.62f, 0.85f)
                    : new Color(0.25f, 0.25f, 0.25f, 0.7f);
            }

            _classButtonImage.color = enabled && _client.Classes.Count > 0
                ? new Color(0.32f, 0.22f, 0.55f, 0.85f)
                : new Color(0.25f, 0.25f, 0.25f, 0.7f);
        }

        /// <summary>
        /// Q2：自己那条 buff 条。剩余秒数 = 快照里的 remaining_ms 减去收到后的本地时长，
        /// 到 0 就停在 0；层数与到期仍只认下一条快照，不在这里改任何战斗状态。
        /// </summary>
        void RefreshSelfEffects()
        {
            // 日志只看自身的 id 集合差；Boss 的效果不往这里塞，避免 DoT 刷屏。
            if (_seenEffectSeq != _client.EffectSeq)
            {
                _seenEffectSeq = _client.EffectSeq;
                LogSelfEffectChanges();
            }

            var effects = _client.SelfEffects;
            var ageMs = _client.EffectAgeMs(_client.CasterId);

            for (var i = 0; i < SelfEffectSlots; i++)
            {
                var used = i < effects.Count;
                _selfEffectBacks[i].enabled = used;
                _selfEffectLabels[i].enabled = used;

                if (!used)
                {
                    _selfEffectLabels[i].text = "";
                    continue;
                }

                var effect = effects[i];
                var head = effect.Name;
                if (effect.Stacks > 1)
                {
                    head += " x" + effect.Stacks;
                }

                var tail = RemainingText(effect, ageMs);
                if (effect.Kind == "shield")
                {
                    // 护盾的吸收量是大数，按快照原值显示，不折算成百分比。
                    tail += "  盾 " + effect.ShieldLeft;
                }

                _selfEffectLabels[i].text = head + "\n" + tail;
                _selfEffectLabels[i].color = EffectColor(effect.Kind);
            }
        }

        /// <summary>
        /// Q2：Boss 血条附近的 debuff / dot 小条，最多四格。只显示名字 + 剩余秒数，
        /// 不写战斗日志；没有这两类效果就整条不显示。
        /// </summary>
        void RefreshBossEffects()
        {
            var effects = _client.BossEffects;
            var ageMs = _client.EffectAgeMs(_client.BossId);

            for (var i = 0; i < BossEffectSlots; i++)
            {
                _bossEffectBacks[i].enabled = false;
                _bossEffectLabels[i].enabled = false;
                _bossEffectLabels[i].text = "";
            }

            var slot = 0;
            for (var i = 0; i < effects.Count && slot < BossEffectSlots; i++)
            {
                var effect = effects[i];
                if (effect.Kind != "debuff" && effect.Kind != "dot")
                {
                    continue;
                }

                _bossEffectBacks[slot].enabled = true;
                _bossEffectLabels[slot].enabled = true;
                _bossEffectLabels[slot].text = effect.Name + "  " + RemainingText(effect, ageMs);
                _bossEffectLabels[slot].color = EffectColor(effect.Kind);
                slot++;
            }
        }

        /// <summary>
        /// 自身效果集合变了就在战斗日志里补一句人话。按 effect_id 与上一份做差，
        /// 只做自身；护盾出场写剩余吸收量，其余写「获得 / 结束」。
        /// </summary>
        void LogSelfEffectChanges()
        {
            var current = new Dictionary<string, EffectSnapshot>();
            foreach (var effect in _client.SelfEffects)
            {
                if (!string.IsNullOrEmpty(effect.EffectId))
                {
                    current[effect.EffectId] = effect;
                }
            }

            var lines = new List<string>();
            foreach (var pair in current)
            {
                if (_selfEffectSeen.ContainsKey(pair.Key))
                {
                    continue;
                }

                var effect = pair.Value;
                if (effect.Kind == "shield")
                {
                    lines.Add(effect.Name + " " + effect.ShieldLeft);
                }
                else
                {
                    lines.Add("获得 " + effect.Name + " x" + Mathf.Max(1, (int)effect.Stacks));
                }
            }

            foreach (var pair in _selfEffectSeen)
            {
                if (!current.ContainsKey(pair.Key))
                {
                    lines.Add(pair.Value.Name + " 结束");
                }
            }

            _selfEffectSeen.Clear();
            foreach (var pair in current)
            {
                _selfEffectSeen[pair.Key] = pair.Value;
            }

            if (lines.Count > 0)
            {
                _logLine = "效果：" + string.Join(" · ", lines);
            }
        }

        /// <summary>显示用倒计时：快照剩余毫秒减本地经过的毫秒，最小停在 0.0s。</summary>
        static string RemainingText(EffectSnapshot effect, double ageMs)
        {
            var leftMs = effect.RemainingMs - ageMs;
            if (leftMs < 0)
            {
                leftMs = 0;
            }

            return (leftMs / 1000.0).ToString("0.0") + "s";
        }

        /// <summary>按 kind 分色，只影响观感；不读配置、不按 kind 做任何判定。</summary>
        static Color EffectColor(string kind)
        {
            switch (kind)
            {
                case "buff":
                    return new Color(0.62f, 0.92f, 0.62f, 1f);
                case "debuff":
                    return new Color(1f, 0.66f, 0.58f, 1f);
                case "dot":
                    return new Color(1f, 0.55f, 0.45f, 1f);
                case "control":
                    return new Color(1f, 0.86f, 0.42f, 1f);
                case "hot":
                    return new Color(0.55f, 0.95f, 0.7f, 1f);
                case "shield":
                    return new Color(0.58f, 0.82f, 1f, 1f);
                default:
                    return Color.white;
            }
        }

        /// <summary>一块不画东西的全屏层，只用来把几件东西一起淡入淡出。</summary>
        RectTransform NewLayer(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(Screen.width, Screen.height);
            return rt;
        }

        RectTransform MakeRect(string name, Rect rect, Color color, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(rect.x, rect.y);
            rt.sizeDelta = new Vector2(rect.width, rect.height);

            // 这个界面不挂 EventSystem，点选靠 HitTest 的像素矩形；
            // 显示元素一律不吃射线，免得以后接上 GraphicRaycaster 后挡住技能键。
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return rt;
        }

        Text MakeText(string name, Rect rect, int size, TextAnchor anchor, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);

            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(rect.x, rect.y);
            rt.sizeDelta = new Vector2(rect.width, rect.height);

            var text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        /// <summary>
        /// 安卓上要系统字体才有中文字形，内置的 LegacyRuntime 只有拉丁字母。
        /// 找不到就退回去，界面上的字都是英文，不会变方块。
        /// </summary>
        static Font LoadFont()
        {
            var names = new[] { "PingFang SC", "Microsoft YaHei", "Noto Sans CJK SC", "Droid Sans Fallback", "sans-serif" };
            var font = Font.CreateDynamicFontFromOSFont(names, 32);
            return font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
    }
}
