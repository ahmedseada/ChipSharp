using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ChipSharp.Backend.Llvm;
using ChipSharp.Frontend;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Runtime.Windows;
using ChipSharp.Targets;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Host;

/// <summary>
/// The PC runtime's files beside its entry point: what a program does where .NET would throw, and what the code LLVM
/// writes calls in a C runtime (memset, memcpy, memmove, __chkstk). memset, memcpy and memmove are built for this
/// PC as a DLL and checked against what they must do; the array programs run __chkstk (stack frames over a page).
/// </summary>
public sealed class WindowsRuntimeFilesTests
{
    private static readonly TargetLoader Targets = new(Path.Combine(AppContext.BaseDirectory, "targets"));
    private static readonly Lazy<Memory> Built = new(BuildMemory);

    private delegate IntPtr Fill(IntPtr target, int value, nuint count);

    private delegate IntPtr Copy(IntPtr target, IntPtr source, nuint count);

    [Fact]
    public void The_runtime_is_the_entry_point_the_console_the_float_text_and_math_then_the_failures_the_memory_functions_the_stack_probe_and_the_heap()
    {
        var files = new WindowsX64Runtime().Startup(Targets.Load("host")).Files;

        Assert.Equal(
            ["chipsharp_runtime.c", "chipsharp_console.c", "chipsharp_float_text.c", "chipsharp_float_math.c",
                "chipsharp_math.c", "chipsharp_math_musl.c", "chipsharp_math_amd.c", "chipsharp_failures.c",
                "chipsharp_memory.c", "chipsharp_chkstk.S", "chipsharp_string.c", "chipsharp_exceptions.c",
                "chipsharp_heap.c", "chipsharp_heap_windows.c", "chipsharp_heap_size.c",
                "chipsharp_string_heap.c"],
            files.Select(f => f.FileName));
        Assert.All(files, file => Assert.DoesNotContain("#include", file.Content));
        Assert.Contains("void ChipSharp_Runtime_IndexOutOfRange(void)", files[7].Content);
        Assert.Contains("ExitProcess(1);", files[7].Content);
        Assert.Contains("void *memset(void *target, int value, size_t count)", files[8].Content);
        Assert.Contains("void *memcpy(void *target, const void *source, size_t count)", files[8].Content);
        Assert.Contains("void *memmove(void *target, const void *source, size_t count)", files[8].Content);
        Assert.Contains("__chkstk:", files[9].Content);
    }

    [Fact]
    public void Every_runtime_defines_the_function_an_index_outside_an_array_calls()
    {
        var symbol = LlvmCodeGenerator.SymbolName(StackArrays.IndexOutOfRange);
        var windows = new WindowsX64Runtime().Startup(Targets.Load("host")).Files;
        var pico = new Rp2040Runtime().Startup(Targets.Load("pico")).Files;

        Assert.Equal("ChipSharp_Runtime_IndexOutOfRange", symbol);
        Assert.Contains($" {symbol}(void)", windows.Single(f => f.FileName == WindowsX64Runtime.FailuresFileName).Content);
        Assert.Contains($" {symbol}(void)", pico.Single(f => f.FileName == Rp2040Runtime.FailuresFileName).Content);
        Assert.Contains("void chipsharp_uart0_write(const char *bytes, unsigned int count)",
            pico.Single(f => f.FileName == Rp2040Runtime.SourceFileName).Content);
    }

    [HostFact]
    public void Memset_fills_exactly_the_bytes_it_is_given()
    {
        var memory = Built.Value;
        foreach (var (offset, count) in Ranges())
        {
            var buffer = Enumerable.Repeat((byte)0xAA, 200).ToArray();
            var expected = (byte[])buffer.Clone();
            Array.Fill(expected, (byte)0x5C, offset, count);

            WithPinned(buffer, address => Assert.Equal(address + offset, memory.Set(address + offset, 0x125C, (nuint)count)));

            Assert.Equal(expected, buffer);
        }
    }

    [HostFact]
    public void Memcpy_and_memmove_copy_like_the_C_library_and_memmove_also_when_the_two_overlap()
    {
        var memory = Built.Value;
        foreach (var (offset, count) in Ranges())
        {
            foreach (var to in new[] { 0, offset / 2, offset, Math.Min(offset + 1, 199 - count), Math.Min(offset + 7, 200 - count) })
            {
                var buffer = Enumerable.Range(0, 200).Select(i => (byte)(i * 7)).ToArray();
                var expected = (byte[])buffer.Clone();
                Array.Copy(buffer, offset, expected, to, count); // Array.Copy copies overlapping ranges as memmove does

                WithPinned(buffer, address => memory.Move(address + to, address + offset, (nuint)count));

                Assert.Equal(expected, buffer);
            }

            var source = Enumerable.Range(0, 200).Select(i => (byte)(255 - i)).ToArray();
            var target = new byte[200];
            WithPinned(source, from => WithPinned(target, to => Assert.Equal(to + 3, memory.Copy(to + 3, from + offset, (nuint)count))));
            Assert.Equal(source.AsSpan(offset, count).ToArray(), target.AsSpan(3, count).ToArray());
        }
    }

    /// <summary>Offsets and lengths that fit in 200 bytes with room to move, odd and even, empty and long.</summary>
    private static IEnumerable<(int Offset, int Count)> Ranges() =>
        from offset in new[] { 0, 1, 7, 16, 33 }
        from count in new[] { 0, 1, 2, 3, 8, 15, 16, 31, 64, 100, 150 }
        where offset + count <= 192
        select (offset, count);

    private static void WithPinned(byte[] buffer, Action<IntPtr> action)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            action(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// chipsharp_memory.c built with the host's flags into a DLL and loaded. A loaded DLL cannot be deleted, so it
    /// lives in a folder named after the source's hash, which later runs reuse.
    /// </summary>
    private static Memory BuildMemory()
    {
        var source = WindowsX64Runtime.Read(WindowsX64Runtime.MemoryFileName);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
        var folder = Path.Combine(Path.GetTempPath(), "chipsharp-tests", "windows-memory-" + hash);
        var dll = Path.Combine(folder, "memory.dll");
        if (!File.Exists(dll))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "memory.c"), source);
            var built = $"memory-{Guid.NewGuid():N}.dll"; // then renamed: a half-written DLL is never loaded
            var start = new ProcessStartInfo(new ClangToolchain().FindClang())
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            string[] arguments =
            [
                "--target=x86_64-pc-windows-msvc", "-O2", "-nostdlib", "-fuse-ld=lld", "-shared", "memory.c", "-o",
                built, "-Wl,-noentry", "-Wl,-export:memset", "-Wl,-export:memcpy", "-Wl,-export:memmove",
            ];
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using (var process = Process.Start(start)!)
            {
                var errors = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, $"clang failed:\n{output}\n{errors.Result}");
            }

            try
            {
                File.Move(Path.Combine(folder, built), dll);
            }
            catch (IOException) when (File.Exists(dll))
            {
                // another test run built it first
            }
        }

        var library = NativeLibrary.Load(dll);
        T Get<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        return new Memory(Get<Fill>("memset"), Get<Copy>("memcpy"), Get<Copy>("memmove"));
    }

    private sealed record Memory(Fill Set, Copy Copy, Copy Move);
}
