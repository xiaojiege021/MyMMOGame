using Mmo.Proto.World;

namespace World;

/// <summary>
/// 同屏档位的唯一计算点：按观察者与被观察者的距离，从 config/world.json 的两档阈值
/// 算出「近处完整、远处简模、再远只留名字」。方案写定三档的顺序，没写定距离，
/// 这里不写死数值、也不给兜底。档位由世界服算完随快照发给周围的人，
/// 客户端只按收到的档位决定画什么，不在手机上自行决定。
/// 不做 AOI 与距离裁剪：同一分线仍是全量名单，档位只改表现。
/// </summary>
public sealed class AppearanceTierTable(WorldConfig config)
{
    public AppearanceTier ForDistance(double distance) =>
        distance < config.TierNearDistance ? AppearanceTier.Full
        : distance < config.TierFarDistance ? AppearanceTier.Simple
        : AppearanceTier.Name;

    public AppearanceTier For(AvatarState self, AvatarState other)
    {
        var dx = (double)other.X - self.X;
        var dy = (double)other.Y - self.Y;
        var dz = (double)other.Z - self.Z;
        return ForDistance(Math.Sqrt(dx * dx + dy * dy + dz * dz));
    }
}
