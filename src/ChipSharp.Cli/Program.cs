// chipsharp: the command line. It builds programs, and shows what they go through on the way:
//
//   chipsharp build <assembly.dll>   compiles the program for a target; prints the file it wrote (.exe, .uf2, ...)
//                                    and how much of the chip's flash and RAM the program takes
//   chipsharp flash <assembly.dll | image>
//                                    puts the program onto a board with its target's flasher (the Uno: avrdude, through
//                                    its USB port; the Pico: a copy to its RPI-RP2 drive); a .dll is built first, as
//                                    build does, an image (.hex, .uf2) is flashed as it is
//   chipsharp check <assembly.dll>   whether ChipSharp can compile the program, and the methods it uses; if it
//                                    cannot, the C# features it uses that ChipSharp does not have yet, each with
//                                    where, when it is planned and what to do for now
//   chipsharp tir <assembly.dll>     the program's TIR, after the whole-program pass and the translation
//   chipsharp il <assembly.dll>      the IL of every method, ildasm style
//   chipsharp targets list           every target
//   chipsharp targets show <name>    one target, with everything it inherits merged in
//
// Options: -t, --target <name>    with build: the target to build for (default: host); with flash: the board (needed);
//                                with check and tir: check the program for it: what it has (a heap, a console) and its
//                                interrupts (with tir, put the handlers in them)
//          --port <port>          with flash: where the board is, a serial port (COM5) for avrdude, the boot loader's
//                                 drive (E:\) for uf2-copy (default: the one there is that looks like the board's)
//          -o, --output <folder>  with build and flash: where to write the program (default: chipsharp-output\<target>)
//          --keep                 with build: also write the program's TIR and generated code (.ll) there
//          --verify-heap          with build: check the whole heap after every garbage collection, and stop the
//                                 program saying what is wrong when it is corrupted
//          --no-cache             with build: compile everything, not taking what was compiled before from the build
//                                 cache (%LOCALAPPDATA%\ChipSharp\cache, or $env:CHIPSHARP_CACHE), nor keeping it there
//          --quiet-notes          with build and check: leave out the notes (where .NET's text depends on the PC's
//                                 language settings and ChipSharp's does not), which are written to standard error
//          --details              with check: every place ChipSharp cannot compile on a line of its own, not by
//                                 feature, and every library method it can call, with its parameters; every note too
//          --method <name>        with il and tir: only the methods with this display name, e.g. Program.Main
//          --targets <folder>     (default: $env:CHIPSHARP_TARGETS, then the targets folder next to chipsharp.exe)

using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;
using ChipSharp.Targets;

