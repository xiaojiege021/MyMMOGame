using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace HotUpdate
{
    /// <summary>把点选输入接到目标上。项目是纯 Input System（activeInputHandler=1），
    /// 旧 Input API 在这里是死的，必须走 Pointer.current。</summary>
    public sealed class TargetPicker : MonoBehaviour
    {
        Camera _camera;
        bool _wasPressed;

        void Awake()
        {
            _camera = Camera.main;
            Debug.Log("p02a_picker=awake camera=" + (_camera != null));
            if (_camera != null)
            {
                Debug.Log("p02a_camera name=" + _camera.name
                    + " pos=" + _camera.transform.position
                    + " ortho=" + _camera.orthographic
                    + " size=" + _camera.orthographicSize
                    + " rect=" + _camera.pixelRect
                    + " screen=" + Screen.width + "x" + Screen.height
                    + " culling=" + _camera.cullingMask);
            }
        }

        void Start()
        {
            // 物理世界里到底有哪些碰撞体，位置对不对，一次打清楚。
            var colliders = FindObjectsOfType<Collider>();
            Debug.Log("p02a_colliders count=" + colliders.Length);
            foreach (var collider in colliders)
            {
                Debug.Log("p02a_collider " + collider.name
                    + " enabled=" + collider.enabled
                    + " trigger=" + collider.isTrigger
                    + " pos=" + collider.transform.position
                    + " bounds=" + collider.bounds.center + "/" + collider.bounds.size);
            }
        }

        void Update()
        {
            if (_camera == null)
            {
                return;
            }

            var pointer = Pointer.current;
            if (pointer == null)
            {
                if (Time.frameCount % 300 == 0)
                {
                    Debug.Log("p02a_pointer=none");
                }

                return;
            }

            // 用按下状态的边沿，而不是 wasPressedThisFrame：
            // 注入的触摸事件偶尔丢单帧，边沿检测不会漏。
            var pressed = pointer.press.isPressed;
            if (pressed != _wasPressed)
            {
                Debug.Log("p02a_press=" + pressed + " device=" + pointer.device.name);
            }

            if (!pressed || _wasPressed)
            {
                _wasPressed = pressed;
                return;
            }

            _wasPressed = true;
            var position = pointer.position.ReadValue();
            Debug.Log("p02a_tap x=" + (int)position.x + " y=" + (int)position.y);

            // 右下角是技能键和撤键，点在那儿不该顺手换个目标。
            if (BattleHud.HitTest(position))
            {
                Debug.Log("p02a_tap ui=skip");
                return;
            }

            var ray = _camera.ScreenPointToRay(position);
            if (!Physics.Raycast(ray, out var hit, 100f))
            {
                Debug.Log("p02a_ray=miss");
                return;
            }

            var entity = hit.collider.GetComponent<TargetEntity>();
            Debug.Log("p02a_ray=hit collider=" + hit.collider.name + " entity=" + (entity != null));
            if (entity != null)
            {
                TargetSelector.Select(entity.TargetId, entity.DisplayName);
            }
        }
    }
}
