using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using ChipSharp.Frontend;
using ChipSharp.Frontend.Il;
using ChipSharp.Image.Uf2;
using ChipSharp.Runtime.Atmega328p;
using ChipSharp.Runtime.Pfs154;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Runtime.Stm8s103;
using ChipSharp.Targets;
using ChipSharp.Tests.Avr;
using ChipSharp.Tests.Pico;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.Contract;

/// <summary>
/// The hardware contract kept in step: its C header (src/ChipSharp.Runtime.Shared/chipsharp_hardware.h) and its C# list
/// (ChipSharp.Abstractions' HardwareContract) say the same; the frontend maps ChipSharp.Hardware's calls, and
/// ChipSharp.Library's runtime functions are, the contract's; each chip's runtime ships the header, includes it in its C
/// and defines every function its target has (linked, so the symbols are checked too); and the header compiles for
/// every compiler a chip is built with.
/// </summary>
public sealed partial class HardwareContractTests : IDisposable
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public void The_header_declares_the_functions_of_the_CSharp_list_in_its_order_with_their_types_and_guards()
    {
        var declared = HardwareHeader.Functions(HardwareHeader.Text);

        Assert.Equal(HardwareContract.Functions.Select(f => f.Name), declared.Select(f => f.Name));
        Assert.All(HardwareContract.Functions.Zip(declared), pair =>
        {
            var (function, header) = pair;
            Assert.Equal((function.Name, function.Returns, function.Section, function.Guard, function.Reentrant, function.NoReturn),
                (header.Name, header.Returns, header.Section, header.Guard, header.Reentrant, header.NoReturn));
            Assert.Equal(function.Parameters, header.Parameters);
        });
        Assert.Equal(58, declared.Count);
    }

    [Fact]
    public void A_function_of_a_double_is_declared_only_where_C_has_a_64_bit_double()
    {
        var text = HardwareHeader.Text.ReplaceLineEndings("\n");
        var doubles = HardwareContract.Functions.Where(f => f.NeedsDouble64).Select(f => f.Name).ToList();

        Assert.Equal(["ChipSharp_Runtime_AdcTemperature", "ChipSharp_Runtime_AdcReference", "ChipSharp_Runtime_PwmDuty"], doubles);
        var guarded = Regex.Matches(text, @"#if CHIPSHARP_DOUBLE64\n(?<body>.*?)#endif", RegexOptions.Singleline)
            .SelectMany(m => HardwareHeader.Functions("// ---- Doubles (chip) ----\n" + m.Groups["body"].Value))
            .Select(f => f.Name);
        Assert.Equal(doubles, guarded);
    }

    [Fact]
    public void Every_call_of_ChipSharp_Hardware_the_frontend_maps_is_a_function_of_the_contract_and_back()
    {
        var hardware = KnownExternals.All
            .Where(f => f.LibraryMethod.StartsWith("ChipSharp.Hardware.", StringComparison.Ordinal)).ToList();

        Assert.Equal(19, hardware.Count);
        Assert.All(hardware, external =>
        {
            var function = HardwareContract.ForCSharp(external.LibraryMethod);
            Assert.True(function is not null, $"{external.LibraryMethod} is not in the contract");
            Assert.Equal(function!.ExternalName, external.DisplayName);
            Assert.Equal(function.TirReturns(), external.ReturnType);
            Assert.Equal(function.TirParameters(), external.ParameterTypes);
            Assert.Equal(HardwareFunctionKind.Runtime, function.Kind);
        });
        Assert.Equal(HardwareContract.Functions.Where(f => f.CSharp?.StartsWith("ChipSharp.Hardware.", StringComparison.Ordinal) == true)
                .Select(f => f.CSharp).Order(StringComparer.Ordinal),
            hardware.Select(f => f.LibraryMethod).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_runtime_function_of_the_librarys_hardware_and_clock_is_a_function_of_the_contract_and_back()
    {
        // ChipRuntime: the buses, serial ports, analog inputs, PWM and the console's input; Runtime: the clock
        var chip = PublicStatic(LibrarySubstitutes.ChipRuntimeType);
        var clock = PublicStatic(LibrarySubstitutes.RuntimeType)
            .Where(m => m.Name.StartsWith("Clock", StringComparison.Ordinal) || m.Name == "LocalUtcOffset");
        List<IlMethod> methods = [.. chip, .. clock];

        Assert.Equal(23, chip.Count);
        Assert.All(methods, method =>
        {
            var csharp = $"{method.DeclaringType.Type.FullName}.{method.Name}";
            var function = HardwareContract.ForCSharp(csharp);
            Assert.True(function is not null, $"{csharp} is not in the contract");
            Assert.True(LibrarySubstitutes.TryMapRuntimeFunction(
                new IlMethodReference(method.DeclaringType.Type, method.Name, method.Signature, [], null), out var external));
            Assert.Equal(function!.ExternalName, external!.DisplayName);
            Assert.Equal(function.TirReturns(), external.ReturnType);
            Assert.Equal(function.TirParameters(), external.ParameterTypes);
        });
        Assert.Equal(
            HardwareContract.Functions.Where(f => f.CSharp?.StartsWith("ChipSharp.Library.", StringComparison.Ordinal) == true)
                .Select(f => f.CSharp).Order(StringComparer.Ordinal),
            methods.Select(m => $"{m.DeclaringType.Type.FullName}.{m.Name}").Order(StringComparer.Ordinal));
    }

    /// <summary>The public static methods of one of ChipSharp.Library's runtime classes.</summary>
    private static List<IlMethod> PublicStatic(string type) => [.. LibrarySubstitutes.Assembly.FindType(type)!.Methods
        .Where(m => m.IsStatic && (m.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public)];

    [Fact]
    public void A_target_has_the_functions_of_the_sections_it_has_the_guards_of()
    {
        static List<string> Sections(TargetDescription target) =>
            [.. HardwareContract.For(target).Select(f => f.Section).Distinct()];

        string[] every = ["Start-up and the program", "Pins", "Delays", "Registers", "Time zones", "Failure output and the stops"];
        Assert.Equal(
            [
                "Start-up and the program", "Pins", "Delays", "Registers", "The timer event", "The pin event", "The clock",
                "Time zones", "The console", "The console's input", "Serial ports", "The I2C buses", "The SPI buses",
                "Analog inputs", "PWM outputs", "Failure output and the stops",
            ],
            Sections(Targets.Load("pico")));
        Assert.Equal(Sections(Targets.Load("pico")), Sections(Targets.Load("uno")));

        // the SDCC chips, as their files say today: the PFS154 its events, its clock and PWM, the STM8S103 all the Pico's
        // and the Uno's sections; neither has a 64-bit double
        Assert.Equal(
            [
                "Start-up and the program", "Pins", "Delays", "Registers", "The timer event", "The pin event", "The clock",
                "Time zones", "PWM outputs", "Failure output and the stops",
            ],
            Sections(Targets.Load("pfs154-bare")));
        Assert.DoesNotContain(HardwareContract.For(Targets.Load("pfs154-bare")), f => f.NeedsDouble64);
        Assert.All(every, section => Assert.Contains(section, Sections(Targets.Load("pfs154-bare"))));
        var stm8 = HardwareContract.For(Targets.Load("stm8s103-board"));
        Assert.Equal(Sections(Targets.Load("pico")), stm8.Select(f => f.Section).Distinct());
        Assert.DoesNotContain(stm8, f => f.NeedsDouble64);
        Assert.Contains(HardwareContract.For(Targets.Load("uno")), f => f.NeedsDouble64);
        Assert.DoesNotContain(HardwareContract.For(Targets.Load("pico")), f => f.Kind == HardwareFunctionKind.Program);
    }

    [Theory]
    [InlineData("pico")]
    [InlineData("uno")]
    [InlineData("pfs154-bare")]
    [InlineData("stm8s103-board")]
    [InlineData("esp32c5-devkitc")]
    public void A_chip_runtime_ships_the_header_and_each_C_file_that_defines_a_function_of_the_contract_includes_it(
        string name)
    {
        var target = Targets.Load(name);
        var files = RuntimeFiles(target);

        Assert.Equal(HardwareHeader.Text.ReplaceLineEndings("\n"),
            Assert.Single(files, f => f.FileName == HardwareContract.HeaderFileName).Content.ReplaceLineEndings("\n"));
        var defining = files.Where(f => f.FileName.EndsWith(".c", StringComparison.Ordinal)
            && HardwareContract.Functions.Any(function => Defines(f.Content, function.Name))).ToList();
        Assert.NotEmpty(defining);
        Assert.All(defining, file => Assert.True(file.Content.Contains($"#include \"{HardwareContract.HeaderFileName}\"",
            StringComparison.Ordinal), $"{file.FileName} defines a function of the contract but does not include it"));
        // the shared files and compiler-rt's stay as they were: only the chip's and architecture's own include it
        Assert.All(files.Where(f => f.FileName.StartsWith("chipsharp_", StringComparison.Ordinal) && f.FileName.EndsWith(".c",
            StringComparison.Ordinal)), file => Assert.DoesNotContain("#include", file.Content));
    }

    [PicoFact]
    public void The_Picos_runtime_defines_every_function_its_target_has_with_the_contracts_types()
    {
        var pico = Targets.Load("pico").WithoutHeap();

        var elf = Link(pico, new ClangToolchain(new ClangToolchainOptions
        {
            OutputFolder = _scratch, ProgramName = "Contract", UseCache = false,
        }));

        var symbols = ElfFile.Read(elf).Symbols;
        Assert.All(Referenced(pico), name => Assert.True(symbols.ContainsKey(name), $"{name} is not in the program"));
    }

    [AvrFact]
    public void The_Unos_runtime_defines_every_function_its_target_has_with_the_contracts_types()
    {
        var uno = Targets.Load("uno");

        var elf = Link(uno, new ClangToolchain(new ClangToolchainOptions
        {
            Driver = ClangDriver.Zig, OutputFolder = _scratch, ProgramName = "Contract", UseCache = false,
        }));

        var symbols = ElfFile.Read(elf).Symbols;
        Assert.All(Referenced(uno), name => Assert.True(symbols.ContainsKey(name), $"{name} is not in the program"));
    }

    [SdccFact]
    public void The_STM8S103s_runtime_defines_every_function_its_target_has_with_the_contracts_types()
    {
        // all of them at once do not fit in the STM8S103F3's 8 KB (a program has only those it uses): a board of the
        // chip with the 32 KB the STM8's code addresses reach, for the link alone
        var targets = Path.Combine(_scratch, "targets");
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "targets"), "*.json",
            SearchOption.AllDirectories))
        {
            var copy = Path.Combine(targets, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "targets"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        File.WriteAllText(Path.Combine(targets, "board", "stm8s103-contract.json"), """
            { "name": "stm8s103-contract", "inherits": "stm8s103-board", "memory": { "flash": { "size": "32K" } } }
            """);
        var stm8 = new TargetLoader(targets).Load("stm8s103-contract");

        var ihx = Link(stm8, new SdccToolchain(new SdccToolchainOptions
        {
            OutputFolder = _scratch, ProgramName = "Contract", UseCache = false,
        }));

        // SDCC's map names each symbol with a '_' first, cut at 32 characters, and the module it is in (and sdcc fails a
        // program that uses a symbol nothing defines)
        var map = File.ReadAllText(Path.ChangeExtension(ihx, ".map"));
        Assert.All(Referenced(stm8), name => Assert.Matches($@"\s{Regex.Escape(("_" + name)[..Math.Min(32, name.Length + 1)])}\s",
            map));
    }

    [SdccFact]
    public void The_PFS154s_runtime_defines_every_function_its_target_has()
    {
        var target = Targets.Load("pfs154-bare");
        var names = Referenced(target);

        // all of them take more than its 2K words of program memory: half a program
        List<List<string>> halves = [[.. names.Take(names.Count / 2)], [.. names.Skip(names.Count / 2)]];
        for (var i = 0; i < halves.Count; i++)
        {
            var folder = Path.Combine(_scratch, $"pfs154-{i}");
            // the events' handlers, which the runtime's files of the events call
            var program = new StringBuilder()
                .Append("#include \"").Append(HardwareContract.HeaderFileName).Append("\"\n\n")
                .Append(string.Concat(Interrupts.ForTarget(target).Values.Select(e => $"void {e.Symbol}(void)\n{{\n}}\n\n")))
                .Append("chipsharp_i32 chipsharp_entry(void)\n{\n    return 0;\n}\n")
                .ToString();
            // and a goto to each function (SDCC's pdk14 port takes no table of their addresses in C), never run
            var references = new StringBuilder("    .module contract_references\n    .area CODE\n_contract_references::\n")
                .Append(string.Concat(halves[i].Select(name => $"    goto _{name}\n")))
                .ToString();

            new SdccToolchain(new SdccToolchainOptions { OutputFolder = folder, ProgramName = "Contract", UseCache = false })
                .Build(new GeneratedCode(
                [
                    .. RuntimeFiles(target), new GeneratedFile("contract.c", program),
                    new GeneratedFile("contract_references.s", references),
                ]), target);

            // the linker fails a program whose symbol nothing defines; and each is in the program
            var map = File.ReadAllText(Path.Combine(folder, "Contract.map"));
            Assert.All(halves[i], name => Assert.Contains("_" + name[..Math.Min(name.Length, 31)], map));
        }
    }

    [SdccFact]
    public void The_header_compiles_with_SDCC_for_the_Padauk_and_STM8_chips_with_reentrant_functions_on_the_Padauk()
    {
        foreach (var target in new[] { SdccPrograms.Pfs154, SdccPrograms.Stm8s103 })
        {
            var folder = Path.Combine(_scratch, target.Name);
            var code = new GeneratedCode(
            [
                new GeneratedFile(HardwareContract.HeaderFileName, HardwareHeader.Text),
                new GeneratedFile("contract.c", $$"""
                    #include "{{HardwareContract.HeaderFileName}}"

                    static volatile chipsharp_i32 last;

                    void ChipSharp_Runtime_PinToggle(chipsharp_i32 pin) CHIPSHARP_REENTRANT
                    {
                        last = pin;
                    }

                    void ChipSharp_Runtime_DelayMilliseconds(chipsharp_i32 milliseconds)
                    {
                        last = milliseconds;
                    }

                    chipsharp_i32 chipsharp_entry(void)
                    {
                        ChipSharp_Runtime_PinToggle(13);
                        ChipSharp_Runtime_DelayMilliseconds(sizeof(chipsharp_i32) == 4 && sizeof(chipsharp_i64) == 8 ? 1 : -1);
                        return last;
                    }

                    void main(void)
                    {
                        chipsharp_entry();
                    }
                    """),
            ]);

            new SdccToolchain(new SdccToolchainOptions { OutputFolder = folder, ProgramName = "Contract", UseCache = false })
                .Build(code, target);

            // SDCC's Padauk ports keep a reentrant function's parameters on the stack, the others' at fixed addresses
            var assembly = File.ReadAllText(Path.Combine(folder, "contract.c.asm"));
            var pdk = target.Setting("sdcc.port") == "pdk14";
            Assert.Equal(pdk, !assembly.Contains("_ChipSharp_Runtime_PinToggle_PARM_1", StringComparison.Ordinal)
                && assembly.Contains("_ChipSharp_Runtime_DelayMilliseconds_PARM_1", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_header_compiles_for_Arm_and_RISC_V_with_clang_strictly()
    {
        if (PicoTools.MissingLlvm.Value is not null)
        {
            return;
        }

        Directory.CreateDirectory(_scratch);
        File.WriteAllText(Path.Combine(_scratch, HardwareContract.HeaderFileName), HardwareHeader.Text);
        File.WriteAllText(Path.Combine(_scratch, "contract.c"), $"#include \"{HardwareContract.HeaderFileName}\"\n" +
            "_Static_assert(sizeof(chipsharp_i32) == 4 && sizeof(chipsharp_u32) == 4 && sizeof(chipsharp_i64) == 8 " +
            "&& sizeof(chipsharp_f64) == 8 && sizeof(chipsharp_u8) == 1, \"the contract's types\");\n");
        foreach (var triple in (string[])["thumbv6m-none-eabi", "riscv32-unknown-elf", "x86_64-pc-windows-msvc"])
        {
            var (exitCode, output) = Run(new ClangToolchain().FindClang(),
                [$"--target={triple}", "-ffreestanding", "-std=c11", "-Wall", "-Wextra", "-pedantic", "-Werror",
                    "-fsyntax-only", "contract.c"], _scratch);
            Assert.True(exitCode == 0, $"{triple}: {output}");
        }
    }

    [Fact]
    public void The_C_code_generator_declares_the_runtimes_functions_reentrant_where_the_contract_does_on_the_Padauk()
    {
        const string source = """
            using ChipSharp.Hardware;

            Pin.Output(4);
            while (true)
            {
                Pin.Toggle(4);
                Timing.DelayMilliseconds(500);
            }
            """;
        string Generated(string target)
        {
            var description = Targets.Load(target);
            var bytes = ChipSharp.Tests.Support.CSharpCompiler.Compile([source], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], "Blink");
            var module = ChipSharp.Frontend.Lowering.IlToTir.Lower(ChipSharp.Frontend.Analysis.WholeProgramPass.Analyze(
                IlAssembly.Load(bytes, "Blink.dll"), description));
            return Assert.Single(new ChipSharp.Backend.C.CCodeGenerator().Generate(module, description).Files).Content;
        }

        // SDCC's pdk14 keeps a function's parameters at fixed addresses unless it is __reentrant: the program's
        // prototype must say what the runtime's definition (CHIPSHARP_REENTRANT) does, or the call passes them wrong
        var padauk = Generated("pfs154-bare");
        Assert.Matches(@"void ChipSharp_Runtime_PinToggle\(u?int32_t \w+\) CS_REENTRANT;", padauk);
        Assert.Matches(@"void ChipSharp_Runtime_DelayMilliseconds\(u?int32_t \w+\);", padauk);
        Assert.Contains("#define CS_REENTRANT __reentrant", padauk);
        Assert.Matches(@"void ChipSharp_Runtime_PinToggle\(u?int32_t \w+\);", Generated("stm8s103-board"));
    }

    /// <summary>
    /// A program that references every function <paramref name="target"/>'s runtime must define, and every clear
    /// function its interrupts table names, linked with the runtime: a function missing, or defined with other types than
    /// the header's, fails the build (the C of the runtime includes the header).
    /// </summary>
    private string Link(TargetDescription target, IToolchain toolchain)
    {
        var names = Referenced(target);
        var program = new StringBuilder()
            .Append("#include \"").Append(HardwareContract.HeaderFileName).Append("\"\n\n")
            .Append(string.Concat(Interrupts.ForTarget(target).Values.Where(i => i.Clear is not null)
                .Select(i => $"void {i.Clear}(void);\n")))
            .Append("typedef void (*function)(void);\n\nstatic volatile function const used[] =\n{\n")
            .Append(string.Concat(names.Select(name => $"    (function){name},\n")))
            .Append("};\n\nchipsharp_i32 chipsharp_entry(void)\n{\n    chipsharp_i32 count = 0;\n")
            .Append("    for (unsigned int i = 0; i < sizeof used / sizeof used[0]; i++)\n    {\n")
            .Append("        count += used[i] != 0;\n    }\n\n    return count;\n}\n")
            .ToString();
        var code = new GeneratedCode([.. RuntimeFiles(target), new GeneratedFile("contract.c", program)]);
        return toolchain.Build(code, target).Path;
    }

    /// <summary>The functions of the contract <paramref name="target"/> has, and the clear functions of its events.</summary>
    private static List<string> Referenced(TargetDescription target) =>
    [
        .. HardwareContract.For(target).Select(f => f.Name),
        .. Interrupts.ForTarget(target).Values.Select(i => i.Clear).OfType<string>(),
    ];

    /// <summary>Every file of the target's runtime, as a build takes them.</summary>
    private static List<GeneratedFile> RuntimeFiles(TargetDescription target)
    {
        var runtime = target.Runtime switch
        {
            "rp2040" => (IArchRuntime)new Rp2040Runtime(),
            "atmega328p" => new Atmega328pRuntime(),
            "pfs154" => new Pfs154Runtime(),
            "stm8s103f3" => new Stm8s103Runtime(),
            _ => throw new InvalidOperationException(target.Runtime),
        };
        return [.. runtime.Startup(target).Files, .. runtime.InterruptEntry(target).Files, .. runtime.Atomics(target).Files];
    }

    /// <summary>Runs a tool in <paramref name="folder"/>: its exit code, and what it wrote.</summary>
    private static (int ExitCode, string Output) Run(string tool, IReadOnlyList<string> arguments, string folder)
    {
        var start = new ProcessStartInfo(tool)
        {
            WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result + errors);
    }

    /// <summary>True when <paramref name="c"/> defines the function <paramref name="name"/> (not only declares it).</summary>
    private static bool Defines(string c, string name) =>
        Regex.IsMatch(c.ReplaceLineEndings("\n"), $@"\b{Regex.Escape(name)}\([^;{{]*\)[^;{{]*\n?\{{");
}
