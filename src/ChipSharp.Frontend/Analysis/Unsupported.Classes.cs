namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// What of classes (see <see cref="ClassRules"/>) does not compile yet, in C# words. The features that come with the
/// next pieces of M3 name the piece: stored strings (object's ToString) and generics over classes. Like
/// everything that makes objects, they need a heap.
/// </summary>
internal sealed partial record Unsupported
{
    /// <summary>The <see cref="Arrival"/> of what returns or keeps a string: ToString.</summary>
    private const string ArrivesWithStoredStrings = "with M3 (stored strings)";

    /// <summary>The <see cref="Arrival"/> of generic classes.</summary>
    private const string WithGenericClasses = "with M3 (generics over classes)";

    /// <summary>
    /// An object's ToString (<c>node.ToString()</c>, and an object printed, which .NET prints as its ToString gives it):
    /// it makes a string. Its Equals and GetHashCode compile (see <see cref="VirtualMethods"/>).
    /// </summary>
    public static readonly Unsupported ObjectMethods = Coming("ToString of objects (and printing an object)", false,
        "For now, give the class a method that prints what it holds, such as Print(), and call that.",
        ArrivesWithStoredStrings);

    public static readonly Unsupported GenericClasses = Coming("generic classes", true,
        "For now, write a class for each type argument you need, or make it a generic struct.", WithGenericClasses);

    public static readonly Unsupported ObjectReferences = Heap("references to objects (ref, out and in of a class, object or string)",
        true, "For now, return the object, or keep it in a field.");

    public static readonly Unsupported Finalizers = new("finalizers (~ClassName())", true,
        "For now, give the class a method that lets go of what it holds, and call it when the program is done with the object.");

    /// <summary>
    /// A rule: an interrupt handler, or a method it calls, makes an object (<see cref="ClassRules.Allocates"/>). The
    /// runtime stops such a program with the same words; this finds it before it runs.
    /// </summary>
    public static readonly Unsupported AllocationInHandler = Rule("an interrupt handler cannot create objects (new ...)",
        "The heap may be in the middle of an allocation or a collection when the interrupt comes: create what the " +
        "handler needs before, e.g. in a static field.");

    /// <summary>A class of the program derived from a library class other than object, e.g. "System.Attribute".</summary>
    public static Unsupported LibraryBaseClass(string name) => new($"classes derived from the library class {name}", true,
        "For now, derive the class from object or from a class of your own.");

    /// <summary>A <c>new</c> of a library type ChipSharp does not have, e.g. "System.Random".</summary>
    public static Unsupported LibraryType(string name) => new($"the library type {name}", false, "") { IsLibraryMethod = true };

    /// <summary>True for a feature of classes that comes with a later piece of M3 than classes: it needs a heap too.</summary>
    private bool ComesWithLaterClasses => Arrival is ArrivesWithStoredStrings or WithGenericClasses;

    private static Unsupported Coming(string what, bool isPlural, string hint, string arrival) =>
        new(what, isPlural, hint) { Arrival = arrival };
}
