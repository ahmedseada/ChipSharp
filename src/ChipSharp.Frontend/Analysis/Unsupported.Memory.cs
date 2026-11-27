namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The heap and its collector in C# words: System.GC's methods, which need a heap, and the rule that lets the
/// collector run with interrupts on (see src/ChipSharp.Runtime.Shared/chipsharp_heap.c).
/// </summary>
internal sealed partial record Unsupported
{
    /// <summary>A call of GC.Collect, GC.GetTotalMemory or GC.KeepAlive: on a target without a heap, there is none to work on.</summary>
    public static readonly Unsupported GcMethods = Heap("System.GC's methods (GC.Collect, GC.GetTotalMemory, GC.KeepAlive)",
        true, "Without a heap there is no garbage to collect and no object to keep alive: leave them out.");

    /// <summary>
    /// A rule: an interrupt handler, or a method it calls (a delegate it invokes too), stores a reference other than
    /// null in a static field, a field of an object, an element of an array or through a ref (<see cref="HandlerStores"/>).
    /// </summary>
    public static readonly Unsupported ReferenceStoreInHandler = Rule(
        "an interrupt handler cannot store an object (a reference other than null) in a static field, an object or an " +
        "array",
        "The garbage collector may be in the middle of a collection when the interrupt comes, and runs on after it: an " +
        "object moved from where it has not looked yet to where it has looked already would be freed while still in " +
        "use. Read objects in the handler, store numbers and bools (in volatile static fields, or in the objects' " +
        "fields), set references to null; let Main store the objects.");
}
