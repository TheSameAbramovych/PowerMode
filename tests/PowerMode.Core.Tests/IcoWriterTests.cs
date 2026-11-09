using System.Buffers.Binary;
using PowerMode.Core.Cli;
using PowerMode.Core.Presentation;

namespace PowerMode.Core.Tests;

public sealed class IcoWriterTests
{
    [Fact]
    public void Writes_a_directory_entry_per_image_and_the_data_after_it()
    {
        byte[] small = [1, 2, 3];
        byte[] large = [9, 8, 7, 6, 5];

        var ico = IcoWriter.Write([new IcoImage(16, small), new IcoImage(256, large)]);

        Assert.Equal(6 + 2 * 16 + 8, ico.Length);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)));

        // 16 px entry
        Assert.Equal(16, ico[6]);
        Assert.Equal(16, ico[7]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(6 + 4)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(6 + 6)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(6 + 8)));
        Assert.Equal(38, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(6 + 12)));

        // 256 px is written as 0
        Assert.Equal(0, ico[22]);
        Assert.Equal(0, ico[23]);
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(22 + 8)));
        Assert.Equal(41, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(22 + 12)));
        Assert.Equal(small, ico[38..41]);
        Assert.Equal(large, ico[41..]);
    }

    [Fact]
    public void Rejects_no_images_and_bad_sizes()
    {
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([]));
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([new IcoImage(512, [1])]));
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([new IcoImage(0, [1])]));
    }

    [Fact]
    public void Make_icon_takes_a_file()
    {
        var options = CliOptions.Parse(["--make-icon", @"src\PowerMode.App\Assets\PowerMode.ico"]);

        Assert.Equal(CliCommand.MakeIcon, options.Command);
        Assert.Equal(@"src\PowerMode.App\Assets\PowerMode.ico", options.IconFile);
        Assert.False(options.IsForwardable);
        Assert.False(CliOptions.Parse(["--make-icon"]).IsValid);
        Assert.False(CliOptions.Parse(["--make-icon", "--status"]).IsValid);
        Assert.Contains("--make-icon", CliOptions.Usage, StringComparison.Ordinal);
    }
}
