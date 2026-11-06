using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AOT
{
    public sealed class LaunchHost : MonoBehaviour
    {
        public string PackageRoot;

        void Start()
        {
            StartCoroutine(LaunchSession.Run(PackageRoot, Finish));
        }

        void Finish(bool ok, string detail)
        {
            Debug.Log(ok ? "launch_check=ok " + detail : "launch_check=fail " + detail);
#if UNITY_EDITOR
            if (PackageRoot != null)
            {
                EditorApplication.Exit(ok ? 0 : 1);
            }
#endif
        }
    }
}
