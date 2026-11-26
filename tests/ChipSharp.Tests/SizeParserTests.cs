using ChipSharp.Targets;

namespace ChipSharp.Tests;

public sealed class SizeParserTests
{
    [Theory]
    [InlineData("4096", 4096UL)]
    [InlineData("264K", 264UL * 1024)]
    [InlineData("2M", 2UL * 1024 * 1024)]
    [InlineData("2m", 2UL * 1024 * 1024)]
    [InlineData("1G", 1UL << 30)]
    [InlineData("0x10000000", 0x10000000UL)]
    [InlineData(" 16K ", 16UL * 1024)]
    public void Reads_sizes_and_addresses(string text, ulong expected) =>
        Assert.Equal(expected, SizeParser.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("2MB")]
    [InlineData("-4")]
    [InlineData("0xZZ")]
    public void Rejects_anything_else(string text) =>
        Assert.ThrowsAny<FormatException>(() => SizeParser.Parse(text));
}
