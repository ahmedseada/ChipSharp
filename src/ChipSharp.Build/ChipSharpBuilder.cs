using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Frontend.Lowering;
using ChipSharp.Ir;
using ChipSharp.Targets;

namespace ChipSharp.Build;

/// <summary>
/// The build pipeline: one call from a compiled C# assembly to a program for a target.
/// <code>
/// .dll --> IL --> whole-program pass --> TIR --code generator--> .ll --toolchain + runtime--> .exe / .elf --image--> .uf2
/// </code>
/// The target file names the part each step uses ("backend", "toolchain", "runtime", "image"); the parts come from a
/// <see cref="ComponentRegistry"/>, by default <see cref="DefaultComponents.CreateRegistry"/>. Every problem is a
/// <see cref="ChipSharpException"/> that says what is wrong and where.
/// </summary>
public static class ChipSharpBuilder
{
    /// <summary>Builds the program in <paramref name="assemblyPath"/> with ChipSharp's own plug-ins.</summary>
    public static BuildResult Build(string assemblyPath, BuildOptions options) =>
        Build(assemblyPath, options, DefaultComponents.CreateRegistry);

    /// <summary>
    /// Builds the program in <paramref name="assemblyPath"/> with the plug-ins <paramref name="createRegistry"/> makes
    /// for this build. The steps, in order:
    /// <list type="number">
    /// <item>Load the target; it must be complete.</item>
    /// <item>Read the assembly, and find the parts the target names in the registry.</item>
    /// <item>Check the program (the whole-program pass) and translate it into TIR; the notes the pass gives (see
    /// <see cref="AnalysisResult.Notes"/>) come back with the result.</item>
    /// <item>Generate code, add the runtime's startup, interrupt entry and atomics files, and link it all with the
    /// toolchain; then see how much of the target's memory the program takes (<see cref="MemoryReport"/>).</item>
    /// <item>When the target names an image format, package the linked program into the output folder as
    /// &lt;program&gt;.&lt;image name&gt;, e.g. Blink.uf2; otherwise copy the linked program itself there.</item>
    /// </list>
    /// </summary>
    public static BuildResult Build(string assemblyPath, BuildOptions options,
        Func<BuildContext, ComponentRegistry> createRegistry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createRegistry);

        var targets = new TargetLoader(TargetsFolder(options));
        var target = targets.Load(options.Target);
        if (!target.IsComplete)
        {
            throw Incomplete(target, targets);
        }

        var assembly = IlAssembly.Load(assemblyPath);
        var programName = CheckProgramName(options.ProgramName ?? assembly.Name);
        var outputFolder = Path.GetFullPath(options.OutputFolder ?? Path.Combine("chipsharp-output", target.Name));
        var workFolder = Path.Combine(Path.GetTempPath(), "chipsharp", Path.GetRandomFileName());
        var registry = createRegistry(new BuildContext(options, target, programName, workFolder))
            ?? throw new InvalidOperationException("The registry factory returned no registry.");
        var parts = Parts.Find(registry, target);

        var analysis = WholeProgramPass.Check(assembly, target);
        var module = IlToTir.Lower(analysis.ToProgram());
        var kept = new List<string>();
        if (options.KeepIntermediates)
        {
            kept.Add(WriteText(outputFolder, programName + ".tir", TirPrinter.Print(module)));
        }

        // a program that never uses the heap (no object, no string made, nothing of System.GC: the whole program,
        // the library substitutes and the runtime functions it calls) is built as for the target without one: no RAM
        // kept for a heap (the stack may use it), no memory taken from the operating system, no collector linked in
        var linked = module.UsesHeap ? target : target.WithoutHeap();
        var code = Generate(parts.CodeGenerator, module, linked);
        if (options.KeepIntermediates)
        {
            kept.AddRange(code.Files.Select(file => WriteText(outputFolder, file.FileName, file.Content)));
        }

        var binary = parts.Toolchain.Build(new GeneratedCode(
        [
            .. code.Files,
            .. parts.Runtime.Startup(linked).Files,
            .. parts.Runtime.InterruptEntry(linked).Files,
            .. parts.Runtime.Atomics(linked).Files,
        ]), linked);
        var memory = MemoryReport.Read(binary, linked);

        string output;
        var variants = new List<(string Path, ImageVariant Variant)>();
        if (parts.Image is null)
        {
            output = CopyInto(outputFolder, binary.Path);
        }
        else
        {
            if (options.KeepIntermediates)
            {
                kept.Add(CopyInto(outputFolder, binary.Path));
            }

            output = Package(parts.Image, binary, target, Path.Combine(outputFolder, $"{programName}.{parts.Image.Extension}"));
            foreach (var variant in parts.Image.Variants)
            {
                variants.Add((PackageVariant(parts.Image, variant, binary, target,
                    Path.Combine(outputFolder, $"{programName}.{variant.Suffix}.{parts.Image.Extension}")), variant));
            }
        }

