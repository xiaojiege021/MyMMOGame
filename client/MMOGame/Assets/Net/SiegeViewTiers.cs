using Mmo.Proto.Battle;

namespace Mmo.Net
{
    /// <summary>方案 15.1 写定的城战两档：完整模型，或只留队伍颜色和血条。</summary>
    public enum SiegeVisualKind
    {
        Full,
        MarkerOnly,
    }

    /// <summary>
    /// 一个单位该画成什么样。这只是展示规格，不是模型、场景或 UI 本身。
    /// 队伍颜色只按服务端给的队伍编号，血条只按服务端给的血量与上限。
    /// </summary>
    public struct SiegeVisualSpec
    {
        public SiegeVisualKind Kind;
        public bool FullModel;
        public bool SimpleModel;
        public bool TeamColor;
        public bool HpBar;
        public bool NamePlate;
        public bool TrailsAndEffects;
        public uint Team;
        public long Hp;
        public long HpMax;
    }

    /// <summary>
    /// 把战斗服下发的城战档位翻译成展示规格（P3-3a2）。
    /// 档位由战斗服按 config/siege.json 的上限确认，这里不读坐标、不算距离、
    /// 不按人数重排，也不认识新档位时擅自画全：未识别档位一律按最省的一档处理。
    /// </summary>
    public static class SiegeViewTiers
    {
        public static SiegeVisualSpec For(SiegeUnitVisual unit)
        {
            var spec = new SiegeVisualSpec
            {
                Team = unit.Team,
                Hp = unit.Hp,
                HpMax = unit.HpMax,
                // 别人的拖尾和光效恒关，两档都不开。
                TrailsAndEffects = false,
            };

            switch (unit.Tier)
            {
                case SiegeVisualTier.SiegeVisualFull:
                    spec.Kind = SiegeVisualKind.Full;
                    spec.FullModel = true;
                    spec.SimpleModel = false;
                    spec.TeamColor = true;
                    spec.HpBar = true;
                    spec.NamePlate = true;
                    break;
                case SiegeVisualTier.SiegeVisualMarker:
                default:
                    // 超出同屏上限与未识别档位：没有完整模型，只留队伍颜色和血条。
                    spec.Kind = SiegeVisualKind.MarkerOnly;
                    spec.FullModel = false;
                    spec.SimpleModel = false;
                    spec.TeamColor = true;
                    spec.HpBar = true;
                    spec.NamePlate = false;
                    break;
            }

            return spec;
        }
    }
}
