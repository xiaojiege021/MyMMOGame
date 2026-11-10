using Google.Protobuf;
using Google.Protobuf.Reflection;
using Mmo.Proto.Battle;
using Mmo.Proto.Common;

// E8 核对用：服务端把空消息序列化一遍，打印和客户端同一组数。
// 客户端那边的代码在 Assets/Editor/E8Check.cs，打印格式必须一致。
//
// P0-1a 核对用：加 --p0-1a，打印技能请求与技能结果的字段清单，再走一次往返。
// 客户端那边的代码在 Assets/Editor/P0Check.cs，打印格式同样必须一致。
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--p0-1a")
        {
            return P0Probe();
        }

        var message = new Empty();
        var bytes = message.ToByteArray();
        var back = Empty.Parser.ParseFrom(bytes);

        Console.WriteLine("proto_type=" + typeof(Empty).FullName);
        Console.WriteLine("proto_file=" + CommonReflection.Descriptor.Name);
        Console.WriteLine("proto_bytes=" + bytes.Length);
        Console.WriteLine("proto_roundtrip=" + (back.Equals(message) ? "ok" : "diff"));
        return bytes.Length == 0 && back.Equals(message) ? 0 : 1;
    }

    static int P0Probe()
    {
        var request = new SkillRequest
        {
            SkillId = 1001,
            TargetId = 42,
            CastTimeMs = 1_700_000_000_000,
        };
        var result = new SkillResult
        {
            CasterId = 7,
            SkillId = 1001,
            TargetId = 42,
            Hit = true,
            Damage = 1234,
            Healing = 0,
            StaggerMs = 300,
            CooldownMs = 6000,
        };

        var requestBack = SkillRequest.Parser.ParseFrom(request.ToByteArray());
        var resultBack = SkillResult.Parser.ParseFrom(result.ToByteArray());
        var roundtrip = requestBack.Equals(request) && resultBack.Equals(result);

        Console.WriteLine("p0_request_type=" + typeof(SkillRequest).FullName);
        Console.WriteLine("p0_request_fields=" + FieldNames(SkillRequest.Descriptor));
        Console.WriteLine("p0_result_type=" + typeof(SkillResult).FullName);
        Console.WriteLine("p0_result_fields=" + FieldNames(SkillResult.Descriptor));
        Console.WriteLine("p0_roundtrip=" + (roundtrip ? "ok" : "diff"));
        return roundtrip ? 0 : 1;
    }

    // 按 proto 里的声明顺序打印。两端是同一份 proto，顺序必然一致。
    static string FieldNames(MessageDescriptor descriptor)
    {
        var names = new List<string>();
        foreach (var field in descriptor.Fields.InDeclarationOrder())
        {
            names.Add(field.Name);
        }
        return string.Join(",", names);
    }
}
