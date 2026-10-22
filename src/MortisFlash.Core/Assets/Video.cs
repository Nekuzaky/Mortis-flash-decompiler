using MortisFlash.Swf;

namespace MortisFlash.Assets;

public sealed class SwfVideo
{
    public required int Id { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Codec { get; init; }
    public required int FrameCount { get; init; }
    public required byte[] Flv { get; init; }
    public SwfTag? Tag { get; init; }

    public string CodecName => Codec switch
    {
        2 => "Sorenson H.263", 3 => "Screen video", 4 => "On2 VP6", 5 => "On2 VP6 alpha", 6 => "Screen video 2", 7 => "H.264",
        _ => $"codec {Codec}",
    };
}

/// <summary>Rebuilds an FLV file from a DefineVideoStream and its VideoFrame tags.</summary>
public static class VideoExtractor
{
    public static SwfVideo? Extract(SwfTag define, IEnumerable<SwfTag> allTags, double frameRate)
    {
        var r = new SwfReader(define.Data);
        int id = r.U16();
        r.U16(); // declared frame count
        int w = r.U16(), h = r.U16();
        r.U8();
        int codec = r.U8();
        var frames = new List<(int Num, byte[] Data)>();
        foreach (var t in allTags)
        {
            if (t.Code != TagCodes.VideoFrame || t.Data.Length < 4) continue;
            if ((t.Data[0] | t.Data[1] << 8) != id) continue;
            frames.Add((t.Data[2] | t.Data[3] << 8, t.Data[4..]));
        }
        if (frames.Count == 0) return null;
        frames.Sort((a, b) => a.Num.CompareTo(b.Num));

        var ms = new MemoryStream();
        ms.Write("FLV"u8);
        ms.WriteByte(1);
        ms.WriteByte(1); // video only
        WriteU32Be(ms, 9);
        WriteU32Be(ms, 0);
        double fps = frameRate > 0 ? frameRate : 24;
        bool first = true;
        foreach (var (num, data) in frames)
        {
            bool key = first || IsKeyFrame(codec, data);
            first = false;
            var payload = new MemoryStream();
            payload.WriteByte((byte)((key ? 1 : 2) << 4 | codec));
            if (codec is 4 or 5) payload.WriteByte(0); // FLV-specific VP6 adjustment byte
            payload.Write(data);
            var p = payload.ToArray();
            int ts = (int)(num * 1000 / fps);
            ms.WriteByte(9);
            WriteU24Be(ms, p.Length);
            WriteU24Be(ms, ts & 0xFFFFFF);
            ms.WriteByte((byte)(ts >> 24));
            WriteU24Be(ms, 0);
            ms.Write(p);
            WriteU32Be(ms, p.Length + 11);
        }
        return new SwfVideo { Id = id, Width = w, Height = h, Codec = codec, FrameCount = frames.Count, Flv = ms.ToArray(), Tag = define };
    }

    static bool IsKeyFrame(int codec, byte[] d)
    {
        if (d.Length < 2) return false;
        switch (codec)
        {
            case 4:
            case 5:
            {
                int off = codec == 5 ? 3 : 0;
                return d.Length > off && (d[off] & 0x80) == 0;
            }
            case 2:
            {
                try
                {
                    var r = new SwfReader(d);
                    r.UB(17); r.UB(5); r.UB(8);
                    int size = (int)r.UB(3);
                    if (size == 0) { r.UB(8); r.UB(8); }
                    else if (size == 1) { r.UB(16); r.UB(16); }
                    return r.UB(2) == 0;
                }
                catch { return false; }
            }
            case 3:
            case 6:
                return (d[0] >> 4) == 1;
        }
        return false;
    }

    static void WriteU32Be(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    static void WriteU24Be(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }
}
