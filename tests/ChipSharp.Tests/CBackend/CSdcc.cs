using System.Diagnostics;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Backend.C;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.CBackend;

/// <summary>
/// The SDCC chips for the c code generator's tests: the shipped PFS154 (pdk14) and STM8S103 (stm8) boards, the C it
/// writes for them, compiled by sdcc on its own (to see its warnings) or linked by the toolchain "sdcc" with a stand-in
/// runtime.
/// </summary>
internal static class CSdcc
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    /// <summary>The PFS154 on its own: pdk14, 128 bytes of RAM, functions __reentrant only when asked.</summary>
    public static TargetDescription Pfs154 => Shipped.Load("pfs154-bare");

    /// <summary>The STM8S103 board: stm8, big endian.</summary>
    public static TargetDescription Stm8s103 => Shipped.Load("stm8s103-board");

    /// <summary>
    /// <paramref name="target"/> with its capability <paramref name="name"/> set to <paramref name="value"/>, e.g. a
    /// console, so that the samples that print compile for a chip whose runtime has none yet.
    /// </summary>
    public static TargetDescription With(TargetDescription target, string name, string value) => new()
    {
        Name = target.Name,
        InheritanceChain = target.InheritanceChain,
        Backend = target.Backend,
        Toolchain = target.Toolchain,
        Runtime = target.Runtime,
        Triple = target.Triple,
        Cpu = target.Cpu,
        PointerSize = target.PointerSize,
        Endian = target.Endian,
        Image = target.Image,
        Flasher = target.Flasher,
        Memory = target.Memory,
        Pins = target.Pins,
        Capabilities = new Dictionary<string, string>(target.Capabilities, StringComparer.OrdinalIgnoreCase)
        {
            [name] = value,
        },
        HeapSize = target.HeapSize,
        Settings = target.Settings,
        MissingValues = target.MissingValues,
    };

    /// <summary>
    /// The C of the C# program <paramref name="source"/> for <paramref name="target"/>; null when the whole-program pass
    /// does not take it for the target (it needs a heap, say).
    /// </summary>
    public static (TirModule Module, string C)? Generate(string source, string name, TargetDescription target)
    {
        var dll = CSharpCompiler.Compile(source, name);
        var assembly = IlAssembly.Load(dll, name + ".dll");
        var analysis = WholeProgramPass.Check(assembly, target);
        if (!analysis.IsSupported)
        {
            return null;
        }

        var module = IlToTir.Lower(analysis.ToProgram());
        return (module, Assert.Single(new CCodeGenerator().Generate(module, target).Files).Content);
    }

    /// <summary>The C of the C# program <paramref name="source"/> for <paramref name="target"/>; fails the test, saying why, when there is none.</summary>
    public static (TirModule Module, string C) Required(string source, string name, TargetDescription target) =>
        Generate(source, name, target) ?? throw new Xunit.Sdk.XunitException(
            WholeProgramPass.Check(IlAssembly.Load(CSharpCompiler.Compile(source, name), name + ".dll"), target).Summary);

    /// <summary>
    /// Compiles <paramref name="c"/> with sdcc for <paramref name="target"/>'s port and flags in
    /// <paramref name="folder"/>; returns what sdcc said, and fails the test when it fails.
    /// </summary>
    public static async Task<string> CompileAsync(string c, string fileName, TargetDescription target, string folder)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, fileName), c);
        var sdcc = new SdccToolchain().FindSdcc();
        var flags = target.Settings["sdcc"]?["flags"]?.AsArray().Select(flag => flag!.GetValue<string>()) ?? [];
        var start = new ProcessStartInfo(sdcc)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in (string[])[$"-m{target.Setting("sdcc.port")}", .. flags, "-c", fileName])
        {
            start.ArgumentList.Add(argument);
        }

        // SDCC's preprocessor finds its cc1 on the PATH
        start.Environment["PATH"] = Path.GetDirectoryName(sdcc) + Path.PathSeparator +
            (start.Environment.TryGetValue("PATH", out var path) ? path : "");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var messages = (await output + await errors).Trim();
        Assert.True(process.ExitCode == 0, $"sdcc failed on {fileName} in {folder}:\n{messages}");
        return messages;
    }

    /// <summary>
    /// A stand-in runtime for linking <paramref name="module"/>'s C: main(), which runs the program and keeps its result
    /// where a debugger sees it, and each external function the program calls, doing nothing.
    /// </summary>
    public static string Runtime(TirModule module, TargetDescription target)
    {
        var types = new CTypes(new TirLayout(target.PointerSize, 1));
        types.Name(module, new HashSet<string>(StringComparer.Ordinal));
        var text = new StringBuilder("#include <stdint.h>\n#include <stddef.h>\n\nint chipsharp_entry(void);\n\n");
        foreach (var function in module.Functions.Where(f => f.Kind == TirFunctionKind.External))
        {
            var symbol = CText.Symbol(function.Name);
            if (symbol is "memset" or "memcpy" or "memmove")
            {
                continue; // SDCC's library has them
            }

            var names = function.Parameters.Select((_, i) => "p" + i).ToList();
            text.Append(CFunctionWriter.Signature(function, types, symbol, names, reentrant: false)).Append("\n{\n");
            foreach (var name in names)
            {
                text.Append("    (void)").Append(name).Append(";\n");
            }

            if (function.ReturnType != TirType.Void)
            {
                text.Append("    return 0;\n");
            }

            text.Append("}\n\n");
        }

        text.Append("volatile int result;\n\nvoid main(void)\n{\n    result = chipsharp_entry();\n    for (;;)\n    {\n    }\n}\n");
        return text.ToString();
    }

    /// <summary>Links the C of <paramref name="source"/> for <paramref name="target"/> with the stand-in runtime; returns the .ihx.</summary>
    public static string Link(string source, string name, TargetDescription target, string folder) =>
        Link(Required(source, name, target).Module, target, folder);

    /// <summary>Links the C of <paramref name="module"/> for <paramref name="target"/> with the stand-in runtime; returns the .ihx.</summary>
    public static string Link(TirModule module, TargetDescription target, string folder)
    {
        var c = Assert.Single(new CCodeGenerator().Generate(module, target).Files);
        var binary = new SdccToolchain(new SdccToolchainOptions
            {
                OutputFolder = folder,
                ProgramName = module.Name,
                UseCache = false,
            })
            .Build(new GeneratedCode([c, new GeneratedFile("runtime.c", Runtime(module, target))]), target);
        return binary.Path;
    }
}
