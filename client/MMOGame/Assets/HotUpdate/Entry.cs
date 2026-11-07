using Google.Protobuf;
using Mmo.Proto.Common;

namespace HotUpdate
{
    // 热更程序集的进门处。AOT 侧只用反射调它的 Ping，不直接引用类型。
    public static class Entry
    {
        public const int Version = 1;

        public static string Ping()
        {
            return "hotupdate-ok";
        }

        // 用来确认热更程序集也能用 proto 生成的类型。
        public static int EmptyBytes()
        {
            return new Empty().ToByteArray().Length;
        }
    }
}
