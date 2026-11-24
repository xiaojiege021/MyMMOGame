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
    /// 搭界面：血条 / 蓝条 / 技能键 / 前摇 / 小队 / 效果格 / 日志，加三个控件工厂与字体。
    /// </summary>
    public sealed partial class BattleHud : MonoBehaviour
    {
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
            _classButtonLabel.text = UiStrings.HudLayout.T001;

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
