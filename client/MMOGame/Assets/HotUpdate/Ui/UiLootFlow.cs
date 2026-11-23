using System;
using System.Collections.Generic;
using Mmo.Net;
using Mmo.Proto.Battle;
using UnityEngine;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U15 掷点流程：收战斗服下发的 `LootAssigned`，把归属逐行摊开；
    /// 表态只走 <see cref="BattleLink.SendLootIntentAsync"/>，三个词 need / greed / pass。
    ///
    /// 点数不在这里出现：本类没有随机、没有区间、没有「谁分高」的比较，
    /// 归属与掷点都在服务端，界面只把回包摊开、把三个词发出去。
    /// </summary>
    public sealed class UiLootFlow : MonoBehaviour
    {
        public const string ReadyLog = "u15_loot_flow=ready";

        readonly List<UiLootRow> _rows = new List<UiLootRow>();

        BattleLink _link;

        /// <summary>回包里的分配口径。临时只有 need_greed 才会来这条。</summary>
        public string Mode { get; private set; } = string.Empty;

        /// <summary>收到过归属没有。没收到时屏上写空态，不自己凑一行。</summary>
        public bool HasRows { get; private set; }

        /// <summary>最近一次发出去的意向，核对与日志用。</summary>
        public string LastIntent { get; private set; } = string.Empty;

        /// <summary>发过几笔意向。</summary>
        public int Intents { get; private set; }

        public List<UiLootRow> Rows => _rows;

        public static UiLootFlow Ensure()
        {
            var existing = FindObjectOfType<UiLootFlow>();
            if (existing != null)
            {
                return existing;
            }

            var go = new GameObject("UiLootFlow");
            var flow = go.AddComponent<UiLootFlow>();
            Debug.Log(ReadyLog);
            return flow;
        }

        /// <summary>战斗连接。表态要经过它，界面自己不发包。</summary>
        public void Bind(BattleLink link)
        {
            _link = link;
        }

        /// <summary>战斗进程收到 `LootAssigned` 时调这里，由它交给屏。没有屏就只记一行。</summary>
        public static void Feed(LootAssigned assigned)
        {
            if (assigned == null)
            {
                return;
            }

            var flow = FindObjectOfType<UiLootFlow>();
            if (flow == null)
            {
                Debug.Log("u15_loot_assigned lines=" + assigned.Lines.Count + " (no_screen)");
                return;
            }

            flow.Apply(assigned);
        }

        public void Apply(LootAssigned assigned)
        {
            var rows = UiLootCodex.Rows(assigned);
            _rows.Clear();
            for (var i = 0; i < rows.Count; i++)
            {
                _rows.Add(rows[i]);
            }

            Mode = assigned.Mode ?? string.Empty;
            HasRows = true;
            Debug.Log("u15_loot_assigned lines=" + _rows.Count + " mode=" + Mode
                + " nobody=" + CountNobody());
        }

        /// <summary>没人要的行数。只用来打点，不改归属。</summary>
        public int CountNobody()
        {
            var nobody = 0;
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].WinnerCharacterId == 0)
                {
                    nobody++;
                }
            }

            return nobody;
        }

        /// <summary>
        /// 对一件掉落表态。三个词之外的直接不发——发了服务端也会整条忽略，
        /// 这里先挡住，屏上不会出现第四个词。点数不由客户端产生。
        /// </summary>
        public bool Decide(string itemId, string decision, Action<Exception> failed = null)
        {
            var word = decision ?? string.Empty;
            if (word != UiLootCodex.Need && word != UiLootCodex.Greed && word != UiLootCodex.Pass)
            {
                Debug.Log("u15_loot_intent=reject word=" + word);
                return false;
            }

            if (_link == null)
            {
                Debug.Log("u15_loot_intent=no_link item=" + itemId);
                return false;
            }

            LastIntent = word;
            Intents++;
            Debug.Log("u15_loot_intent=" + word + " item=" + itemId);
            _link.SendLootIntentAsync(itemId, word).ContinueWith(task =>
            {
                if (task.IsFaulted && failed != null)
                {
                    failed(task.Exception);
                }
            });
            return true;
        }
    }
}
