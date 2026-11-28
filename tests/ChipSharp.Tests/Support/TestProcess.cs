using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ChipSharp.Tests.Support;

/// <summary>Settings of the process the tests run in, made before any test runs.</summary>
internal static class TestProcess
{
    /// <summary>
    /// Lets the thread pool start threads at once, up to many: the tests that build programs wait for clang, the programs
    /// and the simulator with threads of the pool (a build waits for its compiles), and a pool that adds a thread only
    /// every half second when all are waiting would leave the processors idle. And keeps Windows from holding a crashed
    /// program on a dialog (<see cref="NoCrashDialogs"/>).
    /// </summary>
    [ModuleInitializer]
    internal static void Start()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 16 * Environment.ProcessorCount),
            Math.Max(completionPorts, 4 * Environment.ProcessorCount));
        NoCrashDialogs();
    }

    /// <summary>
    /// Many tests run programs that stop on purpose where .NET throws: a division by zero, a stack overflow. On a PC with
    /// a just-in-time debugger registered (Visual Studio's vsjitdebugger), Windows stops such a program on a dialog,
    /// "Click on OK to terminate the program, Click on CANCEL to debug the program", and the test waits for it until its
    /// time runs out. The error mode a process is started with comes from the process that starts it, so setting it here
    /// covers every program the tests start (dotnet, the programs ChipSharp builds, the chipsharp command) and theirs:
    /// no dialog for a crash (SEM_NOGPFAULTERRORBOX), a missing file or a missing disk; the program just stops.
    /// </summary>
    private static void NoCrashDialogs()
    {
        if (OperatingSystem.IsWindows())
        {
            const uint failCriticalErrors = 0x0001, noGpFaultErrorBox = 0x0002, noOpenFileErrorBox = 0x8000;
            SetErrorMode(GetErrorMode() | failCriticalErrors | noGpFaultErrorBox | noOpenFileErrorBox);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();
}
