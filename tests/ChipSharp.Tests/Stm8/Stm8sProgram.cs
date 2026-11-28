using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Simulators;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Stm8;

/// <summary>A C# program built for the blue STM8S103 board: its .hex, and what it takes of the chip's flash and RAM.</summary>
internal sealed record Stm8sProgram(string Hex, MemoryReport Memory)
{
    public static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    public static TargetDescription Board => Shipped.Load("stm8s103-board");

    /// <summary>
    /// The C# program <paramref name="source"/> built for stm8s103-board by the build pipeline, as chipsharp build -t
    /// stm8s103-board does, with the class libraries at <paramref name="libraries"/> (e.g. ChipSharp.Components.dll)
    /// referenced: copied next to the program, where the build finds them.
    /// </summary>
    public static Stm8sProgram Build(string folder, string name, string source, params string[] libraries)
    {
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, name + ".dll");
        File.WriteAllBytes(dll,
            CSharpCompiler.Compile([source], [typeof(ChipSharp.Hardware.Pin).Assembly.Location, .. libraries], name));
        foreach (var library in libraries)
        {
            File.Copy(library, Path.Combine(folder, Path.GetFileName(library)), overwrite: true);
        }

        var result = ChipSharpBuilder.Build(dll, new BuildOptions
        {
            Target = "stm8s103-board",
            TargetsFolder = Path.Combine(AppContext.BaseDirectory, "targets"),
            OutputFolder = Path.Combine(folder, name),
        });
        return new Stm8sProgram(result.OutputPath, result.Memory);
    }

    /// <summary>Runs the program in chipsim's STM8S103 with the command line's options (--input, --send, --i2c, ...).</summary>
    public SimulationResult Run(double milliseconds, params string[] options) => Simulation.Run(SimulationOptions.Parse(
        "stm8s103", [Hex, "--ms", milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture), .. options]));
}
