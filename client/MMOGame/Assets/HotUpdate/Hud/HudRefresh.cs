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
    /// 每帧把回包里的数写到屏上（血、蓝、小队、前摇、飘字、按钮灰化）。
    /// </summary>
    public sealed partial class BattleHud : MonoBehaviour
    {
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
    }
}
