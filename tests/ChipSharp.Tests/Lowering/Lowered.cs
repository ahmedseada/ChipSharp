using ChipSharp.Frontend;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Tests.Support;
using Microsoft.CodeAnalysis;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// C# compiled with Roslyn, read back as IL and translated to TIR. The program is made here (the entry point, then
/// every method of the assembly it calls, in the order first called, and the initializers of the types they use), so
/// the tests do not need the whole-program pass.
/// </summary>
internal static class Lowered
{
    /// <summary>Compiles <paramref name="source"/> and reads it back; the file is called "&lt;assemblyName&gt;.dll".</summary>
    public static IlAssembly Compile(string source, string assemblyName = "Test") =>
        IlAssembly.Load(CSharpCompiler.Compile(source, assemblyName), assemblyName + ".dll");

    /// <summary>
    /// Compiles a program the way <c>dotnet build</c> does in Debug: unoptimized IL, with nops, every value in a
    /// local, bools kept in locals before each branch, and so on. Reads it back like <see cref="Compile"/>.
    /// </summary>
    public static IlAssembly CompileDebug(string source, string assemblyName) =>
        IlAssembly.Load(
            CSharpCompiler.Compile(source, assemblyName, OutputKind.ConsoleApplication, OptimizationLevel.Debug),
            assemblyName + ".dll");

    /// <summary>
    /// The program that starts at <paramref name="entryPoint"/> (a display name such as "C.Run"), or at the
    /// assembly's own entry point when it is null.
    /// </summary>
    public static IlProgram Program(IlAssembly assembly, string? entryPoint = null)
    {
        var start = entryPoint is null
            ? assembly.EntryPoint ?? throw new InvalidOperationException("The assembly has no entry point.")
            : Assert.Single(assembly.FindMethods(entryPoint));
        var methods = new List<IlMethod> { start };
        var dispatch = new VirtualDispatch();
        for (var i = 0; i < methods.Count; i++)
        {
            // the initializers of the types it uses, as the whole-program pass adds them
            foreach (var initializer in StaticInitialization.InitializersRunBy(methods[i], isStart: i == 0))
            {
                if (!methods.Contains(initializer))
                {
                    methods.Add(initializer);
                }
            }

            // a virtual call reaches what its slot runs on the objects the program makes, as in the whole-program pass
            var instructions = methods[i].Body?.Instructions ?? [];
            for (var j = 0; j < instructions.Count; j++)
            {
                // a delegate type's own methods are the runtime's; a delegate of a virtual method reaches it through its slot
                if (instructions[j].Operand is IlMethodOperand { Method.Definition: { } callee } && !methods.Contains(callee)
                    && VirtualMethods.CalledSlot(instructions, j) is null && Delegates.LoadedSlot(instructions, j) is null
                    && !Delegates.IsDelegateMethod(callee) && !Frontend.Generics.Instantiations.IsGeneric(callee))
                {
                    methods.Add(callee);
                }

                methods.AddRange(dispatch.Follow(instructions, j, methods[i].Assembly).Where(method => !methods.Contains(method)));
            }
        }

        return new IlProgram(assembly, methods);
    }

    /// <summary>Compiles, translates and verifies a program; see <see cref="Program"/> for <paramref name="entryPoint"/>.</summary>
    public static TirModule Module(string source, string? entryPoint = null, string assemblyName = "Test") =>
        Lower(Program(Compile(source, assemblyName), entryPoint));

    /// <summary>Translates <paramref name="program"/> and checks that the result is well-formed TIR.</summary>
    public static TirModule Lower(IlProgram program)
    {
        var module = IlToTir.Lower(program);
        TirVerifier.Verify(module);
        return module;
    }

    /// <summary>The TIR of one method, as the printer writes it, in a program that starts there.</summary>
    public static string Function(string source, string method) =>
        TirPrinter.Print(Assert.Single(Module(source, method).Functions, f => f.Name == method));
}
