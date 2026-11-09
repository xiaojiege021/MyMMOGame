using UnityEngine;
using UnityEngine.InputSystem;

namespace HotUpdate
{
    /// <summary>
    /// 玩家在这张图上的位置。左边半屏按下拖动就是走位，前摇里也能拖。
    ///
    /// 本地先按服务端给的速度上限走，再限频上报；服务端的夹取值回来才改这里，
    /// 所以「走近了才打得到」这件事在客户端看得见，但算不算还是服务端说了算。
    /// </summary>
    public sealed class HeroAvatar : MonoBehaviour
    {
        /// <summary>屏幕左边多大一块算摇杆区。右边留给技能键。</summary>
        const float StickZoneWidth = 0.45f;

        /// <summary>拖到这个像素数就是满速。</summary>
        const float FullTiltPixels = 180f;

        BattleClient _client;

        Transform _boss;

        bool _dragging;

        Vector2 _origin;

        /// <summary>场景里建一个能走的玩家。没有模型，先用胶囊占位。</summary>
        public static HeroAvatar Create(BattleClient client, Transform boss, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "PlayerAvatar";
            go.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);

            // 位置按服务端给的出生坐标摆，别按 Boss 旁边固定 6 米算。
            go.transform.position = new Vector3(
                boss.position.x + client.XMm / 1000f,
                0.9f,
                boss.position.z + client.YMm / 1000f);

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = color;
            }

            // 点选射线不能打到自己身上，否则站在 Boss 前面就选不中。
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var avatar = go.AddComponent<HeroAvatar>();
            avatar._client = client;
            avatar._boss = boss;
            return avatar;
        }

        void Update()
        {
            if (_client == null || _boss == null)
            {
                return;
            }

            var direction = ReadStick();

            if (direction.sqrMagnitude > 0.0001f && _client.Connected)
            {
                var speed = _client.MoveSpeedMmPerS;
                var dt = Time.deltaTime;

                var nextX = _client.XMm + direction.x * speed * dt;
                var nextY = _client.YMm + direction.y * speed * dt;

                // 场地是个圆，本地先夹一次，免得每一帧都被服务端拉回来。
                var radius = _client.ArenaRadiusMm;
                var length = Mathf.Sqrt(nextX * nextX + nextY * nextY);
                if (radius > 0 && length > radius)
                {
                    var scale = radius / length;
                    nextX *= scale;
                    nextY *= scale;
                }

                _client.ReportMove(nextX, nextY);
            }

            // 照本地坐标摆。服务端夹过之后会把 XMm/YMm 改掉，下一帧就跟过去。
            transform.position = new Vector3(
                _boss.position.x + _client.XMm / 1000f,
                transform.position.y,
                _boss.position.z + _client.YMm / 1000f);
        }

        /// <summary>读到方向就返回单位向量乘力度，没按就返回零。</summary>
        Vector2 ReadStick()
        {
            var pointer = Pointer.current;
            if (pointer == null)
            {
                _dragging = false;
                return Vector2.zero;
            }

            var position = pointer.position.ReadValue();

            if (!pointer.press.isPressed)
            {
                _dragging = false;
                return Vector2.zero;
            }

            if (!_dragging)
            {
                if (position.x > Screen.width * StickZoneWidth)
                {
                    return Vector2.zero;
                }

                _dragging = true;
                _origin = position;
                return Vector2.zero;
            }

            var delta = position - _origin;
            if (delta.magnitude < 20f)
            {
                return Vector2.zero;
            }

            var tilt = Mathf.Clamp01(delta.magnitude / FullTiltPixels);
            return delta.normalized * tilt;
        }
    }
}
