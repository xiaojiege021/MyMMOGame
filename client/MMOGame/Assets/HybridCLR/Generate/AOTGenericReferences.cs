using System.Collections.Generic;
public class AOTGenericReferences : UnityEngine.MonoBehaviour
{

    // {{ AOT assemblies
    public static readonly IReadOnlyList<string> PatchedAOTAssemblyList = new List<string>
    {
        "Google.Protobuf.dll",
        "System.Core.dll",
        "Unity.InputSystem.dll",
        "UnityEngine.CoreModule.dll",
        "mscorlib.dll",
    };
    // }}

    // {{ constraint implement type
    // }} 

    // {{ AOT generic types
    // Google.Protobuf.Collections.RepeatedField.<GetEnumerator>d__29<object>
    // Google.Protobuf.Collections.RepeatedField<object>
    // Google.Protobuf.FieldCodec.<>c<object>
    // Google.Protobuf.FieldCodec.<>c__DisplayClass38_0<object>
    // Google.Protobuf.FieldCodec.<>c__DisplayClass39_0<object>
    // Google.Protobuf.FieldCodec.InputMerger<object>
    // Google.Protobuf.FieldCodec.ValuesMerger<object>
    // Google.Protobuf.FieldCodec<object>
    // Google.Protobuf.IDeepCloneable<object>
    // Google.Protobuf.ValueReader<object>
    // Google.Protobuf.ValueWriter<object>
    // System.Action<object,object>
    // System.Action<object>
    // System.ByReference<byte>
    // System.ByReference<object>
    // System.Collections.Generic.ArraySortHelper<object>
    // System.Collections.Generic.Comparer<object>
    // System.Collections.Generic.EqualityComparer<object>
    // System.Collections.Generic.ICollection<object>
    // System.Collections.Generic.IComparer<object>
    // System.Collections.Generic.IEnumerable<object>
    // System.Collections.Generic.IEnumerator<object>
    // System.Collections.Generic.IEqualityComparer<object>
    // System.Collections.Generic.IList<object>
    // System.Collections.Generic.List.Enumerator<object>
    // System.Collections.Generic.List<object>
    // System.Collections.Generic.ObjectComparer<object>
    // System.Collections.Generic.ObjectEqualityComparer<object>
    // System.Collections.ObjectModel.ReadOnlyCollection<object>
    // System.Comparison<object>
    // System.Func<System.Threading.Tasks.VoidTaskResult>
    // System.Func<object,System.Threading.Tasks.VoidTaskResult>
    // System.Func<object,byte>
    // System.Func<object,int>
    // System.Func<object,object>
    // System.Func<object>
    // System.Linq.Buffer<object>
    // System.Linq.Enumerable.Iterator<object>
    // System.Linq.Enumerable.WhereEnumerableIterator<object>
    // System.Linq.Enumerable.WhereSelectArrayIterator<object,object>
    // System.Linq.Enumerable.WhereSelectEnumerableIterator<object,object>
    // System.Linq.Enumerable.WhereSelectListIterator<object,object>
    // System.Predicate<object>
    // System.ReadOnlySpan<byte>
    // System.ReadOnlySpan<object>
    // System.Runtime.CompilerServices.AsyncTaskMethodBuilder<System.Threading.Tasks.VoidTaskResult>
    // System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter<System.Threading.Tasks.VoidTaskResult>
    // System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter<object>
    // System.Runtime.CompilerServices.ConfiguredTaskAwaitable<System.Threading.Tasks.VoidTaskResult>
    // System.Runtime.CompilerServices.ConfiguredTaskAwaitable<object>
    // System.Runtime.CompilerServices.TaskAwaiter<System.Threading.Tasks.VoidTaskResult>
    // System.Runtime.CompilerServices.TaskAwaiter<object>
    // System.Span<byte>
    // System.Span<object>
    // System.Threading.Tasks.ContinuationTaskFromResultTask<System.Threading.Tasks.VoidTaskResult>
    // System.Threading.Tasks.ContinuationTaskFromResultTask<object>
    // System.Threading.Tasks.Task<System.Threading.Tasks.VoidTaskResult>
    // System.Threading.Tasks.Task<object>
    // System.Threading.Tasks.TaskFactory<System.Threading.Tasks.VoidTaskResult>
    // System.Threading.Tasks.TaskFactory<object>
    // UnityEngine.InputSystem.InputControl<UnityEngine.Vector2>
    // UnityEngine.InputSystem.InputProcessor<UnityEngine.Vector2>
    // UnityEngine.InputSystem.Utilities.InlinedArray<object>
    // }}