try
{
    var rest = new List<string>(args);
    var folder = TakeOption(rest, "--targets")
        ?? Environment.GetEnvironmentVariable(TargetLoader.EnvironmentVariable)
        ?? Path.Combine(AppContext.BaseDirectory, "targets");
    var methodName = TakeOption(rest, "--method");
    var target = TakeOption(rest, "--target", "-t");
    var output = TakeOption(rest, "--output", "-o");
    var keep = TakeFlag(rest, "--keep");
    var verifyHeap = TakeFlag(rest, "--verify-heap");
    var details = TakeFlag(rest, "--details");
    var noCache = TakeFlag(rest, "--no-cache");
    var quietNotes = TakeFlag(rest, "--quiet-notes");
    var port = TakeOption(rest, "--port");

    // build, and flash of a .dll: builds the program for the target, prints the file it wrote (and those kept) and how
    // much of the chip it takes; returns the file
    string Build(string path, string targetName)
    {
        var result = ChipSharpBuilder.Build(path, new BuildOptions
        {
            Target = targetName,
            TargetsFolder = folder,
            OutputFolder = output,
            KeepIntermediates = keep,
            VerifyHeap = verifyHeap,
            UseCache = !noCache,
        });
        if (!quietNotes)
        {
            WriteNotes(result.NoteGroups.Select(group => group.Line));
        }

        Console.WriteLine(result.OutputPath);
        foreach (var file in result.IntermediatePaths)
        {
            Console.WriteLine(file);
        }

        Console.WriteLine(result.Memory.Text);
        return result.OutputPath;
    }

    switch (rest)
    {
        case ["build", var path]:
        {
            Build(path, target ?? BuildOptions.DefaultTarget);
            return 0;
        }

        case ["flash", var path]:
        {
            // the board first: a target with no flasher fails before anything is built
            var targets = new TargetLoader(folder);
            if (target is null)
            {
                throw new ChipSharpException(
                    $"chipsharp flash needs the board to flash, with -t (e.g. -t uno): {ChipSharpFlasher.Boards(targets)}.");
            }

            var board = ChipSharpFlasher.LoadTarget(targets, target);
            var image = string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase)
                ? Build(path, board.Name)
                : path;
            await ChipSharpFlasher.FlashAsync(image, board, new FlashOptions(port), new LinesProgress());
            return 0;
        }

        case ["tir", var path]:
        {
            var module = ChipSharpBuilder.Translate(path, target is null ? null : new TargetLoader(folder).Load(target));
            if (methodName is null)
            {
                Console.Write(TirPrinter.Print(module));
                return 0;
            }

            var functions = module.Functions.Where(f => string.Equals(f.Name, methodName, StringComparison.Ordinal)).ToList();
            if (functions.Count == 0)
            {
                throw new ChipSharpException(IlAssembly.Load(path).FindMethods(methodName).Count > 0
                    ? $"'{methodName}' is in {path} but not in the program: the entry point never calls it, so it is not compiled."
                    : $"{path} has no method named '{methodName}' (write it as Type.Method, e.g. Program.Main).");
            }

            Console.Write(string.Join("\n", functions.Select(TirPrinter.Print)));
            return 0;
        }

        case ["targets", "list"]:
        {
            foreach (var targetName in new TargetLoader(folder).Names)
            {
                Console.WriteLine(targetName);
            }

            return 0;
        }

        case ["targets", "show", var name]:
        {
            Print(new TargetLoader(folder).Load(name));
            return 0;
        }

        case ["il", var path]:
        {
            var assembly = IlAssembly.Load(path);
            if (methodName is null)
            {
                Console.Write(IlPrinter.Print(assembly));
                return 0;
            }

            var methods = assembly.FindMethods(methodName);
            if (methods.Count == 0)
            {
                throw new ChipSharpException($"{path} has no method named '{methodName}' (write it as Type.Method, e.g. Program.Main).");
            }

            Console.Write(IlPrinter.Print(methods));
            return 0;
        }

        case ["check", var path]:
        {
            var checkedFor = target is null ? null : new TargetLoader(folder).Load(target);
            var analysis = WholeProgramPass.Check(IlAssembly.Load(path), checkedFor);
            if (!analysis.IsSupported)
            {
                throw new ChipSharpException(details ? analysis.Details : analysis.Summary);
            }

            var count = analysis.Methods.Count == 1
                ? "1 method"
                : string.Create(CultureInfo.InvariantCulture, $"{analysis.Methods.Count} methods");
            var forTarget = checkedFor is null ? "" : $" for target '{checkedFor.Name}'";
            Console.WriteLine($"{path}: ChipSharp compiles it{forTarget}. The program uses {count}, entry point first:");
            foreach (var method in analysis.Methods)
            {
                var handler = analysis.InterruptHandlers.FirstOrDefault(h => h.Method == method);
                var handles = handler is null ? "" : $" (handles the {handler.Event} event)";
                Console.WriteLine($"  {CSharpNames.Method(method)}{handles}");
            }

            if (!quietNotes)
            {
                WriteNotes(details ? analysis.Notes.Select(note => note.Line) : analysis.NoteGroups.Select(group => group.Line));
            }

            return 0;
        }

        default:
        {
            Console.WriteLine("""
                usage:
                  chipsharp build <assembly.dll> [-t|--target <name>] [-o|--output <folder>] [--keep] [--verify-heap]
                                  [--no-cache] [--quiet-notes]
                  chipsharp flash <assembly.dll | image.hex | image.uf2> -t|--target <board> [--port <port>]
                                  [build's options, for a .dll]
                  chipsharp check <assembly.dll> [--details] [-t|--target <name>] [--quiet-notes]
                  chipsharp tir <assembly.dll> [--method <name>] [-t|--target <name>]
                  chipsharp il <assembly.dll> [--method <name>]
                  chipsharp targets list
                  chipsharp targets show <name>
                options:
                  -t, --target <name>     build: the target to build for (default: host); flash: the board; check,
                                          tir: the target to check the program for: its heap, console and interrupts
                  --port <port>           flash: the board's serial port (avrdude: COM5) or boot drive (uf2-copy:
                                          E:\); by default the one there is that looks like the board's
                  -o, --output <folder>   build, flash: where to write the program (default: chipsharp-output\<target>)
                  --keep                  build: also write the program's TIR (.tir) and generated code (.ll) there
                  --verify-heap           build: check the whole heap after every garbage collection
                  --no-cache              build: compile everything, without the build cache (%LOCALAPPDATA%\ChipSharp\cache,
                                          or set CHIPSHARP_CACHE)
                  --quiet-notes           build, check: leave out the notes on text that .NET reads, writes, compares
                                          or cases by the PC's language settings, and ChipSharp the same everywhere
                  --details               check: list every place ChipSharp cannot compile, not one line per C#
                                          feature, and every library method it can call; every note too
                  --method <name>         tir, il: only the methods with this display name, e.g. Program.Main
                  --targets <folder>      where the target files are (or set CHIPSHARP_TARGETS)
                build finds LLVM's clang in its install folder or on the PATH, or set CHIPSHARP_LLVM_BIN to its bin folder.
                For an AVR (the Arduino Uno: -t uno) it runs the clang inside Zig: set CHIPSHARP_ZIG to zig.exe.
                flash -t uno runs avrdude: from the PATH, the Arduino IDE's, or set CHIPSHARP_AVRDUDE to avrdude.exe.
                """);
            return 2;
        }
    }
}
catch (ChipSharpException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

// Writes notes to standard error, as errors are: "note: " and the note, one a line. Notes do not stop anything.
static void WriteNotes(IEnumerable<string> notes)
{
    foreach (var note in notes)
    {
        Console.Error.WriteLine($"note: {note}");
    }
}

// Removes an option and its value from args; names are its spellings, e.g. --target and -t.
static string? TakeOption(List<string> args, params string[] names)
{
    var i = args.FindIndex(a => names.Contains(a, StringComparer.OrdinalIgnoreCase));
    if (i < 0)
    {
        return null;
    }

    if (i == args.Count - 1)
    {
        throw new ChipSharpException($"{args[i]} needs a value.");
    }

    var value = args[i + 1];
    args.RemoveRange(i, 2);
    return value;
}

// Removes an option that takes no value, e.g. --keep, from args; true when it was there.
static bool TakeFlag(List<string> args, string name) =>
    args.RemoveAll(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) > 0;

static void Print(TargetDescription t)
{
    static void Row(string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Console.WriteLine($"  {label,-13}{value}");
        }
    }

    static string Bytes(ulong n) => n switch
    {
        >= 1UL << 20 when n % (1UL << 20) == 0 => $"{n >> 20} MB",
        >= 1UL << 10 when n % (1UL << 10) == 0 => $"{n >> 10} KB",
        _ => $"{n} bytes",
    };

    Console.WriteLine($"Target {t.Name}   ({string.Join(" -> ", t.InheritanceChain)})");
    Row("backend", t.Backend);
    Row("toolchain", t.Toolchain);
    Row("runtime", t.Runtime);
    Row("triple", t.Triple);
    Row("cpu", t.Cpu);
    Row("pointer size", $"{t.PointerSize} bytes, {t.Endian.ToString().ToLowerInvariant()} endian");
    Row("image", t.Image);
    Row("flasher", t.Flasher);
    if (t.Memory.Count > 0)
    {
        Console.WriteLine("  memory");
        foreach (var (name, region) in t.Memory)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {name,-11}0x{region.Origin:X8}  {Bytes(region.Size)}"));
        }
    }

    Row("capabilities", string.Join(", ", t.Capabilities.Select(c => $"{c.Key}={c.Value}")));
    Row("pins", string.Join(", ", t.Pins.Select(p => $"{p.Key}={p.Value}")));
    Row("interrupts", string.Join(", ", Interrupts.ForTarget(t).Values.Select(i => $"{i.Event}=IRQ {i.Number}")));
    Row("incomplete", t.IsComplete
        ? null
        : $"{string.Join(", ", t.MissingValues)} (a target that inherits this one must set it; build a board instead)");
}

/// <summary>Writes each stage of a flash on a line of its own, as it comes (what runs, then what happened).</summary>
internal sealed class LinesProgress : IProgress<FlashProgress>
{
    public void Report(FlashProgress value) => Console.WriteLine(value.Stage);
}
