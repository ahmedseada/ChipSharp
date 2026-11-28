using System.Buffers.Binary;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Image.Esp;
using ChipSharp.Simulators;
using ChipSharp.Simulators.Esp32c5;
using ChipSharp.Tests.Build;
using ChipSharp.Targets;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Esp32c5;

/// <summary>
/// The ESP32-C5 image as an app behind ESP-IDF's second-stage bootloader (Wokwi's flow; a devkit with its factory
/// bootloader), where the app is at flash offset 0x10000: the application description at the start of the first
/// segment, and a check of each rule that bootloader applies (release/v5.5: esp_image_format.c, bootloader_common_loader.c,
/// bootloader_utility.c, the C5's bootloader.ld; EspIdfBootloaderRules names each). No toolchain is needed: the images are
/// made from small .elf files.
/// </summary>
public sealed class EspIdfBootloaderTests : IDisposable
{
    private static readonly TargetLoader Shipped = new(Path.Combine(AppContext.BaseDirectory, "targets"));

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    /// <summary>A first segment: the description as esp32c5_app_desc.c has it, then <paramref name="code"/>.</summary>
    private static byte[] Program(params byte[] code)
    {
        var bytes = new byte[256 + code.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xABCD5432);
        void Text(int offset, string text) => Encoding.ASCII.GetBytes(text).CopyTo(bytes, offset);
        Text(16, "1.0");
        Text(48, "chipsharp");
        Text(80, "00:00:00");
        Text(96, "Jan  1 1970");
        Text(112, "ChipSharp");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(178), 65535);
        bytes[180] = 16;
        code.CopyTo(bytes, 256);
        return bytes;
    }

    /// <summary>The image of a program of <paramref name="size"/> bytes of code after the description, for the board.</summary>
    private static EspImage Image(int size = 64, TargetDescription? target = null)
    {
        var data = Program(new byte[size]);
        return EspImageFormat.Make(EspImageTests.Elf(0x40800100, (0x40800000, data)), target ?? Shipped.Load("esp32c5-devkitc"), "app.elf",
            "blink", new byte[32]);
    }

    private static IReadOnlyList<string> Check(EspImage image, uint offset = 0x10000, uint size = 0x100000) =>
        EspIdfBootloaderRules.Check(image.Write(), offset, size);

    [Fact]
    public void The_description_has_ESP_IDFs_layout_and_the_writer_fills_in_the_project_name_and_the_elfs_hash()
    {
        var hash = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var image = EspImageFormat.Make(EspImageTests.Elf(0x40800100, (0x40800000, Program(1, 2, 3, 4))),
            Shipped.Load("esp32c5-devkitc"), "app.elf", "my-program", hash);

        var description = EspAppDescription.Read(image.Segments[0].Data);

        Assert.Equal(0xABCD5432u, description.Magic);
        Assert.Equal((0u, "1.0", "my-program", "00:00:00", "Jan  1 1970", "ChipSharp"),
            (description.SecureVersion, description.Version, description.ProjectName, description.Time, description.Date, description.IdfVersion));
        Assert.Equal(hash, description.ElfSha256);
        Assert.Equal((0, 65535, 16), (description.MinEfuseBlockRevision, (int)description.MaxEfuseBlockRevision, (int)description.MmuPageSizeLog2));
        // the bytes at the offsets esp_app_desc.h gives, after the description the program
        var bytes = image.Segments[0].Data;
        Assert.Equal(0xABCD5432u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(hash, bytes[144..176]);
        Assert.Equal([1, 2, 3, 4], bytes[256..260]);
        // a long name is cut to 31 characters and a NUL
        var long_ = EspAppDescription.Read(EspAppDescription.Fill(Program(), new string('x', 40), null));
        Assert.Equal(new string('x', 31), long_.ProjectName);
        Assert.Equal("ChipSharp", long_.IdfVersion);
    }

    [Fact]
    public void A_program_without_the_description_is_the_image_of_before_and_the_bootloader_would_refuse_it()
    {
        // the image the owner's Wokwi run refused: the program's code at the start, 17667 (0x4503) at +176
        var code = new byte[300];
        BinaryPrimitives.WriteUInt32LittleEndian(code, 0xC0067139);
        BinaryPrimitives.WriteUInt16LittleEndian(code.AsSpan(176), 17667);
        var board = Shipped.Load("esp32c5-devkitc");
        var image = EspImageFormat.Make(EspImageTests.Elf(0x40800000, (0x40800000, code)), board, "old.elf");

        // no mapped segments are added either: it is the plain image the ROM loads
        Assert.Single(image.Segments);
        var found = Check(image);
        Assert.Contains(found, m => m.StartsWith("Failed to fetch app description header!", StringComparison.Ordinal));
        Assert.Contains(found, m => m.Contains("rom_index == 2", StringComparison.Ordinal));

        // what the bootloader read as the eFuse block revisions: 17667 is v176.67, as in the log
        var read = new EspAppDescription
        {
            Version = "", ProjectName = "", Time = "", Date = "", IdfVersion = "", ElfSha256 = new byte[32],
            MinEfuseBlockRevision = 17667, MaxEfuseBlockRevision = 0,
        };
        Assert.Equal(["Image requires efuse blk rev >= v176.67, but chip is v0.0"],
            EspIdfBootloaderRules.CheckRevisions(image, read, 0, 0));
    }

    [Fact]
    public void The_image_passes_every_check_of_the_bootloader_on_every_chip_and_eFuse_block_revision()
    {
        var image = Image();

        Assert.Equal([], Check(image));
        // the header: chip id 23, any chip revision (0 to 65535), the SHA-256 appended
        Assert.Equal((23, 0, 65535, true), (image.ChipId, (int)image.MinRevisionFull, (int)image.MaxRevisionFull, image.DigestAppended));
        // segment 0, the description first, loaded at SRAM's start; then the two segments mapped from flash
        Assert.Equal(3, image.Segments.Count);
        Assert.Equal(0x40800000u, image.Segments[0].Address);
        Assert.All(image.Segments.Skip(1), s => Assert.True(EspIdfBootloaderRules.IsMapped(s.Address) && s.Data.Length == 0));
        foreach (var revision in new uint[] { 0, 1, 100, 102, 65534 })
        {
            Assert.Equal([], EspIdfBootloaderRules.CheckRevisions(image, EspAppDescription.Read(image.Segments[0].Data), revision, revision));
        }
    }

    [Fact]
    public void The_mapped_segments_have_their_data_at_the_offset_of_their_address_in_the_64_KB_page()
    {
        var image = Image();
        var bytes = image.Write();

        // header 24, segment 0 (8 + length), then the first mapped segment's header: its data is at the next 8 bytes
        var first = 24 + 8 + image.Segments[0].Data.Length + 8;
        Assert.Equal(0x42000000u + (uint)first, image.Segments[1].Address);
        Assert.Equal(0x42010000u + (uint)first + 8, image.Segments[2].Address);
        Assert.Equal(0x42000000u + (uint)first, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(first - 8)));
        Assert.Equal([], Check(image, 0x10000));
        Assert.Equal([], Check(image, 0x120000, 0x100000));
        // an app partition not on a 64 KB boundary would break the alignment (the partition tool does not allow one)
        Assert.Contains(Check(image, 0x12000), m => m.Contains("doesn't match data", StringComparison.Ordinal));

        // the one program in 16384 whose mapped segment would start its page: 4 bytes of zeros move it
        foreach (var size in new[] { 65536 - 40 - 256, 65536 - 40 - 256 - 8 })
        {
            var edge = Image(size);
            Assert.Equal([], Check(edge));
            Assert.Equal(size + 256 + 4, edge.Segments[0].Data.Length);
            Assert.NotEqual(0u, edge.Segments[1].Address % 0x10000);
            Assert.NotEqual(0u, edge.Segments[2].Address % 0x10000);
        }
    }

    [Fact]
    public void A_program_that_would_load_over_the_bootloaders_own_SRAM_is_a_build_error_and_a_rule_violation()
    {
        var board = Shipped.Load("esp32c5-devkitc");
        // the bootloader's code starts at 0x4084bba0 (bootloader.ld: 0x4084e5a0 - 0x2a00): up to there is the app's
        var fits = EspImageFormat.Make(EspImageTests.Elf(0x40800000, (0x40800000, Program(new byte[0x4bba0 - 256]))), board, "fits.elf");
        Assert.Equal([], Check(fits));

        var error = Assert.Throws<ChipSharpException>(() =>
            EspImageFormat.Make(EspImageTests.Elf(0x40800000, (0x40800000, Program(new byte[0x4bba0 - 256 + 4]))), board, "big.elf"));
        Assert.Contains("'big.elf' loads up to 0x4084bba3", error.Message);
        Assert.Contains("below 0x4084bba0", error.Message);

        foreach (var address in new uint[] { 0x4084bba0, 0x4084e5a0, 0x408555a0, 0x4085a000, 0x4085e59c })
        {
            var overlapping = new EspImage
            {
                ChipId = 23, Entry = 0x40800100,
                Segments = [new EspSegment(0x40800000, Image().Segments[0].Data), new EspSegment(address, [1, 2, 3, 4]), .. Image().Segments.Skip(1)],
            };
            Assert.Contains(Check(overlapping), m => m.Contains("overlaps the bootloader's SRAM", StringComparison.Ordinal));
        }

        // from the ROM's stack and data on, up to the end of SRAM, is still the bootloader's; outside SRAM is no place at all
        var outside = new EspImage
        {
            ChipId = 23, Entry = 0x40800100,
            Segments = [.. Image().Segments, new EspSegment(0x40860000, [1, 2, 3, 4]), new EspSegment(0x3fc00000, [1, 2, 3, 4])],
        };
        Assert.Equal(2, Check(outside).Count(m => m.Contains("bad load address range", StringComparison.Ordinal)));
    }

    [Fact]
    public void Each_other_rule_of_the_bootloader_names_what_it_refused()
    {
        var good = Image();
        var segments = good.Segments;
        EspImage With(EspImage image, Action<List<EspSegment>> change)
        {
            var list = image.Segments.ToList();
            change(list);
            return new EspImage
            {
                ChipId = image.ChipId, Entry = image.Entry, MinRevisionFull = image.MinRevisionFull,
                MaxRevisionFull = image.MaxRevisionFull, DigestAppended = image.DigestAppended, Segments = list,
            };
        }

        // magic word, then the description's checks
        Assert.Contains("Failed to fetch app description header!", Check(With(good, l => l[0] = new EspSegment(l[0].Address, new byte[256 + 4])))[0]);
        Assert.Contains("too short for app description", Check(With(good, l => l[0] = new EspSegment(l[0].Address, new byte[200])))[0]);
        Assert.Contains(Check(With(good, l => l[0] = new EspSegment(l[0].Address, EspAppDescription.Fill(l[0].Data, null, null).Select((b, i) => i == 180 ? (byte)14 : b).ToArray()))),
            m => m.StartsWith("MMU page size mismatch", StringComparison.Ordinal));
        var narrow = EspAppDescription.Fill(segments[0].Data, null, null);
        BinaryPrimitives.WriteUInt16LittleEndian(narrow.AsSpan(176), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(narrow.AsSpan(178), 101);
        var narrowed = Check(With(good, l => l[0] = new EspSegment(l[0].Address, narrow)));
        Assert.Contains("Image requires efuse blk rev >= v1.0, but chip is v0.0", narrowed);
        Assert.Contains(narrowed, m => m.StartsWith("Image requires efuse blk rev <= v1.1", StringComparison.Ordinal));

        // the header: the chip, the revisions of the chip, the appended hash, the partition
        var wrongChip = new EspImage { ChipId = 5, Entry = good.Entry, Segments = segments };
        Assert.Contains("mismatch chip ID, expected 23, found 5", Check(wrongChip));
        var needsNewer = new EspImage { ChipId = 23, Entry = good.Entry, MinRevisionFull = 101, Segments = segments };
        Assert.Contains(Check(needsNewer), m => m.StartsWith("chip revision check failed", StringComparison.Ordinal));
        var needsOlder = new EspImage { ChipId = 23, Entry = good.Entry, MaxRevisionFull = 99, Segments = segments };
        Assert.Contains(Check(needsOlder), m => m.Contains("<= v0.99", StringComparison.Ordinal));
        var noHash = new EspImage { ChipId = 23, Entry = good.Entry, DigestAppended = false, Segments = segments };
        Assert.Contains(Check(noHash), m => m.Contains("no appended SHA-256", StringComparison.Ordinal));
        Assert.Contains(Check(good, 0x10000, 100), m => m.StartsWith("Image length", StringComparison.Ordinal));

        // the segments: how many are mapped, a length that is not a multiple of 4, the entry point
        Assert.Contains(Check(With(good, l => l.RemoveAt(2))), m => m.Contains("rom_index == 2", StringComparison.Ordinal));
        Assert.Contains(Check(With(good, l => l.Add(new EspSegment(0x42020000, [])))), m => m.Contains("has 3 segments mapped", StringComparison.Ordinal));
        var oddEntry = new EspImage { ChipId = 23, Entry = 0x40850000, Segments = segments };
        Assert.Contains(Check(oddEntry), m => m.Contains("entry point 0x40850000", StringComparison.Ordinal));

        // the checksum and the hash: what EspImage.Read verifies
        var bytes = good.Write();
        bytes[^1] ^= 1;
        Assert.Equal(["The ESP image's SHA-256 does not match its bytes."], EspIdfBootloaderRules.Check(bytes));
        Assert.StartsWith("Not an ESP image", Assert.Single(EspIdfBootloaderRules.Check(new byte[64])));
    }

    [Fact]
    public void The_ROM_image_has_only_the_programs_segments_with_the_description_and_is_not_an_IDF_app()
    {
        var elf = EspImageTests.Elf(0x40800100, (0x40800000, Program(new byte[64])));
        var board = Shipped.Load("esp32c5-devkitc");

        var rom = EspImageFormat.Make(elf, board, "app.elf", "blink", new byte[32], app: false);
        var app = EspImageFormat.Make(elf, board, "app.elf", "blink", new byte[32]);

        // the app's first segment, then two mapped ones; the ROM image only the first, byte for byte the same
        Assert.Single(rom.Segments);
        Assert.Equal(app.Segments[0].Data, rom.Segments[0].Data);
        Assert.Equal("blink", EspAppDescription.Read(rom.Segments[0].Data).ProjectName);
        Assert.DoesNotContain(rom.Segments, s => EspIdfBootloaderRules.IsMapped(s.Address));
        Assert.Contains(EspIdfBootloaderRules.Check(rom.Write()), m => m.Contains("rom_index == 2", StringComparison.Ordinal));
        Assert.Equal([], Check(app));
        // the ROM may load up to the ROM's stack and data, which the bootloader's own SRAM does not allow
        var big = EspImageTests.Elf(0x40800100, (0x40800000, Program(new byte[0x50000])));
        Assert.Single(EspImageFormat.Make(big, board, "big.elf", null, null, app: false).Segments);
        Assert.Contains("big.elf", Assert.Throws<ChipSharpException>(() => EspImageFormat.Make(big, board, "big.elf")).Message);

        // the format names both
        var format = new EspImageFormat();
        Assert.Equal(["rom"], format.Variants.Select(v => v.Suffix));
        Assert.True(format.Variants[0].Flashed);
    }

    /// <summary>A program that waits for an interrupt for ever: <c>csrsi mstatus, 8; wfi; j -4</c> (interrupts on, so the simulator does not take the wait for the end) after the description.</summary>
    private string WaitingImage()
    {
        var path = Path.Combine(_scratch, "waiting.bin");
        Directory.CreateDirectory(_scratch);
        var image = EspImageFormat.Make(EspImageTests.Elf(0x40800100, (0x40800000, Program(0x73, 0x60, 0x04, 0x30, 0x73, 0x00, 0x50, 0x10, 0x6F, 0xF0, 0xDF, 0xFF))),
            Shipped.Load("esp32c5-devkitc"), "waiting.elf", "waiting", new byte[32]);
        // the entry is the first instruction after the description
        File.WriteAllBytes(path, new EspImage
        {
            ChipId = 23, Entry = 0x40800100, Segments = image.Segments,
        }.Write());
        return path;
    }

    [Fact]
    public void Chipsim_starts_an_image_as_the_bootloader_does_with_the_watchdog_it_leaves_running_and_the_PLL_on()
    {
        var path = WaitingImage();

        // from the bootloader: the PLL is on (the CPU at 160 MHz, said as the clock changes), the watchdog's stage 0 is
        // 9 s of the RTC slow clock, a program that does not turn it off is reset then
        var boot = Simulation.Run(SimulationOptions.Parse("esp32c5", [path, "--ms", "20000", "--idf-bootloader"]));
        Assert.Contains("RTC watchdog (RWDT) reset the chip", boot.Stopped);
        var seconds = double.Parse(boot.Lines[^1][1..boot.Lines[^1].IndexOf(" ms]", StringComparison.Ordinal)].Trim(),
            System.Globalization.CultureInfo.InvariantCulture) / 1000;
        Assert.InRange(seconds, 8.9, 9.1);
        var chip = new Esp32c5Chip(Esp32c5Image.Load(path));
        chip.StartLikeIdfBootloader();
        Assert.True(chip.PllRunning);
        Assert.Equal(160_000_000, chip.CpuHertz);
        Assert.Equal(0x4085C380u, chip.Cpu.X[2]);
    }

    [Fact]
    public void Chipsim_refuses_what_the_bootloader_would_refuse_and_what_is_not_an_ESP_image()
    {
        Directory.CreateDirectory(_scratch);
        var old = Path.Combine(_scratch, "old.bin");
        File.WriteAllBytes(old, EspImageFormat.Make(EspImageTests.Elf(0x40800000, (0x40800000, new byte[300])),
            Shipped.Load("esp32c5-devkitc"), "old.elf").Write());

        var refused = Assert.Throws<ImageException>(() => Simulation.Run(SimulationOptions.Parse("esp32c5", [old, "--idf-bootloader"])));
        Assert.Contains("ESP-IDF's bootloader would refuse", refused.Message);
        Assert.Contains("Failed to fetch app description header!", refused.Message);

        var elf = Path.Combine(_scratch, "x.elf");
        File.WriteAllBytes(elf, [0x7F, (byte)'E', (byte)'L', (byte)'F']);
        Assert.Contains("is not an ESP image", Assert.Throws<ImageException>(() =>
            Simulation.Run(SimulationOptions.Parse("esp32c5", [elf, "--idf-bootloader"]))).Message);
        Assert.Contains("--idf-bootloader is for the esp32c5", Assert.Throws<UsageException>(() =>
            Simulation.Run(SimulationOptions.Parse("pfs154", [old, "--idf-bootloader"]))).Message);
    }
}
