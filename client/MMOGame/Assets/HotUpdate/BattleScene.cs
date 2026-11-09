using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate
{
    /// <summary>
    /// 场景入口。先把核对要的坐标打出来，再连战斗进程；
    /// 连上之后才按服务端给的 Boss 编号找场景实体、摆玩家、建界面。
    /// 没连上就只留点选，不凭空造一场本地战斗。
    /// </summary>
    public sealed class BattleScene : MonoBehaviour
    {
        BattleClient _client;

        HeroAvatar _avatar;

        BattleHud _hud;

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

            _client = gameObject.AddComponent<BattleClient>();
        }

        void Update()
        {
            if (_client == null || _hud != null)
            {
                return;
            }

            if (!_client.Connected || _client.BossId == 0)
            {
                return;
            }

            var boss = FindBoss(_client.BossId);
            if (boss == null)
            {
                Debug.Log("p02b_boss_missing id=" + _client.BossId);
                return;
            }

            _avatar = HeroAvatar.Create(_client, boss.transform, new Color(0.25f, 0.55f, 0.9f));
            _hud = BattleHud.Create(_client);

            Debug.Log("p02b_scene=ready boss=" + boss.TargetId + " at=" + boss.transform.position);
        }

        TargetEntity FindBoss(ulong bossId)
        {
            foreach (var target in FindObjectsOfType<TargetEntity>())
            {
                if (target.TargetId == bossId)
                {
                    return target;
                }
            }

            return null;
        }
    }
}
