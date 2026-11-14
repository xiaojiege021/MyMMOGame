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
    // System.Action<HotUpdate.Ui.SiegeRow>
    // System.Action<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Action<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Action<Mmo.Net.AuctionOutcome>
    // System.Action<Mmo.Net.BagSlotView>
    // System.Action<Mmo.Net.CurrencyRow>
    // System.Action<Mmo.Net.IngotOutcome>
    // System.Action<Mmo.Net.LeadershipOutcome>
    // System.Action<Mmo.Net.PositionOutcome>
    // System.Action<Mmo.Net.RechargeOrderOutcome>
    // System.Action<Mmo.Net.RechargeSettleOutcome>
    // System.Action<Mmo.Net.SectOutcome>
    // System.Action<Mmo.Net.ShopOutcome>
    // System.Action<Mmo.Net.UpgradeOutcome>
    // System.Action<int,object>
    // System.Action<int>
    // System.Action<object,object>
    // System.Action<object>
    // System.Action<uint>
    // System.Action<ushort>
    // System.ByReference<byte>
    // System.ByReference<object>
    // System.Collections.Generic.ArraySortHelper<HotUpdate.Ui.SiegeRow>
    // System.Collections.Generic.ArraySortHelper<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Collections.Generic.ArraySortHelper<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Collections.Generic.ArraySortHelper<Mmo.Net.BagSlotView>
    // System.Collections.Generic.ArraySortHelper<Mmo.Net.CurrencyRow>
    // System.Collections.Generic.ArraySortHelper<int>
    // System.Collections.Generic.ArraySortHelper<object>
    // System.Collections.Generic.Comparer<HotUpdate.Ui.SiegeRow>
    // System.Collections.Generic.Comparer<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Collections.Generic.Comparer<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Collections.Generic.Comparer<Mmo.Net.BagSlotView>
    // System.Collections.Generic.Comparer<Mmo.Net.CurrencyRow>
    // System.Collections.Generic.Comparer<int>
    // System.Collections.Generic.Comparer<object>
    // System.Collections.Generic.EqualityComparer<object>
    // System.Collections.Generic.ICollection<HotUpdate.Ui.SiegeRow>
    // System.Collections.Generic.ICollection<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Collections.Generic.ICollection<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Collections.Generic.ICollection<Mmo.Net.BagSlotView>
    // System.Collections.Generic.ICollection<Mmo.Net.CurrencyRow>
    // System.Collections.Generic.ICollection<int>
    // System.Collections.Generic.ICollection<object>
    // System.Collections.Generic.IComparer<HotUpdate.Ui.SiegeRow>
    // System.Collections.Generic.IComparer<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Collections.Generic.IComparer<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Collections.Generic.IComparer<Mmo.Net.BagSlotView>
    // System.Collections.Generic.IComparer<Mmo.Net.CurrencyRow>
    // System.Collections.Generic.IComparer<int>
    // System.Collections.Generic.IComparer<object>
    // System.Collections.Generic.IEnumerable<HotUpdate.Ui.SiegeRow>
    // System.Collections.Generic.IEnumerable<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Collections.Generic.IEnumerable<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Collections.Generic.IEnumerable<Mmo.Net.BagSlotView>
    // System.Collections.Generic.IEnumerable<Mmo.Net.CurrencyRow>
    // System.Collections.Generic.IEnumerable<int>
    // System.Collections.Generic.IEnumerable<object>
    // System.Collections.Generic.IEnumerator<HotUpdate.Ui.SiegeRow>
    // System.Collections.Generic.IEnumerator<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Collections.Generic.IEnumerator<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Collections.Generic.IEnumerator<Mmo.Net.BagSlotView>
    // System.Collections.Generic.IEnumerator<Mmo.Net.CurrencyRow>
    // System.Collections.Generic.IEnumerator<int>
    // System.Collections.Generic.IEnumerator<object>
    // System.Collections.Generic.IEqualityComparer<object>
    // System.Collections.Generic.IList<HotUpdate.Ui.SiegeRow>
    // System.Collections.Generic.IList<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Collections.Generic.IList<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Collections.Generic.IList<Mmo.Net.BagSlotView>
    // System.Collections.Generic.IList<Mmo.Net.CurrencyRow>
    // System.Collections.Generic.IList<int>
    // System.Collections.Generic.IList<object>
    // System.Collections.Generic.List.Enumerator<HotUpdate.Ui.SiegeRow>
    // System.Collections.Generic.List.Enumerator<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Collections.Generic.List.Enumerator<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Collections.Generic.List.Enumerator<Mmo.Net.BagSlotView>
    // System.Collections.Generic.List.Enumerator<Mmo.Net.CurrencyRow>
    // System.Collections.Generic.List.Enumerator<int>
    // System.Collections.Generic.List.Enumerator<object>
    // System.Collections.Generic.List<HotUpdate.Ui.SiegeRow>
    // System.Collections.Generic.List<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Collections.Generic.List<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Collections.Generic.List<Mmo.Net.BagSlotView>
    // System.Collections.Generic.List<Mmo.Net.CurrencyRow>
    // System.Collections.Generic.List<int>
    // System.Collections.Generic.List<object>
    // System.Collections.Generic.ObjectComparer<HotUpdate.Ui.SiegeRow>
    // System.Collections.Generic.ObjectComparer<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Collections.Generic.ObjectComparer<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Collections.Generic.ObjectComparer<Mmo.Net.BagSlotView>
    // System.Collections.Generic.ObjectComparer<Mmo.Net.CurrencyRow>
    // System.Collections.Generic.ObjectComparer<int>
    // System.Collections.Generic.ObjectComparer<object>
    // System.Collections.Generic.ObjectEqualityComparer<object>
    // System.Collections.ObjectModel.ReadOnlyCollection<HotUpdate.Ui.SiegeRow>
    // System.Collections.ObjectModel.ReadOnlyCollection<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Collections.ObjectModel.ReadOnlyCollection<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Collections.ObjectModel.ReadOnlyCollection<Mmo.Net.BagSlotView>
    // System.Collections.ObjectModel.ReadOnlyCollection<Mmo.Net.CurrencyRow>
    // System.Collections.ObjectModel.ReadOnlyCollection<int>
    // System.Collections.ObjectModel.ReadOnlyCollection<object>
    // System.Comparison<HotUpdate.Ui.SiegeRow>
    // System.Comparison<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Comparison<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Comparison<Mmo.Net.BagSlotView>
    // System.Comparison<Mmo.Net.CurrencyRow>
    // System.Comparison<int>
    // System.Comparison<object>
    // System.Func<System.Threading.Tasks.VoidTaskResult>
    // System.Func<byte>
    // System.Func<int,object,Mmo.Net.AuctionOutcome>
    // System.Func<int,object,Mmo.Net.IngotOutcome>
    // System.Func<int,object,Mmo.Net.RechargeOrderOutcome>
    // System.Func<int,object,Mmo.Net.RechargeSettleOutcome>
    // System.Func<int,object,Mmo.Net.ShopOutcome>
    // System.Func<int,object,object>
    // System.Func<object,Mmo.Net.LeadershipOutcome>
    // System.Func<object,Mmo.Net.PositionOutcome>
    // System.Func<object,Mmo.Net.SectOutcome>
    // System.Func<object,Mmo.Net.UpgradeOutcome>
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
    // System.Nullable<HotUpdate.Ui.UiEntryCodex.Race>
    // System.Nullable<HotUpdate.Ui.UiEntryCodex.Role>
    // System.Predicate<HotUpdate.Ui.SiegeRow>
    // System.Predicate<HotUpdate.Ui.UiDungeonFlow.PartyRow>
    // System.Predicate<HotUpdate.Ui.UiWorldCodex.GatherNode>
    // System.Predicate<Mmo.Net.BagSlotView>
    // System.Predicate<Mmo.Net.CurrencyRow>
    // System.Predicate<int>
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
        // object UnityEngine.Object.FindObjectOfType<object>()
        // object[] UnityEngine.Object.FindObjectsOfType<object>()
        // object[] UnityEngine.Resources.ConvertObjects<object>(UnityEngine.Object[])
        // object UnityEngine.Resources.GetBuiltinResource<object>(string)
    }
}
