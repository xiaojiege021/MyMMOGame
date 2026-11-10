using UnityEditor;
using UnityEditor.Android;
using UnityEngine;

// 把《长生隙》插画写进 Android 各档图标槽位。
// 关键在 adaptive icon：它有前景 / 背景两层，只填一张贴图时前景会留 Unity 自带
// 的白色立方体，桌面上就变成插画中间盖一个 Unity 标志。这里把前景设成全透明、
// 背景设成插画，桌面只显示插画。换图后重跑一次即可：Unity -batchmode -quit
// -projectPath client/MMOGame -executeMethod ApplyAppIcon.Run
public static class ApplyAppIcon
{
    const string ArtPath = "Assets/Art/Icon/app-icon.png";
    const string ClearPath = "Assets/Art/Icon/app-icon-foreground.png";

    public static void Run()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var art = AssetDatabase.LoadAssetAtPath<Texture2D>(ArtPath);
        var clear = AssetDatabase.LoadAssetAtPath<Texture2D>(ClearPath);
        if (art == null || clear == null)
        {
            Debug.Log("app_icon=missing art=" + (art != null) + " clear=" + (clear != null));
            EditorApplication.Exit(1);
            return;
        }

        var kinds = new[]
        {
            AndroidPlatformIconKind.Legacy,
            AndroidPlatformIconKind.Round,
            AndroidPlatformIconKind.Adaptive
        };
        foreach (var kind in kinds)
        {
            var icons = PlayerSettings.GetPlatformIcons(BuildTargetGroup.Android, kind);
            Debug.Log("app_icon_kind=" + kind + " count=" + icons.Length);
            foreach (var icon in icons)
            {
                var layers = icon.GetTextures();
                if (kind == AndroidPlatformIconKind.Adaptive)
                {
                    // layer 0 = foreground（透明），layer 1 = background（插画）。
                    for (var i = 0; i < layers.Length; i++)
                    {
                        icon.SetTexture(i == 0 ? clear : art, i);
                    }
                }
                else
                {
                    icon.SetTexture(art, 0);
                }
                Debug.Log("app_icon_slot=" + kind + " size=" + icon.width + " layers=" + layers.Length);
            }

            PlayerSettings.SetPlatformIcons(BuildTargetGroup.Android, kind, icons);
        }

        AssetDatabase.SaveAssets();
        Debug.Log("app_icon=ok");
        EditorApplication.Exit(0);
    }
}
