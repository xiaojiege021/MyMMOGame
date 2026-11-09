using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate
{
    /// <summary>场景就绪哨兵。真机核对等 logcat 里这一行再开始点。</summary>
    public sealed class BattleScene : MonoBehaviour
    {
        void Start()
        {
            var targets = FindObjectsOfType<TargetEntity>();
            var camera = Camera.main;

            // 每个目标的点击坐标打出来给核对脚本用。
            // adb input tap 的原点在屏幕左上，和 Unity 的左下相反，这里直接换成 adb 的口径。
            if (camera != null)
            {
                foreach (var target in targets)
                {
                    var point = camera.WorldToScreenPoint(target.transform.position);
                    var tapY = Screen.height - (int)point.y;
                    Debug.Log("target_screen=" + target.TargetId
                        + " x=" + (int)point.x + " y=" + tapY);
                }
            }

            Debug.Log("p02a_scene=ok targets=" + targets.Length);
        }
    }
}
