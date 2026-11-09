using System.Buffers.Binary;

namespace PowerMode.Core.Presentation;

/// <summary>One square image of an .ico file, PNG-encoded.</summary>
public sealed record IcoImage(int Size, byte[] Png);

/// <summary>
/// Writes an .ico file whose images are stored as PNG (supported by Windows since Vista, also for the exe's
/// ApplicationIcon). Layout: ICONDIR, one ICONDIRENTRY per image, then the image data in the same order.
/// </summary>
public static class IcoWriter
{
    private const int HeaderSize = 6;
    private const int EntrySize = 16;
    private const ushort IconType = 1;
    private const ushort Planes = 1;
    private const ushort BitsPerPixel = 32;
    private const int MaxSize = 256;

    public static byte[] Write(IReadOnlyList<IcoImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0 || images.Count > ushort.MaxValue)
        {
            throw new ArgumentException("An icon needs 1 to 65535 images.", nameof(images));
        }

        if (images.Any(i => i.Size is < 1 or > MaxSize))
        {
            throw new ArgumentException($"Icon images are 1 to {MaxSize} px.", nameof(images));
        }

        var output = new byte[HeaderSize + EntrySize * images.Count + images.Sum(i => i.Png.Length)];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], IconType);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)images.Count);
        var offset = HeaderSize + EntrySize * images.Count;
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var entry = span.Slice(HeaderSize + EntrySize * i, EntrySize);

            // Width and height are one byte each; 0 means 256.
            entry[0] = (byte)(image.Size % MaxSize);
            entry[1] = (byte)(image.Size % MaxSize);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], Planes);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], BitsPerPixel);
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..], image.Png.Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], offset);
            image.Png.CopyTo(span[offset..]);
            offset += image.Png.Length;
        }

        return output;
    }
}