    public void RefMethods()
    {
        // System.Collections.Generic.IEnumerable<object> System.Linq.Enumerable.Select<object,object>(System.Collections.Generic.IEnumerable<object>,System.Func<object,object>)
        // object[] System.Linq.Enumerable.ToArray<object>(System.Collections.Generic.IEnumerable<object>)
        // System.Collections.Generic.IEnumerable<object> System.Linq.Enumerable.Iterator<object>.Select<object>(System.Func<object,object>)
        // System.Void System.Runtime.CompilerServices.AsyncTaskMethodBuilder.AwaitUnsafeOnCompleted<System.Runtime.CompilerServices.TaskAwaiter<object>,HotUpdate.BattleClient.<ConnectAsync>d__140>(System.Runtime.CompilerServices.TaskAwaiter<object>&,HotUpdate.BattleClient.<ConnectAsync>d__140&)
        // System.Void System.Runtime.CompilerServices.AsyncTaskMethodBuilder<System.Threading.Tasks.VoidTaskResult>.AwaitUnsafeOnCompleted<System.Runtime.CompilerServices.TaskAwaiter<object>,HotUpdate.BattleClient.<ConnectAsync>d__140>(System.Runtime.CompilerServices.TaskAwaiter<object>&,HotUpdate.BattleClient.<ConnectAsync>d__140&)
        // System.Void System.Runtime.CompilerServices.AsyncTaskMethodBuilder.Start<HotUpdate.BattleClient.<ConnectAsync>d__140>(HotUpdate.BattleClient.<ConnectAsync>d__140&)
        // System.Void System.Runtime.CompilerServices.AsyncVoidMethodBuilder.AwaitUnsafeOnCompleted<System.Runtime.CompilerServices.TaskAwaiter,HotUpdate.BattleClient.<Fire>d__150>(System.Runtime.CompilerServices.TaskAwaiter&,HotUpdate.BattleClient.<Fire>d__150&)
        // System.Void System.Runtime.CompilerServices.AsyncVoidMethodBuilder.AwaitUnsafeOnCompleted<System.Runtime.CompilerServices.TaskAwaiter,HotUpdate.BattleClient.<OnDestroy>d__151>(System.Runtime.CompilerServices.TaskAwaiter&,HotUpdate.BattleClient.<OnDestroy>d__151&)
        // System.Void System.Runtime.CompilerServices.AsyncVoidMethodBuilder.AwaitUnsafeOnCompleted<System.Runtime.CompilerServices.TaskAwaiter,HotUpdate.BattleClient.<Start>d__138>(System.Runtime.CompilerServices.TaskAwaiter&,HotUpdate.BattleClient.<Start>d__138&)
        // System.Void System.Runtime.CompilerServices.AsyncVoidMethodBuilder.AwaitUnsafeOnCompleted<System.Runtime.CompilerServices.TaskAwaiter,HotUpdate.BattleClient.<SwitchClass>d__141>(System.Runtime.CompilerServices.TaskAwaiter&,HotUpdate.BattleClient.<SwitchClass>d__141&)
        // System.Void System.Runtime.CompilerServices.AsyncVoidMethodBuilder.Start<HotUpdate.BattleClient.<Fire>d__150>(HotUpdate.BattleClient.<Fire>d__150&)
        // System.Void System.Runtime.CompilerServices.AsyncVoidMethodBuilder.Start<HotUpdate.BattleClient.<OnDestroy>d__151>(HotUpdate.BattleClient.<OnDestroy>d__151&)
        // System.Void System.Runtime.CompilerServices.AsyncVoidMethodBuilder.Start<HotUpdate.BattleClient.<Start>d__138>(HotUpdate.BattleClient.<Start>d__138&)
        // System.Void System.Runtime.CompilerServices.AsyncVoidMethodBuilder.Start<HotUpdate.BattleClient.<SwitchClass>d__141>(HotUpdate.BattleClient.<SwitchClass>d__141&)
        // object UnityEngine.Component.GetComponent<object>()
        // object UnityEngine.GameObject.AddComponent<object>()
        // object UnityEngine.GameObject.GetComponent<object>()
        // object[] UnityEngine.Object.FindObjectsOfType<object>()
        // object[] UnityEngine.Resources.ConvertObjects<object>(UnityEngine.Object[])
        // object UnityEngine.Resources.GetBuiltinResource<object>(string)
    }
}
