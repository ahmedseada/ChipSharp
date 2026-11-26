namespace ChipSharp.Tests.Host;

/// <summary>
/// The sample programs in samples/ and their expected output (written by samples/update-expected.ps1 from real
/// .NET). ChipSharp's output for each sample must match its expected file exactly.
/// </summary>
public sealed class SampleFilesTests
{
    private const string ExpectedSuffix = ".expected.txt";
    private static readonly string Samples = FindSamplesFolder();

    [Fact]
    public void Every_sample_has_its_expected_output_and_every_expected_output_its_sample()
    {
        var programs = Directory.GetFiles(Samples, "*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.OrdinalIgnoreCase);
        var outputs = Directory.GetFiles(Samples, "*" + ExpectedSuffix)
            .Select(path => Path.GetFileName(path)[..^ExpectedSuffix.Length])
            .Order(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Square", programs);
        Assert.Equal(programs, outputs);
    }

    [Fact]
    public void Expected_outputs_are_dotnets_windows_output_byte_for_byte()
    {
        Assert.All(Directory.GetFiles(Samples, "*" + ExpectedSuffix), path =>
        {
            var text = File.ReadAllText(path);
            Assert.EndsWith("\r\n", text);
            Assert.Equal(text.Replace("\r\n", "\n").Replace("\n", "\r\n"), text);
        });
        Assert.Equal("1764\r\n", File.ReadAllText(Path.Combine(Samples, "Square" + ExpectedSuffix)));
    }

    /// <summary>samples/ at the top of the repository: the first folder above the tests that has ChipSharp.slnx.</summary>
    private static string FindSamplesFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "ChipSharp.slnx")))
            {
                return Path.Combine(folder.FullName, "samples");
            }
        }

        throw new InvalidOperationException($"No ChipSharp.slnx above '{AppContext.BaseDirectory}'.");
    }
}
