using ChipSharp.Abstractions;
using ChipSharp.Tests.Pico;
using ChipSharp.Toolchain.Clang;
using Xunit.Sdk;

namespace ChipSharp.Tests.Avr;

/// <summary>
/// A test that builds a program for the Arduino Uno (ATmega328P) with the clang and ld.lld inside Zig. It is skipped,
/// with the reason, on a machine without Zig; with <see cref="NeedsLlvmObjcopy"/>, also without LLVM's llvm-objcopy.
/// </summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionFactDiscoverer", "ChipSharp.Tests")]
public sealed class AvrFactAttribute : FactAttribute
{
    public AvrFactAttribute()
    {
        if (AvrTools.MissingZig.Value is { } reason)
        {
            Skip = reason;
        }
    }

    /// <summary>The test compares with what LLVM's llvm-objcopy (next to the installed clang) writes.</summary>
    public bool NeedsLlvmObjcopy
    {
        get => false;
        set
        {
            if (value && Skip is null && AvrTools.MissingObjcopy.Value is { } reason)
            {
                Skip = reason;
            }
        }
    }
}

/// <summary>
/// A test that builds a program for the Arduino Uno and runs it in the avr8js simulator (tools/avr-sim). It is
/// skipped, with the reason, when Zig, Node.js or the simulator's packages are missing.
/// </summary>
[XunitTestCaseDiscoverer("ChipSharp.Tests.Support.OwnCollectionFactDiscoverer", "ChipSharp.Tests")]
public sealed class AvrSimulatorFactAttribute : FactAttribute
{
    public AvrSimulatorFactAttribute()
    {
        if (AvrTools.MissingSimulator.Value is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Where the tools the Arduino Uno tests need are, and why a test cannot run when one is missing.</summary>
internal static class AvrTools
{
    /// <summary>The toolchain "zig", as the build pipeline registers it, with no build cache.</summary>
    public static readonly ClangToolchain Zig = new(new ClangToolchainOptions { Driver = ClangDriver.Zig, UseCache = false });

    /// <summary>Null when Zig is there; otherwise why not (the toolchain's own message).</summary>
    public static readonly Lazy<string?> MissingZig = new(() =>
    {
        try
        {
            Zig.FindZig();
            return null;
        }
        catch (ChipSharpException e)
        {
            return e.Message;
        }
    });

    /// <summary>Null when Zig and llvm-objcopy are there; otherwise why not.</summary>
    public static readonly Lazy<string?> MissingObjcopy = new(() =>
    {
        if (MissingZig.Value is { } zig)
        {
            return zig;
        }

        try
        {
            return File.Exists(PicoTools.LlvmTool("llvm-objcopy"))
                ? null
                : "llvm-objcopy is not next to clang; it comes with LLVM.";
        }
        catch (ChipSharpException e)
        {
            return e.Message;
        }
    });

    /// <summary>Null when the simulator can run; otherwise why not.</summary>
    public static readonly Lazy<string?> MissingSimulator = new(() =>
    {
        if (MissingZig.Value is { } zig)
        {
            return zig;
        }

        if (PicoTools.Node is null)
        {
            return "Node.js (node) is not on the PATH; the avr8js simulator runs on it. Install Node.js 18 or later.";
        }

        var package = Path.Combine(Simulator, "node_modules", "avr8js", "package.json");
        return File.Exists(package)
            ? null
            : $"The simulator's packages are not installed: run 'npm ci' in '{Simulator}' once.";
    });

    /// <summary>tools/avr-sim.</summary>
    public static string Simulator => Path.Combine(Repository.Root, "tools", "avr-sim");
}
