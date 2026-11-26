using ChipSharp.Abstractions;

namespace ChipSharp.Build;

/// <summary>
/// What the plug-ins of one build are made with (see <see cref="DefaultComponents.CreateRegistry"/>): some take
/// per-build settings, such as the folder the clang toolchain works in.
/// </summary>
/// <param name="Options">The options the build was started with.</param>
/// <param name="Target">The target being built for, complete.</param>
/// <param name="ProgramName">The program's file name without extension, e.g. "Blink".</param>
/// <param name="WorkFolder">
/// A folder of its own for the toolchain's files (the generated code, the runtime's sources, the linked program),
/// under the system temp folder. It does not exist yet. A successful build deletes it after copying what it needs; a
/// failed one leaves it, so the files an error message names are still there.
/// </param>
public sealed record BuildContext(BuildOptions Options, TargetDescription Target, string ProgramName, string WorkFolder);
