using System.Collections.Generic;
public class AOTGenericReferences : UnityEngine.MonoBehaviour
{

    // {{ AOT assemblies
    public static readonly IReadOnlyList<string> PatchedAOTAssemblyList = new List<string>
    {
        "Unity.InputSystem.dll",
        "UnityEngine.CoreModule.dll",
        "mscorlib.dll",
    };
    // }}

    // {{ constraint implement type
    // }} 

    // {{ AOT generic types
    // System.Collections.Generic.EqualityComparer<object>
    // System.Collections.Generic.IEnumerable<object>
    // System.Collections.Generic.IEnumerator<object>
    // System.Collections.Generic.IEqualityComparer<object>
    // System.Collections.Generic.ObjectEqualityComparer<object>
    // UnityEngine.InputSystem.InputControl<UnityEngine.Vector2>
    // UnityEngine.InputSystem.InputProcessor<UnityEngine.Vector2>
    // UnityEngine.InputSystem.Utilities.InlinedArray<object>
    // }}

    public void RefMethods()
    {
        // object UnityEngine.Component.GetComponent<object>()
        // object[] UnityEngine.Object.FindObjectsOfType<object>()
        // object[] UnityEngine.Resources.ConvertObjects<object>(UnityEngine.Object[])
    }
}
