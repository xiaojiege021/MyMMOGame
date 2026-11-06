using System.IO;
using UnityEngine;

namespace AOT
{
    public static class LaunchLoader
    {
        public const string HostRootFile = "Temp/e6-host-root.txt";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Start()
        {
            var host = new GameObject("LaunchHost").AddComponent<LaunchHost>();
            host.PackageRoot = ReadHostRoot();
        }

        static string ReadHostRoot()
        {
            if (!File.Exists(HostRootFile))
            {
                return null;
            }

            var root = File.ReadAllText(HostRootFile).Trim();
            File.Delete(HostRootFile);
            return root.Length == 0 ? null : root;
        }
    }
}
