using ChipSharp.Abstractions;
using ChipSharp.Build;
using ChipSharp.Flasher.Uf2Copy;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Flash;

/// <summary>
/// The flasher "uf2-copy", for the Pico: the .uf2 copied to the boot loader's drive (RPI-RP2), found by its name or
/// given, with folders standing in for drives; and what it says when there is none, or more than one.
/// </summary>
public sealed class Uf2CopyFlasherTests : IDisposable
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public async Task The_uf2_is_copied_to_the_one_drive_named_RPI_RP2()
    {
        var pico = Shipped.Load("pico");
        Assert.Equal(("uf2-copy", "RPI-RP2"), (pico.Flasher, pico.Setting("uf2Drive")));
        Assert.IsType<Uf2CopyFlasher>(DefaultComponents.CreateFlasherRegistry().Get<IFlasher>("uf2-copy"));

        var drive = BootDrive("e");
        var other = Folder("c");
        var uf2 = Image();
        var flasher = new Uf2CopyFlasher(() => [new Uf2Drive(other, "Windows"), new Uf2Drive(drive, "RPI-RP2")]);
        var stages = new List<string>();

        await flasher.FlashAsync(uf2, pico, new FlashOptions(), new Stages(stages), CancellationToken.None);

        Assert.Equal(File.ReadAllBytes(uf2), File.ReadAllBytes(Path.Combine(drive, "Blink.uf2")));
        Assert.Equal(
            [
                $"{drive}: copying Blink.uf2 (1024 bytes)",
                $"{drive}: copied; the pico writes it into its flash, restarts and runs it",
            ],
            stages);
        Assert.Empty(Directory.GetFiles(other));
    }

    [Fact]
    public async Task A_drive_given_is_taken_when_it_is_a_boot_loaders_and_found_by_its_folder_name_on_Linux()
    {
        var pico = Shipped.Load("pico");
        var uf2 = Image();
        var given = BootDrive("given");
        var flasher = new Uf2CopyFlasher(() => throw new InvalidOperationException("A drive was given."));

        await flasher.FlashAsync(uf2, pico, new FlashOptions(given), null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(given, "Blink.uf2")));

        var notBoot = Folder("documents");
        var error = await Assert.ThrowsAsync<ChipSharpException>(() =>
            flasher.FlashAsync(uf2, pico, new FlashOptions(notBoot), null, CancellationToken.None));
        Assert.Equal(
            $"'{notBoot}' is not a UF2 boot loader's drive (it has no INFO_UF2.TXT): give the board's (RPI-RP2), which " +
            "shows when BOOTSEL is held while the board is plugged in.",
            error.Message);
        Assert.Empty(Directory.GetFiles(notBoot));

        // Linux mounts a drive as a folder named after it, e.g. /media/me/RPI-RP2
        var mounted = BootDrive(Path.Combine("media", "RPI-RP2"));
        await new Uf2CopyFlasher(() => [new Uf2Drive(mounted + Path.DirectorySeparatorChar, null)])
            .FlashAsync(uf2, pico, new FlashOptions(), null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(mounted, "Blink.uf2")));
    }

    [Fact]
    public void Without_the_drive_or_with_two_the_error_says_what_to_do()
    {
        var none = Assert.Throws<ChipSharpException>(() => new Uf2CopyFlasher(() => [new Uf2Drive(@"C:\", "Windows")])
            .FindDrive("RPI-RP2"));
        Assert.Equal(
            "No drive named RPI-RP2 was found: hold the board's BOOTSEL button while plugging it in by USB (or while " +
            "pressing its reset), and its boot loader shows as a drive of that name; or give the drive with --port, " +
            @"e.g. --port E:\.",
            none.Message);

        var two = Assert.Throws<ChipSharpException>(() => new Uf2CopyFlasher(() =>
            [new Uf2Drive(@"E:\", "RPI-RP2"), new Uf2Drive(@"F:\", "rpi-rp2")]).FindDrive("RPI-RP2"));
        Assert.Equal(@"2 drives are named RPI-RP2: E:\, F:\. Say which is the board's with --port, e.g. --port E:\.",
            two.Message);
    }

    private string Image()
    {
        var path = Path.Combine(Folder("program"), "Blink.uf2");
        File.WriteAllBytes(path, [.. Enumerable.Range(0, 1024).Select(i => (byte)i)]);
        return path;
    }

    /// <summary>A folder that stands for a boot loader's drive: it has INFO_UF2.TXT, as the RP2040's has.</summary>
    private string BootDrive(string name)
    {
        var folder = Folder(name);
        File.WriteAllText(Path.Combine(folder, Uf2CopyFlasher.InfoFile), "UF2 Bootloader v3.0\r\nModel: Raspberry Pi RP2\r\n");
        return folder;
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_scratch, name)).FullName;

    private sealed class Stages(List<string> stages) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => stages.Add(value.Stage);
    }
}
