using System.Runtime.CompilerServices;

namespace ChipSharp.Tests.Support;

/// <summary>Settings of the process the tests run in, made before any test runs.</summary>
internal static class TestProcess
{
    /// <summary>
    /// Lets the thread pool start threads at once, up to many: the tests that build programs wait for clang, the programs
    /// and the simulator with threads of the pool (a build waits for its compiles), and a pool that adds a thread only
    /// every half second when all are waiting would leave the processors idle.
    /// </summary>
    [ModuleInitializer]
    internal static void Start()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 16 * Environment.ProcessorCount),
            Math.Max(completionPorts, 4 * Environment.ProcessorCount));
    }
}
