using System;
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Mmo.Proto.Battle;
using UnityEditor;
using UnityEngine;

// P0-1a 核对用：客户端把技能请求和技能结果各走一次往返，打印字段清单。
// 打印格式要和 tools/probe/ProtoProbe/Program.cs 的 --p0-1a 分支完全一致，脚本才好逐行比对。
public static class P0Check
{
    public static void Run()
    {
        try
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

            Debug.Log("p0_request_type=" + typeof(SkillRequest).FullName);
            Debug.Log("p0_request_fields=" + FieldNames(SkillRequest.Descriptor));
            Debug.Log("p0_result_type=" + typeof(SkillResult).FullName);
            Debug.Log("p0_result_fields=" + FieldNames(SkillResult.Descriptor));
            Debug.Log("p0_roundtrip=" + (roundtrip ? "ok" : "diff"));
            EditorApplication.Exit(roundtrip ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.Log("p0_fail=" + e.GetType().Name + " " + e.Message);
            EditorApplication.Exit(1);
        }
    }

    // 按 proto 里的声明顺序打印，和服务端那份同一个顺序。
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
