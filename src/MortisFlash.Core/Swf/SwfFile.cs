using System.IO.Compression;

namespace MortisFlash.Swf;

public enum SwfCompression { None, Zlib, Lzma }

/// <summary>A raw SWF tag: code, name and data (without the tag header).</summary>
public sealed class SwfTag
{
    public required int Code { get; init; }
    public required byte[] Data { get; set; }
    /// <summary>Offset of the tag in the decompressed file (useful for display).</summary>
    public int Offset { get; init; }
    /// <summary>Child tags (DefineSprite).</summary>
    public List<SwfTag>? Children { get; set; }
    /// <summary>Bytes following a sprite's End tag (some exporters append them); written back unchanged.</summary>
    public byte[]? Tail { get; set; }
    /// <summary>Parent sprite, or null for the main timeline.</summary>
    public SwfTag? Parent { get; set; }

    public string Name => TagCodes.Name(Code);
    public SwfReader Reader(int version) => new(Data) { Version = version };

    /// <summary>Character ID for Define* tags (the first two bytes).</summary>
    public int CharacterId => TagCodes.DefinesCharacter(Code) && Data.Length >= 2 ? Data[0] | Data[1] << 8 : -1;

    public override string ToString() => CharacterId >= 0 ? $"{Name} ({CharacterId})" : Name;
}

/// <summary>A decompressed SWF file, split into tags.</summary>
public sealed class SwfFile
{
    public required int Version { get; init; }
    public required SwfCompression Compression { get; init; }
    public required int FileLength { get; init; }
    public required Rect FrameSize { get; init; }
    public required double FrameRate { get; init; }
    public required int FrameCount { get; init; }
    public required List<SwfTag> Tags { get; init; }
    public byte[] Uncompressed { get; init; } = [];
    /// <summary>Offset of the first tag in <see cref="Uncompressed"/> (after the RECT, frame rate and frame count).</summary>
    public int TagsOffset { get; init; }
    /// <summary>Parsing notes (truncated file, unreadable tag, etc.).</summary>
    public List<string> Warnings { get; } = [];

    public bool IsAs3 => Tags.Any(t => t.Code is TagCodes.DoABC or TagCodes.DoABC2)
                         || (FileAttributes & 0x08) != 0;

    public int FileAttributes
    {
        get
        {
            var fa = Tags.FirstOrDefault(t => t.Code == TagCodes.FileAttributes);
            return fa is { Data.Length: >= 1 } ? fa.Data[0] : 0;
        }
    }

    public static bool LooksLikeSwf(ReadOnlySpan<byte> d) =>
        d.Length >= 8 && (d[0] is (byte)'F' or (byte)'C' or (byte)'Z') && d[1] == 'W' && d[2] == 'S' && d[3] <= 60;

    public static SwfFile Load(string path) => Parse(ExtractFromContainer(File.ReadAllBytes(path)));

    /// <summary>
    /// Also accepts Flash projectors (.exe): the SWF is appended to the end of the executable,
    /// followed by the 0xFA123456 signature and its length.
    /// </summary>
    public static byte[] ExtractFromContainer(byte[] data)
    {
        if (LooksLikeSwf(data)) return data;
        // Lowercase signature (fWS / cWS / zWS): SWF encrypted by the Harman AIR SDK
        if (data.Length > 8 && data[0] is (byte)'f' or (byte)'c' or (byte)'z' && data[1] == 'W' && data[2] == 'S')
            throw new InvalidDataException("Encrypted SWF (Harman AIR format): decryption is not supported.");
        if (data.Length > 16)
        {
            int n = data.Length;
            uint magic = BitConverter.ToUInt32(data, n - 8);
            int len = BitConverter.ToInt32(data, n - 4);
            if (magic == 0xFA123456 && len > 8 && len <= n - 8)
            {
                int start = n - 8 - len;
                if (LooksLikeSwf(data.AsSpan(start))) return data[start..(n - 8)];
            }
            // Scan the data for a signature (exe without a footer, SWF embedded elsewhere)
            for (int i = 0; i + 8 < n; i++)
            {
                if (data[i + 1] != 'W' || data[i + 2] != 'S') continue;
                if (!LooksLikeSwf(data.AsSpan(i))) continue;
                int fileLen = BitConverter.ToInt32(data, i + 4);
                if (fileLen > 20 && fileLen < 512 << 20 && (data[i] != 'F' || i + fileLen <= n))
                    return data[i..];
            }
        }
        throw new InvalidDataException("This file is not a SWF (nor a Flash projector containing a SWF).");
    }

