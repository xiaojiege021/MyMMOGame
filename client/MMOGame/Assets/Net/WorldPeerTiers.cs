using Mmo.Proto.World;

namespace Mmo.Net
{
    /// <summary>方案正文写定的三档表现：完整、简模、只留名字。</summary>
    public enum PeerVisualKind
    {
        Full,
        Simple,
        NameOnly,
    }

    /// <summary>
    /// 一个「别人」该画成什么样。这只是展示规格，不是模型、场景或 UI 本身；
    /// 服务端给的档位说什么就画什么，客户端不按距离重算，也不在这里做 AOI。
    /// </summary>
    public struct PeerVisual
    {
        public PeerVisualKind Kind;
        public bool FullModel;
        public bool SimpleModel;
        public bool NamePlate;
        // 方案 15.1 与开发架构第 3 节：别人的拖尾和光效关掉，三档都不开。
        public bool TrailsAndEffects;
        public string DisplayName;
    }

    /// <summary>
    /// 把世界服下发的 AppearanceTier 翻译成展示规格。
    /// 档位由世界服按距离算并发给周围的人（开发架构 4.2 / 9），
    /// 这里不读坐标、不算距离、不认识新档位时按最省的一档处理，绝不擅自画全。
    /// </summary>
    public static class WorldPeerTiers
    {
        public static PeerVisual For(PeerPosition peer)
        {
            return For(peer.Tier, peer.DisplayName);
        }

        public static PeerVisual For(AvatarPosition avatar)
        {
            return For(avatar.AppearanceTier, avatar.DisplayName);
        }

        public static PeerVisual For(AppearanceTier tier, string displayName)
        {
            var name = displayName ?? string.Empty;
            switch (tier)
            {
                case AppearanceTier.Full:
                    return new PeerVisual
                    {
                        Kind = PeerVisualKind.Full,
                        FullModel = true,
                        SimpleModel = false,
                        NamePlate = true,
                        TrailsAndEffects = false,
                        DisplayName = name,
                    };
                case AppearanceTier.Simple:
                    return new PeerVisual
                    {
                        Kind = PeerVisualKind.Simple,
                        FullModel = false,
                        SimpleModel = true,
                        NamePlate = true,
                        TrailsAndEffects = false,
                        DisplayName = name,
                    };
                case AppearanceTier.Name:
                default:
                    // 名字档与未识别的档位都按最省处理：只有名字牌，没有模型。
                    return new PeerVisual
                    {
                        Kind = PeerVisualKind.NameOnly,
                        FullModel = false,
                        SimpleModel = false,
                        NamePlate = true,
                        TrailsAndEffects = false,
                        DisplayName = name,
                    };
            }
        }
    }
}
