using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Build;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Build;

/// <summary>
/// The whole pipeline with a target that packages an image, built from stand-in parts registered in a test registry:
/// a toolchain that "links" by listing the files it was given, a runtime with three small files and an image format
/// that wraps the linked program. The code generator is the real one. None of it needs LLVM.
/// </summary>
public sealed class ImageStepTests : IDisposable
{
    private const string Board = """
        {
          "backend": "llvm", "toolchain": "list-ld", "runtime": "fake-m0", "image": "fake",
          "triple": "thumbv6m-none-eabi", "pointerSize": 4, "endian": "little", "capabilities": { "console": true }
        }
        """;

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));
    private readonly List<BuildContext> _builds = [];

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }

        foreach (var build in _builds.Where(b => Directory.Exists(b.WorkFolder)))
        {
            Directory.Delete(build.WorkFolder, recursive: true);
        }
    }

    [Fact]
    public void The_image_is_named_after_the_program_and_the_image_format_and_is_the_only_output()
    {
        var output = Path.Combine(_scratch, "out");

        var result = Build(new FakeImage(), keep: false, output);

        Assert.Equal(Path.Combine(output, "Square.fake"), result.OutputPath);
        Assert.Equal("fake-board", result.Target.Name);
        Assert.Empty(result.IntermediatePaths);
        Assert.Equal(["Square.fake"], Directory.GetFiles(output).Select(path => Path.GetFileName(path)));
        // the code generator's file first, then the runtime's startup, interrupt entry and atomics files
        Assert.Equal("""
            fake image of Square.elf
            Square.ll
            startup.S
            vectors.S
            atomics.c

            """.ReplaceLineEndings("\n"), File.ReadAllText(result.OutputPath));
    }

    [Fact]
    public void The_plug_ins_are_made_for_the_build_and_its_work_folder_is_gone_after_it()
    {
        var options = new BuildOptions
        {
            Target = "fake-board",
            TargetsFolder = TargetFiles.Write(Path.Combine(_scratch, "targets"), ("fake-board", Board)),
            OutputFolder = Path.Combine(_scratch, "out"),
        };

        var result = ChipSharpBuilder.Build(Samples.Compile("Square", _scratch), options, Registry(new FakeImage()));

        var build = Assert.Single(_builds);
        Assert.Same(options, build.Options);
        Assert.Same(result.Target, build.Target);
        Assert.Equal("Square", build.ProgramName);
        Assert.StartsWith(Path.GetTempPath(), build.WorkFolder);
        Assert.False(Directory.Exists(build.WorkFolder));
    }

    [Fact]
    public void Keep_also_keeps_the_linked_program_the_image_was_made_from()
    {
        var output = Path.Combine(_scratch, "out");

        var result = Build(new FakeImage(), keep: true, output);

        Assert.Equal(Path.Combine(output, "Square.fake"), result.OutputPath);
        Assert.Equal(
            [Path.Combine(output, "Square.tir"), Path.Combine(output, "Square.ll"), Path.Combine(output, "Square.elf")],
            result.IntermediatePaths);
        Assert.Contains("target triple = \"thumbv6m-none-eabi\"", File.ReadAllText(Path.Combine(output, "Square.ll")));
        Assert.StartsWith("Square.ll\n", File.ReadAllText(Path.Combine(output, "Square.elf")));
    }

    [Fact]
    public void An_image_format_that_fails_leaves_no_half_written_image_and_keeps_the_work_folder()
    {
        var output = Path.Combine(_scratch, "out");

        var error = Assert.Throws<ChipSharpException>(() => Build(new FailingImage(), keep: false, output));

        Assert.Equal("The program does not fit in flash.", error.Message);
        Assert.False(File.Exists(Path.Combine(output, "Square.fake")));
        Assert.True(File.Exists(Path.Combine(Assert.Single(_builds).WorkFolder, "Square.elf")));
    }

    private BuildResult Build(IImageFormat image, bool keep, string output) => ChipSharpBuilder.Build(
        Samples.Compile("Square", _scratch),
        new BuildOptions
        {
            Target = "fake-board",
            TargetsFolder = TargetFiles.Write(Path.Combine(_scratch, "targets"), ("fake-board", Board)),
            OutputFolder = output,
            KeepIntermediates = keep,
        },
        Registry(image));

    /// <summary>The test registry: the real code generator, the stand-ins, and the given image format.</summary>
    private Func<BuildContext, ComponentRegistry> Registry(IImageFormat image) => build =>
    {
        _builds.Add(build);
        return new ComponentRegistry()
            .Register(new LlvmCodeGenerator())
            .Register(new ListingToolchain(build))
            .Register(new FakeRuntime())
            .Register(image);
    };

    /// <summary>"Links" by writing the names of the files it was given, one per line, to &lt;program&gt;.elf.</summary>
    private sealed class ListingToolchain(BuildContext build) : IToolchain
    {
        public string Name => "list-ld";

        public LinkedBinary Build(GeneratedCode code, TargetDescription target)
        {
            Directory.CreateDirectory(build.WorkFolder);
            var path = Path.Combine(build.WorkFolder, build.ProgramName + ".elf");
            File.WriteAllText(path, string.Concat(code.Files.Select(file => file.FileName + "\n")));
            return new LinkedBinary(path);
        }
    }

    private sealed class FakeRuntime : IArchRuntime
    {
        public string Name => "fake-m0";

        public RuntimeSources Startup(TargetDescription target) => new([new GeneratedFile("startup.S", "")]);

        public RuntimeSources InterruptEntry(TargetDescription target) => new([new GeneratedFile("vectors.S", "")]);

        public RuntimeSources Atomics(TargetDescription target) => new([new GeneratedFile("atomics.c", "")]);
    }

    /// <summary>Writes a header line and then the linked program.</summary>
    private sealed class FakeImage : IImageFormat
    {
        public string Name => "fake";

        public void Package(LinkedBinary binary, TargetDescription target, Stream output)
        {
            using var writer = new StreamWriter(output);
            writer.Write($"fake image of {Path.GetFileName(binary.Path)}\n");
            writer.Write(File.ReadAllText(binary.Path));
        }
    }

    /// <summary>Starts writing, then gives up.</summary>
    private sealed class FailingImage : IImageFormat
    {
        public string Name => "fake";

        public void Package(LinkedBinary binary, TargetDescription target, Stream output)
        {
            output.Write("UF2\n"u8);
            throw new ChipSharpException("The program does not fit in flash.");
        }
    }
}