    public static SwfFile Parse(byte[] data)
    {
        if (!LooksLikeSwf(data)) throw new InvalidDataException("Missing SWF signature (FWS / CWS / ZWS).");
        char sig = (char)data[0];
        int version = data[3];
        int fileLength = BitConverter.ToInt32(data, 4);
        var warnings = new List<string>();
        byte[] body;
        SwfCompression comp;

        switch (sig)
        {
            case 'C':
                comp = SwfCompression.Zlib;
                body = Inflate(data, 8, Math.Max(0, fileLength - 8), warnings);
                break;
            case 'Z':
                comp = SwfCompression.Lzma;
                // ZWS: compressed length (4), LZMA properties (5), data
                var props = data[12..17];
                int outSize = Math.Max(0, fileLength - 8);
                body = Lzma.Decode(data, 17, props, outSize);
                break;
            default:
                comp = SwfCompression.None;
                body = data[8..];
                break;
        }

        // Rebuild a complete uncompressed SWF (FWS header + body)
        var full = new byte[8 + body.Length];
        full[0] = (byte)'F'; full[1] = (byte)'W'; full[2] = (byte)'S'; full[3] = (byte)version;
        BitConverter.GetBytes(full.Length).CopyTo(full, 4);
        body.CopyTo(full, 8);

        var r = new SwfReader(full, 8) { Version = version };
        var frameSize = r.Rect();
        double frameRate = r.U16() / 256.0;
        int frameCount = r.U16();
        int tagsOffset = r.Position;
        var tags = ReadTags(full, tagsOffset, full.Length, version, null, warnings);

        var swf = new SwfFile
        {
            Version = version,
            Compression = comp,
            FileLength = fileLength,
            FrameSize = frameSize,
            FrameRate = frameRate,
            FrameCount = frameCount,
            Tags = tags,
            Uncompressed = full,
            TagsOffset = tagsOffset,
        };
        swf.Warnings.AddRange(warnings);
        return swf;
    }

    static byte[] Inflate(byte[] data, int offset, int expected, List<string> warnings)
    {
        using var input = new MemoryStream(data, offset, data.Length - offset);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        var output = new MemoryStream(expected > 0 && expected < 512 << 20 ? expected : 1 << 16);
        var buf = new byte[81920];
        try
        {
            int n;
            while ((n = z.Read(buf, 0, buf.Length)) > 0) output.Write(buf, 0, n);
        }
        catch (InvalidDataException)
        {
            warnings.Add("Truncated or corrupted zlib stream: partial read.");
        }
        return output.ToArray();
    }

    static List<SwfTag> ReadTags(byte[] data, int pos, int end, int version, SwfTag? parent, List<string> warnings)
    {
        var list = new List<SwfTag>();
        var r = new SwfReader(data, 0, end) { Version = version, Position = pos };
        while (r.Remaining >= 2)
        {
            int offset = r.Position;
            int header = r.U16();
            int code = header >> 6;
            int len = header & 0x3F;
            if (len == 0x3F)
            {
                if (r.Remaining < 4) break;
                len = r.S32();
            }
            if (len < 0 || len > r.Remaining)
            {
                warnings.Add($"Tag {TagCodes.Name(code)} at 0x{offset:X} extends past the end of the file: parsing stopped.");
                len = Math.Max(0, r.Remaining);
            }
            var tag = new SwfTag { Code = code, Data = r.Bytes(len), Offset = offset, Parent = parent };
            if (code == TagCodes.DefineSprite && tag.Data.Length >= 4)
            {
                try
                {
                    tag.Children = ReadTags(tag.Data, 4, tag.Data.Length, version, tag, warnings);
                }
                catch (Exception e)
                {
                    warnings.Add($"Unreadable sprite at 0x{offset:X}: {e.Message}");
                    tag.Children = [];
                }
            }
            list.Add(tag);
            if (code == TagCodes.End)
            {
                if (parent != null && r.Remaining > 0) parent.Tail = r.Rest();
                break;
            }
        }
        return list;
    }

    /// <summary>All tags, including those inside sprites (depth-first traversal).</summary>
    public IEnumerable<SwfTag> AllTags()
    {
        foreach (var t in Tags)
        {
            yield return t;
            if (t.Children != null)
                foreach (var c in t.Children) yield return c;
        }
    }
}
