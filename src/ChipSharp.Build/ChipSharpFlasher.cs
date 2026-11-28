using ChipSharp.Abstractions;
using ChipSharp.Targets;

namespace ChipSharp.Build;

/// <summary>
/// <c>chipsharp flash</c>: puts a program onto a board with the flasher its target names ("flasher": "avrdude" for the
/// Uno, "uf2-copy" for the Pico). The program is the target's image (a .hex, a .uf2), as <c>chipsharp build</c> writes
/// it; the flashers come from a <see cref="ComponentRegistry"/>, by default
/// <see cref="DefaultComponents.CreateFlasherRegistry"/>. Every problem is a <see cref="ChipSharpException"/> that says
/// what is wrong and what to do.
/// </summary>
public static class ChipSharpFlasher
{
    /// <summary>
    /// The target <paramref name="name"/>, to flash: a complete one whose file names a flasher. Otherwise fails, naming
    /// the boards there are to flash.
    /// </summary>
    public static TargetDescription LoadTarget(TargetLoader targets, string name)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var target = targets.Load(name);
        if (!target.IsComplete)
        {
            throw new ChipSharpException(
                $"Target '{target.Name}' is not a board ({string.Join(", ", target.MissingValues)} is for a board's file " +
                $"to give): flash a board, {Boards(targets)}.");
        }

        if (string.IsNullOrWhiteSpace(target.Flasher))
        {
            throw new ChipSharpException(
                $"Target '{target.Name}' has no flasher (\"flasher\" in its file names what puts a program onto the " +
                $"board): {Boards(targets)}.");
        }

        return target;
    }

    /// <summary>"the boards with a flasher: pico (uf2-copy), uno (avrdude)": the complete targets that name one.</summary>
    public static string Boards(TargetLoader targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var boards = new List<string>();
        foreach (var name in targets.Names)
        {
            try
            {
                var target = targets.Load(name);
                if (target.IsComplete && !string.IsNullOrWhiteSpace(target.Flasher))
                {
                    boards.Add($"{target.Name} ({target.Flasher})");
                }
            }
            catch (ChipSharpException)
            {
                // a broken target file elsewhere is not this flash's problem
            }
        }

        return boards.Count == 0 ? "no target names a flasher" : $"the boards with a flasher: {string.Join(", ", boards)}";
    }

    /// <summary>Flashes <paramref name="imagePath"/> onto <paramref name="target"/>'s board with ChipSharp's own flashers.</summary>
    public static Task FlashAsync(string imagePath, TargetDescription target, FlashOptions options,
        IProgress<FlashProgress>? progress = null, CancellationToken cancellationToken = default) =>
        FlashAsync(imagePath, target, options, DefaultComponents.CreateFlasherRegistry(), progress, cancellationToken);

    /// <summary>
    /// Flashes <paramref name="imagePath"/> onto <paramref name="target"/>'s board with the flasher the target names,
    /// from <paramref name="registry"/>. The file must be there and be the target's image (its "image": .hex, .uf2).
    /// </summary>
    public static async Task FlashAsync(string imagePath, TargetDescription target, FlashOptions options,
        ComponentRegistry registry, IProgress<FlashProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);

        if (string.IsNullOrWhiteSpace(target.Flasher))
        {
            throw new ChipSharpException(
                $"Target '{target.Name}' has no flasher (\"flasher\" in its file names what puts a program onto the board).");
        }

        IFlasher flasher;
        try
        {
            flasher = registry.Get<IFlasher>(target.Flasher);
        }
        catch (ChipSharpException e)
        {
            throw new ChipSharpException($"Target '{target.Name}' cannot be flashed: {e.Message}", e);
        }

        var path = Path.GetFullPath(imagePath);
        var image = string.IsNullOrWhiteSpace(target.Image) ? null : "." + DefaultComponents.ImageExtension(target.Image);
        if (image is not null && !string.Equals(Path.GetExtension(path), image, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChipSharpException(
                $"'{imagePath}' is not a {image} file, which is what the target '{target.Name}' flashes: give the program's " +
                $".dll (chipsharp flash builds it for the target first) or the {image} chipsharp build -t {target.Name} wrote.");
        }

        if (!File.Exists(path))
        {
            throw new ChipSharpException($"'{imagePath}' was not found.");
        }

        await flasher.FlashAsync(path, target, options, progress, cancellationToken).ConfigureAwait(false);
    }
}
