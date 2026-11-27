using System.Globalization;
using ChipSharp.Abstractions;

namespace ChipSharp.Flasher.Uf2Copy;

/// <summary>A drive of this computer: where its files are (E:\, /media/me/RPI-RP2) and its name (volume label).</summary>
public sealed record Uf2Drive(string Root, string? Label);

/// <summary>
/// The flasher "uf2-copy": copies a .uf2 onto the drive a board's UF2 boot loader shows, as one would by hand. The
/// Raspberry Pi Pico's boot ROM shows the drive RPI-RP2 when BOOTSEL is held as the board is plugged in; the board
/// writes each block into its flash as it arrives, then restarts and runs the program (and the drive goes away). The
/// drive's name is the target's "uf2Drive" (rp2040.json); the drive is <see cref="FlashOptions.Port"/> (a drive or a
/// folder), else the one drive of that name. A boot loader's drive has INFO_UF2.TXT, which is checked before the copy.
/// </summary>
public sealed class Uf2CopyFlasher : IFlasher
{
    /// <summary>The file every UF2 boot loader's drive has (the boot loader's version and board).</summary>
    public const string InfoFile = "INFO_UF2.TXT";

    private readonly Func<IEnumerable<Uf2Drive>> _drives;

    public Uf2CopyFlasher()
        : this(SystemDrives)
    {
    }

    /// <summary>For tests: the drives there are, from <paramref name="drives"/>.</summary>
    internal Uf2CopyFlasher(Func<IEnumerable<Uf2Drive>> drives) => _drives = drives;

    public string Name => "uf2-copy";

    public async Task FlashAsync(string imagePath, TargetDescription target, FlashOptions options,
        IProgress<FlashProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var label = target.Setting("uf2Drive") is { Length: > 0 } named
            ? named
            : throw new ChipSharpException(
                $"Target '{target.Name}' is flashed by copying its .uf2 to the boot loader's drive, but does not say " +
                "that drive's name (\"uf2Drive\", e.g. \"RPI-RP2\" for a Pico).");
        var folder = string.IsNullOrWhiteSpace(options.Port) ? FindDrive(label) : options.Port.Trim();
        CheckDrive(folder, label);

        var source = Path.GetFullPath(imagePath);
        var destination = Path.Combine(folder, Path.GetFileName(source));
        var size = new FileInfo(source).Length;
        progress?.Report(new FlashProgress(
            string.Create(CultureInfo.InvariantCulture, $"{folder}: copying {Path.GetFileName(source)} ({size} bytes)"), 0));
        await CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
        progress?.Report(new FlashProgress(
            $"{folder}: copied; the {target.Name} writes it into its flash, restarts and runs it", 1));
    }

    /// <summary>The one drive named <paramref name="label"/>; otherwise an error that says how to get it, or which there are.</summary>
    internal string FindDrive(string label)
    {
        var drives = _drives().Where(drive => IsNamed(drive, label)).ToList();
        return drives.Count switch
        {
            1 => drives[0].Root,
            0 => throw new ChipSharpException(
                $"No drive named {label} was found: hold the board's BOOTSEL button while plugging it in by USB (or " +
                "while pressing its reset), and its boot loader shows as a drive of that name; or give the drive with " +
                $"--port, e.g. --port {(OperatingSystem.IsWindows() ? @"E:\" : $"/media/{label}")}."),
            _ => throw new ChipSharpException(string.Create(CultureInfo.InvariantCulture,
                $"{drives.Count} drives are named {label}: {string.Join(", ", drives.Select(d => d.Root))}. Say which " +
                $"is the board's with --port, e.g. --port {drives[0].Root}.")),
        };
    }

    /// <summary>A drive of that name, by its label or (where drives are folders, on Linux and macOS) its folder's name.</summary>
    private static bool IsNamed(Uf2Drive drive, string label) =>
        string.Equals(drive.Label, label, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(drive.Root.TrimEnd('/', '\\')), label, StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder must be a UF2 boot loader's drive: it has INFO_UF2.TXT.</summary>
    private static void CheckDrive(string folder, string label)
    {
        if (!Directory.Exists(folder))
        {
            throw new ChipSharpException(
                $"'{folder}' is not there: give the drive of the board's boot loader ({label}), which shows when " +
                "BOOTSEL is held while the board is plugged in.");
        }

        if (!File.Exists(Path.Combine(folder, InfoFile)))
        {
            throw new ChipSharpException(
                $"'{folder}' is not a UF2 boot loader's drive (it has no {InfoFile}): give the board's ({label}), " +
                "which shows when BOOTSEL is held while the board is plugged in.");
        }
    }

    /// <summary>
    /// Copies the image, its bytes written through to the drive. The board restarts once it has the last block, and
    /// the drive goes with it: an error after every byte was written (closing the file) is not one.
    /// </summary>
    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var written = false;
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            written = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!written)
            {
                throw new ChipSharpException($"Cannot copy '{source}' to '{destination}': {e.Message}", e);
            }
        }
    }

    /// <summary>The drives there are now, with their labels; those not ready (a card reader with no card) left out.</summary>
    private static List<Uf2Drive> SystemDrives()
    {
        var drives = new List<Uf2Drive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    drives.Add(new Uf2Drive(drive.RootDirectory.FullName, drive.VolumeLabel));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a drive that will not say: not a boot loader's
            }
        }

        return drives;
    }
}
