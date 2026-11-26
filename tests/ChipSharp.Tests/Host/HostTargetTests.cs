using ChipSharp.Abstractions;
using ChipSharp.Runtime.Windows;
using ChipSharp.Targets;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Host;

public sealed class HostTargetTests
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    [Fact]
    public void Host_is_a_complete_windows_x64_target_built_with_clang()
    {
        var host = Targets.Load("host");

        Assert.Equal(["host", "x86_64"], host.InheritanceChain);
        Assert.True(host.IsComplete);
        // from x86_64
        Assert.Equal("llvm", host.Backend);
        Assert.Equal(8, host.PointerSize);
        Assert.Equal(Endianness.Little, host.Endian);
        Assert.True(host.HasCapability("hwMultiply"));
        Assert.True(host.HasCapability("hwDivide"));
        Assert.True(host.HasCapability("fpu"));
        Assert.Equal("full", host.Capabilities["atomics"]);
        // from host itself
        Assert.Equal("clang", host.Toolchain);
        Assert.Equal("windows-x64", host.Runtime);
        Assert.Equal("x86_64-pc-windows-msvc", host.Triple);
        Assert.Null(host.Image);
        Assert.Null(host.Flasher);
    }

    [Fact]
    public void Host_links_a_console_program_that_starts_in_the_runtime_and_imports_only_kernel32()
    {
        var host = Targets.Load("host");

        Assert.Equal("chipsharp_start", host.Setting("link.entry"));
        Assert.Equal("console", host.Setting("link.subsystem"));
        Assert.Equal("i386:x86-64", host.Setting("link.machine"));
        var dlls = host.Settings["link"]!["imports"]!.AsObject();
        Assert.Equal(["kernel32.dll"], dlls.Select(d => d.Key));
        Assert.Equal(["GetStdHandle", "WriteFile", "ExitProcess"],
            dlls["kernel32.dll"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal(["-O2", "-nostdlib", "-fuse-ld=lld", "-Wno-override-module"],
            host.Settings["clang"]!["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Fact]
    public void The_toolchain_and_runtime_the_host_names_are_found_in_the_registry()
    {
        var host = Targets.Load("host");
        var registry = new ComponentRegistry()
            .Register(new ClangToolchain())
            .Register(new WindowsX64Runtime());

        Assert.IsType<ClangToolchain>(registry.Get<IToolchain>(host.Toolchain!));
        Assert.IsType<WindowsX64Runtime>(registry.Get<IArchRuntime>(host.Runtime!));
    }

    [Fact]
    public void The_runtime_is_freestanding_C_with_the_entry_point_every_print_function_and_the_shared_float_text()
    {
        var host = Targets.Load("host");
        var runtime = new WindowsX64Runtime();

        var files = runtime.Startup(host).Files;

        Assert.Equal(["chipsharp_runtime.c", "chipsharp_float_text.c"], files.Select(f => f.FileName));
        var file = files[0];
        Assert.Equal(WindowsX64Runtime.Source, file.Content);
        Assert.Equal(WindowsX64Runtime.FloatText, files[1].Content);
        Assert.All(files, f => Assert.DoesNotContain("#include", f.Content));
        string[] functions =
        [
            "void chipsharp_start(void)",
            "int chipsharp_entry(void);",
            "void ChipSharp_Runtime_WriteLine(void)",
            "void ChipSharp_Runtime_WriteLineInt32(int value)",
            "void ChipSharp_Runtime_WriteLineUInt32(unsigned int value)",
            "void ChipSharp_Runtime_WriteLineInt64(long long value)",
            "void ChipSharp_Runtime_WriteLineUInt64(unsigned long long value)",
            "void ChipSharp_Runtime_WriteLineBool(int value)",
            "void ChipSharp_Runtime_WriteLineFloat32(float value)",
            "void ChipSharp_Runtime_WriteLineFloat64(double value)",
        ];
        Assert.All(functions, f => Assert.Contains(f, file.Content));
        Assert.Contains("int chipsharp_float64_text(u64 bits, char *text)", files[1].Content);
        Assert.Contains("int chipsharp_float32_text(u32 bits, char *text)", files[1].Content);
        Assert.Empty(runtime.InterruptEntry(host).Files);
        Assert.Empty(runtime.Atomics(host).Files);
    }
}
