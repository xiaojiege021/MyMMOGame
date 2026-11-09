using UnityEngine;

namespace HotUpdate
{
    // 场景里一个可以被点选的目标。目标编号和名字序列化在实体上，
    // 场景就是配置，不在代码里写死。换目标、加目标都改场景。
    public sealed class TargetEntity : MonoBehaviour
    {
        public uint TargetId;
        public string DisplayName = "";
    }
}
