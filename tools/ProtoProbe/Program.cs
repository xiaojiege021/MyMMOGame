using Google.Protobuf;
using Mmo.Proto.Common;

// E8 核对用：服务端把空消息序列化一遍，打印和客户端同一组数。
// 客户端那边的代码在 Assets/Editor/E8Check.cs，打印格式必须一致。
internal static class Program
{
    static int Main()
    {
        var message = new Empty();
        var bytes = message.ToByteArray();
        var back = Empty.Parser.ParseFrom(bytes);

        Console.WriteLine("proto_type=" + typeof(Empty).FullName);
        Console.WriteLine("proto_file=" + CommonReflection.Descriptor.Name);
        Console.WriteLine("proto_bytes=" + bytes.Length);
        Console.WriteLine("proto_roundtrip=" + (back.Equals(message) ? "ok" : "diff"));
        return bytes.Length == 0 && back.Equals(message) ? 0 : 1;
    }
}
