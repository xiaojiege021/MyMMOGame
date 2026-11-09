using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
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

        BattleClient _client;

        Font _font;

        RectTransform _hpFill;

        Text _hpText;

        RectTransform _castBar;

        CanvasGroup _castGroup;

        CanvasGroup _cancelGroup;

        readonly Image[] _skillImages = new Image[SkillButtonCount];

        readonly Text[] _skillLabels = new Text[SkillButtonCount];

        Text _damageText;

        Text _rejectText;

        Text _distanceText;

        Text _tipText;

        float _damageAge = 99f;

        float _rejectAge = 99f;

        int _seenDamage;

        int _seenReject;

        int _seenCancel;

        int _seenEnded;

        bool _wasPressed;

        /// <summary>这个屏幕点是不是落在界面的按钮上。点选那边要跳过这块。</summary>
        public static bool HitTest(Vector2 screenPoint)
        {
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

            // 玩家血量：左下角。
            _tipText = MakeText("TipText", new Rect(40f, 40f, 700f, 48f), 26, TextAnchor.MiddleLeft, root);

            // 前摇进度：屏幕下方正中，填充随等待时间走。整层挂一起淡入淡出。
            var castWidth = width * 0.4f;
            var castX = (width - castWidth) / 2f;
            var castY = Screen.height * 0.22f;
            var castLayer = NewLayer("CastLayer", root);
            _castGroup = castLayer.gameObject.AddComponent<CanvasGroup>();
            _castGroup.alpha = 0f;
            MakeRect("CastBack", new Rect(castX, castY, castWidth, 20f), new Color(0f, 0f, 0f, 0.5f), castLayer);
            _castBar = MakeRect("CastFill", new Rect(castX, castY, 0f, 20f), new Color(0.95f, 0.78f, 0.2f, 0.95f), castLayer);

            // 技能键。按钮上的字用 S1/S2/S3，具体技能号看日志。
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

            Debug.Log("p02b_hud=ready screen=" + Screen.width + "x" + Screen.height
                + " skill_rect=" + SkillButtonRect(0).x + "," + SkillButtonRect(0).y
                + " cancel_rect=" + CancelButtonRect().x + "," + CancelButtonRect().y);
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
            }

            if (_seenReject != _client.RejectSeq)
            {
                _seenReject = _client.RejectSeq;
                _rejectText.text = _client.LastReject;
                _rejectAge = 0f;
            }

            if (_seenEnded != _client.EndedSeq)
            {
                _seenEnded = _client.EndedSeq;
                _rejectText.text = "CLEARED";
                _rejectAge = 0f;
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

            // 结束后技能键变灰，按下去也不再发。
            var enabled = _client.Connected && !_client.Ended;
            for (var i = 0; i < SkillButtonCount; i++)
            {
                _skillImages[i].color = enabled
                    ? new Color(0.16f, 0.36f, 0.62f, 0.85f)
                    : new Color(0.25f, 0.25f, 0.25f, 0.7f);
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

            go.GetComponent<Image>().color = color;
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
