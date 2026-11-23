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
    /// Q2 的两条效果条与自身效果的差集日志。
    /// </summary>
    public sealed partial class BattleHud : MonoBehaviour
    {
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

        /// <summary>上一份里有没有这个 effect_id，有就返回那一条。</summary>
        EffectSnapshot FindSeenEffect(string effectId)
        {
            foreach (var seen in _selfEffectSeen)
            {
                if (seen.EffectId == effectId)
                {
                    return seen;
                }
            }
            return null;
        }

        /// <summary>新的一份里有没有这个 effect_id，有就返回那一条。</summary>
        static EffectSnapshot FindEffect(List<EffectSnapshot> list, string effectId)
        {
            foreach (var item in list)
            {
                if (item.EffectId == effectId)
                {
                    return item;
                }
            }
            return null;
        }

        /// <summary>
        /// 自身效果集合变了就在战斗日志里补一句人话。按 effect_id 与上一份做差，
        /// 只做自身；护盾出场写剩余吸收量，其余写「获得 / 结束」。
        /// </summary>
        void LogSelfEffectChanges()
        {
            var current = new List<EffectSnapshot>();
            foreach (var effect in _client.SelfEffects)
            {
                if (!string.IsNullOrEmpty(effect.EffectId))
                {
                    current.Add(effect);
                }
            }

            var lines = new List<string>();
            foreach (var effect in current)
            {
                if (FindSeenEffect(effect.EffectId) != null)
                {
                    continue;
                }

                if (effect.Kind == "shield")
                {
                    lines.Add(effect.Name + " " + effect.ShieldLeft);
                }
                else
                {
                    lines.Add("获得 " + effect.Name + " x" + Mathf.Max(1, (int)effect.Stacks));
                }
            }

            foreach (var old in _selfEffectSeen)
            {
                if (FindEffect(current, old.EffectId) == null)
                {
                    lines.Add(old.Name + " 结束");
                }
            }

            _selfEffectSeen.Clear();
            _selfEffectSeen.AddRange(current);

            if (lines.Count > 0)
            {
                _logLine = "效果：" + string.Join(" · ", lines.ToArray());
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
    }
}
