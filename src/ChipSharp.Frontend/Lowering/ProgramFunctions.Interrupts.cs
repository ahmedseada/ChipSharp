using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Interrupt handlers (see <see cref="Interrupts"/>): each handler is an ordinary function, and the chip reaches it
/// through an entry function of its own, exported under the symbol the vector table names.
/// </summary>
internal sealed partial class ProgramFunctions
{
    /// <summary>The name of the entry function of the interrupt for <paramref name="event"/>, e.g. "&lt;Interrupt timer&gt;".</summary>
    public static string InterruptEntryName(string @event) => $"<Interrupt {@event}>";

    /// <summary>
    /// For each of <paramref name="program"/>'s interrupt handlers whose interrupt the target gave, a function named
    /// <see cref="InterruptEntryName"/>, exported under the interrupt's symbol (e.g. <c>chipsharp_isr_irq1</c>): it calls
    /// the runtime function that clears the event, when the target names one, then the handler. A program checked
    /// without a target has no interrupts to put its handlers in, so they stay ordinary functions.
    /// </summary>
    public void AddInterruptEntries(IlProgram program)
    {
        foreach (var handler in program.InterruptHandlers)
        {
            if (handler.Interrupt is not { } interrupt)
            {
                continue;
            }

            var entry = module.AddFunction(InterruptEntryName(handler.Event), TirType.Void);
            entry.ExportName = interrupt.Symbol;
            var ir = new TirBuilder(entry.AddBlock("entry"));
            if (interrupt.Clear is { } clear)
            {
                ir.Call(External(new ExternalFunction(clear, TirType.Void, [], $"the {handler.Event} event's {clear}")));
            }

            ir.Call(Get(handler.Method).Function);
            ir.Return();
        }
    }
}
