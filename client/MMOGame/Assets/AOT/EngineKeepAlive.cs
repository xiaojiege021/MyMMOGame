using UnityEngine;

namespace AOT
{
    /// <summary>
    /// 热更代码用到 Physics / Collider，但 IL2CPP 剥离引擎代码时看不到热更 dll，
    /// 会把物理模块剥掉——表现是场景里的 BoxCollider 运行时全没了，射线永远打不中。
    /// 这里在 AOT 侧留一个真实引用，把模块钉住。Assets/link.xml 是第二道保险。
    /// </summary>
    public static class EngineKeepAlive
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Keep()
        {
            var hitSomething = Physics.Raycast(Vector3.zero, Vector3.forward, out var hit, 0.01f);
            var anyCollider = Object.FindObjectOfType<Collider>();
            var box = typeof(BoxCollider).Name;
            Debug.Log("engine_keepalive=ok ray=" + hitSomething
                + " collider=" + (anyCollider != null)
                + " box=" + box + " layer=" + Physics.DefaultRaycastLayers);
        }
    }
}
