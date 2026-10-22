using System.IO.Compression;

namespace MortisFlash.Swf;

/// <summary>Writes a <see cref="SwfFile"/> (with possibly modified tags) back to a SWF file.</summary>
public static class SwfWriter
{
    /// <summary>
    /// Serializes the file. Since no LZMA encoder is available here, a "ZWS" SWF is rewritten as zlib ("CWS"),
    /// which every player accepts.
    /// </summary>
    public static byte[] Write(SwfFile swf, SwfCompression? compression = null)
    {
        var comp = compression ?? swf.Compression;
        if (comp == SwfCompression.Lzma) comp = SwfCompression.Zlib;
        if (comp == SwfCompression.Zlib && swf.Version < 6) comp = SwfCompression.None;

        var body = new MemoryStream();
        // Stage RECT, frame rate and frame count: copied unchanged
        body.Write(swf.Uncompressed, 8, swf.TagsOffset - 8);
        WriteTags(body, swf.Tags);
        var bodyBytes = body.ToArray();
        int fileLength = 8 + bodyBytes.Length;

        var output = new MemoryStream();
        output.WriteByte(comp switch { SwfCompression.Zlib => (byte)'C', _ => (byte)'F' });
        output.WriteByte((byte)'W');
        output.WriteByte((byte)'S');
        output.WriteByte((byte)swf.Version);
        output.Write(BitConverter.GetBytes(fileLength));
        if (comp == SwfCompression.Zlib)
        {
            using var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true);
            z.Write(bodyBytes);
        }
        else output.Write(bodyBytes);
        return output.ToArray();
    }

    static void WriteTags(Stream s, List<SwfTag> tags)
    {
        bool hasEnd = false;
        foreach (var t in tags)
        {
            byte[] data = t.Data;
            if (t.Code == TagCodes.DefineSprite && t.Children != null && t.Data.Length >= 4)
            {
                var ms = new MemoryStream();
                ms.Write(t.Data, 0, 4);
                WriteTags(ms, t.Children);
                if (t.Tail != null) ms.Write(t.Tail);
                data = ms.ToArray();
            }
            WriteTag(s, t.Code, data);
            if (t.Code == TagCodes.End) hasEnd = true;
        }
        if (!hasEnd) WriteTag(s, TagCodes.End, []);
    }

    /// <summary>Uses the short header when possible; bitmaps and sound blocks keep the long header, as Flash does.</summary>
    public static void WriteTag(Stream s, int code, byte[] data)
    {
        bool longForm = data.Length >= 63 || code is TagCodes.DefineBits or TagCodes.DefineBitsJPEG2 or TagCodes.DefineBitsJPEG3
            or TagCodes.DefineBitsJPEG4 or TagCodes.DefineBitsLossless or TagCodes.DefineBitsLossless2 or TagCodes.SoundStreamBlock;
        if (longForm)
        {
            Short(s, code << 6 | 0x3F);
            s.Write(BitConverter.GetBytes(data.Length));
        }
        else Short(s, code << 6 | data.Length);
        s.Write(data);
    }

    static void Short(Stream s, int v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
    }
}
