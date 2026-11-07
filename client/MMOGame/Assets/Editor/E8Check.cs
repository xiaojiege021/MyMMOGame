using System;
using Google.Protobuf;
using Mmo.Proto.Common;
using UnityEditor;
using UnityEngine;

// E8 核对用：客户端把同一份空消息序列化一遍。
// 打印格式要和 tools/ProtoProbe/Program.cs 完全一致，脚本才好逐行比对。
public static class E8Check
{
    public static void Run()
    {
        try
        {
            var message = new Empty();
            var bytes = message.ToByteArray();
            var back = Empty.Parser.ParseFrom(bytes);

            Debug.Log("proto_type=" + typeof(Empty).FullName);
            Debug.Log("proto_file=" + CommonReflection.Descriptor.Name);
            Debug.Log("proto_bytes=" + bytes.Length);
            Debug.Log("proto_roundtrip=" + (back.Equals(message) ? "ok" : "diff"));
            Debug.Log("proto_runtime=" + typeof(MessageExtensions).Assembly.GetName().Version);
            EditorApplication.Exit(bytes.Length == 0 && back.Equals(message) ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.Log("proto_fail=" + e.GetType().Name + " " + e.Message);
            EditorApplication.Exit(1);
        }
    }
}