        DeleteFolder(workFolder);
        return new BuildResult(output, kept, target, memory)
        {
            Notes = analysis.Notes, NoteGroups = analysis.NoteGroups, Variants = variants,
            MainMeaning = parts.Image?.MainMeaning ?? "",
        };
    }

    /// <summary>
    /// The program in the assembly at <paramref name="assemblyPath"/> as TIR: read, checked by the whole-program pass
    /// and translated, as a build for <paramref name="target"/> does before it generates code. (Generating code also
    /// verifies the TIR; this does not, so even TIR that is not well formed can be looked at.) Without a target, the
    /// interrupt handlers are not put in any interrupt.
    /// </summary>
    public static TirModule Translate(string assemblyPath, TargetDescription? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        var assembly = IlAssembly.Load(assemblyPath);
        return IlToTir.Lower(target is null ? WholeProgramPass.Analyze(assembly) : WholeProgramPass.Analyze(assembly, target));
    }

    /// <summary>The option, else the environment variable CHIPSHARP_TARGETS, else "targets" next to the running program.</summary>
    private static string TargetsFolder(BuildOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.TargetsFolder))
        {
            return options.TargetsFolder;
        }

        var variable = Environment.GetEnvironmentVariable(TargetLoader.EnvironmentVariable);
        return string.IsNullOrWhiteSpace(variable)
            ? Path.Combine(AppContext.BaseDirectory, "targets")
            : variable.Trim().Trim('"');
    }

    /// <summary>
    /// "Target 'rp2040' is incomplete (memory.flash.size); build a board that inherits it, such as pico or pico-w."
    /// The boards named are the complete targets in the same folder that inherit it.
    /// </summary>
    private static ChipSharpException Incomplete(TargetDescription target, TargetLoader targets)
    {
        var heirs = new List<string>();
        foreach (var name in targets.Names)
        {
            TargetDescription candidate;
            try
            {
                candidate = targets.Load(name);
            }
            catch (ChipSharpException)
            {
                continue; // a broken target file elsewhere is not this build's problem
            }

            if (candidate.IsComplete
                && candidate.InheritanceChain.Skip(1).Contains(target.Name, StringComparer.OrdinalIgnoreCase))
            {
                heirs.Add(candidate.Name);
            }
        }

        var advice = heirs.Count == 0
            ? $"a target that inherits it must set {(target.MissingValues.Count == 1 ? "that" : "those")}: " +
              $"write a board file with \"inherits\": \"{target.Name}\" and build that"
            : $"build a board that inherits it, such as {Alternatives(heirs.Take(3).ToList())}";
        return new ChipSharpException(
            $"Target '{target.Name}' is incomplete ({string.Join(", ", target.MissingValues)}); {advice}.");
    }

    /// <summary>"a", "a or b", "a, b or c".</summary>
    private static string Alternatives(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} or {names[^1]}";

    private static string CheckProgramName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim('.').Length == 0
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ChipSharpException(
                $"The program name '{name}' cannot be a file name: give a plain name, without folders or any of " +
                "\\ / : * ? \" < > |.");
        }

        return name;
    }

    /// <summary>Generates code; TIR the code generator finds broken is ChipSharp's own fault, and the message says so.</summary>
    private static GeneratedCode Generate(ICodeGenerator generator, TirModule module, TargetDescription target)
    {
        try
        {
            return generator.Generate(module, target);
        }
        catch (TirException e)
        {
            throw new ChipSharpException(
                $"The TIR of {module.Name} is not well formed, which is a bug in ChipSharp (please report it, " +
                $"with the output of chipsharp tir):\n{e.Message}", e);
        }
    }

    private static string WriteText(string folder, string fileName, string content)
    {
        var path = Path.Combine(folder, fileName);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, content);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ChipSharpException($"Cannot write '{path}': {e.Message}", e);
        }
    }

    /// <summary>Copies a file the toolchain wrote into <paramref name="folder"/>, under the same name.</summary>
    private static string CopyInto(string folder, string file)
    {
        var source = Path.GetFullPath(file);
        var path = Path.Combine(folder, Path.GetFileName(source));
        try
        {
            Directory.CreateDirectory(folder);
            if (!string.Equals(source, path, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, path, overwrite: true);
            }

            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ChipSharpException($"Cannot copy the linked program '{source}' to '{path}': {e.Message}", e);
        }
    }

    private static string PackageVariant(IImageFormat image, ImageVariant variant, LinkedBinary binary, TargetDescription target, string path)
    {
        try
        {
            using (var stream = File.Create(path))
            {
                image.PackageVariant(variant.Suffix, binary, target, stream);
            }

            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteFile(path);
            throw new ChipSharpException($"Cannot write the {image.Name} image '{path}': {e.Message}", e);
        }
        catch
        {
            DeleteFile(path);
            throw;
        }
    }

    /// <summary>Writes the image to <paramref name="path"/>; when packaging fails, no half-written image is left.</summary>
    private static string Package(IImageFormat image, LinkedBinary binary, TargetDescription target, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = File.Create(path))
            {
                image.Package(binary, target, stream);
            }

            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteFile(path);
            throw new ChipSharpException($"Cannot write the {image.Name} image '{path}': {e.Message}", e);
        }
        catch
        {
            DeleteFile(path);
            throw;
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the error being reported matters more than a leftover file
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // it is under the system temp folder; leaving it does no harm
        }
    }

    /// <summary>The parts a target names, found in the registry before any work is done.</summary>
    private sealed record Parts(ICodeGenerator CodeGenerator, IArchRuntime Runtime, IToolchain Toolchain, IImageFormat? Image)
    {
        public static Parts Find(ComponentRegistry registry, TargetDescription target) => new(
            Part<ICodeGenerator>(registry, target, target.Backend, "backend"),
            Part<IArchRuntime>(registry, target, target.Runtime, "runtime"),
            Part<IToolchain>(registry, target, target.Toolchain, "toolchain"),
            string.IsNullOrWhiteSpace(target.Image) ? null : Part<IImageFormat>(registry, target, target.Image, "image"));

        private static T Part<T>(ComponentRegistry registry, TargetDescription target, string? name, string key)
            where T : class, INamedComponent
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ChipSharpException(
                    $"Target '{target.Name}' cannot be built: it does not say which {key} to use (\"{key}\" is missing).");
            }

            try
            {
                return registry.Get<T>(name);
            }
            catch (ChipSharpException e)
            {
                throw new ChipSharpException($"Target '{target.Name}' cannot be built: {e.Message}", e);
            }
        }
    }
}
