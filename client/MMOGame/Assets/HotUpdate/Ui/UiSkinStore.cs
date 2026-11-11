using UnityEngine;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 皮肤偏好。按 17.5 第 3 条：只存玩家本地设置，不进 <c>config/</c>、不进协议、
    /// 不参与战斗数值与结算；以后要跨设备同步，另开设置字段，不复用业务表。
    ///
    /// 全客户端只有这一处碰 <c>PlayerPrefs</c>，核对脚本按这一点断言。
    /// </summary>
    public static class UiSkinStore
    {
        /// <summary>本机偏好键。改键名等于丢掉玩家已选的皮肤，别随手改。</summary>
        public const string Key = "mmo.ui.skin";

        /// <summary>明亮，也是默认。</summary>
        public const string LightValue = "light";

        /// <summary>暗黑。</summary>
        public const string DarkValue = "dark";

        /// <summary>读偏好。没存过、存了不认识的值，都按默认明亮。</summary>
        public static UiSkin Load()
        {
            return PlayerPrefs.GetString(Key, LightValue) == DarkValue ? UiSkin.Dark : UiSkin.Light;
        }

        /// <summary>存偏好。切一次写一次，不攒到退出。</summary>
        public static void Save(UiSkin skin)
        {
            PlayerPrefs.SetString(Key, skin == UiSkin.Dark ? DarkValue : LightValue);
            PlayerPrefs.Save();
        }

        /// <summary>清回默认。核对与「恢复默认」用。</summary>
        public static void Reset()
        {
            PlayerPrefs.DeleteKey(Key);
            PlayerPrefs.Save();
        }
    }
}
