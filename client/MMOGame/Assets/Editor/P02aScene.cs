using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// P0-2a 阶段：生成 BossBattle 场景。热更脚本不能被编辑器程序集直接引用
// （autoReferenced 是关的），所以挂组件全走反射。
public static class P02aScene
{
    const string ScenePath = "Assets/Scenes/BossBattle.unity";

    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        // 竖屏 1600x2560，正交半高 8：宽半 5。两个目标各占左右半屏，
        // 屏幕投影 x=400 和 x=1200，核对脚本的 tap 坐标就是从这来的。
        var camera = Camera.main;
        camera.orthographic = true;
        camera.orthographicSize = 8f;
        camera.transform.position = new Vector3(0f, 1f, -8f);
        camera.transform.rotation = Quaternion.identity;
        camera.backgroundColor = new Color(0.16f, 0.19f, 0.24f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        // URP 的相机附加数据。没有它运行时会报警告，渲染也要走兜底。
        camera.gameObject.AddComponent<UniversalAdditionalCameraData>();

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(0.9f, 1f, 1.2f);

        MakeTarget("BossStone", "试炼石像", 9001, new Vector3(-2.5f, 0.5f, 0f), 1f);
        MakeTarget("TrainingDummy", "训练木桩", 9002, new Vector3(2.5f, 0.5f, 0f), 1f);

        var picker = new GameObject("TargetPicker");
        AddHotComponent(picker, "HotUpdate.TargetPicker");

        var ready = new GameObject("SceneReady");
        AddHotComponent(ready, "HotUpdate.BattleScene");

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("p02a_scene_built=ok path=" + ScenePath);
        EditorApplication_Exit();
    }

    static void MakeTarget(string name, string displayName, uint id, Vector3 position, float scale)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position = position;
        go.transform.localScale = Vector3.one * scale;

        var entity = AddHotComponent(go, "HotUpdate.TargetEntity");
        var so = new SerializedObject(entity);
        so.FindProperty("TargetId").uintValue = id;
        so.FindProperty("DisplayName").stringValue = displayName;
        so.ApplyModifiedProperties();
    }

    static Component AddHotComponent(GameObject go, string typeName)
    {
        var type = Type.GetType(typeName + ", HotUpdate");
        if (type == null)
        {
            Debug.Log("p02a_scene_built=fail reason=no_type " + typeName);
            UnityEditor.EditorApplication.Exit(1);
            return null;
        }

        return go.AddComponent(type);
    }

    static void EditorApplication_Exit()
    {
        UnityEditor.EditorApplication.Exit(0);
    }
}
