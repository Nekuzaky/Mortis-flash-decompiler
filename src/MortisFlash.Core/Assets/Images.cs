using System.IO.Compression;
using MortisFlash.Swf;
using SkiaSharp;

namespace MortisFlash.Assets;

/// <summary>Image extracted from a DefineBits* tag: exportable file plus a bitmap decoded on demand.</summary>
public sealed class SwfImage
{
    public required int Id { get; init; }
    public required SwfTag Tag { get; init; }
    /// <summary>Bytes of the exported file (JPEG, PNG or GIF).</summary>
    public required byte[] FileBytes { get; init; }
    public required string Extension { get; init; }
    public string Format { get; init; } = "";
    SKBitmap? _bitmap;
    readonly Func<SKBitmap?>? _decode;

    internal SwfImage(Func<SKBitmap?>? decode) => _decode = decode;

    /// <summary>Unpremultiplied (RGBA) bitmap, or null if the format cannot be decoded.</summary>
    public SKBitmap? Bitmap
    {
        get
        {
            if (_bitmap == null && _decode != null)
            {
                try { _bitmap = _decode(); } catch { _bitmap = null; }
            }
            return _bitmap;
        }
    }

    public int Width => Bitmap?.Width ?? 0;
    public int Height => Bitmap?.Height ?? 0;
}

public static class ImageDecoder
{
    public static SwfImage? Decode(SwfTag tag, byte[]? jpegTables)
    {
        var r = new SwfReader(tag.Data);
        int id = r.U16();
        switch (tag.Code)
        {
            case TagCodes.DefineBits:
            {
                var img = r.Rest();
                byte[] jpeg = jpegTables is { Length: > 0 } ? Concat(jpegTables, img) : img;
                jpeg = CleanJpeg(jpeg);
                return new SwfImage(() => SKBitmap.Decode(jpeg)) { Id = id, Tag = tag, FileBytes = jpeg, Extension = ".jpg", Format = "JPEG" };
            }
            case TagCodes.DefineBitsJPEG2:
            {
                var data = r.Rest();
                return FromEncoded(id, tag, data);
            }
            case TagCodes.DefineBitsJPEG3:
            case TagCodes.DefineBitsJPEG4:
            {
                int alphaOffset = (int)r.U32();
                if (tag.Code == TagCodes.DefineBitsJPEG4) r.U16(); // deblocking
                var data = r.Bytes(alphaOffset);
                var alphaZ = r.Rest();
                if (!IsJpeg(data) || alphaZ.Length == 0) return FromEncoded(id, tag, data);
                var jpeg = CleanJpeg(data);
                SKBitmap? Build()
                {
                    var bmp = SKBitmap.Decode(jpeg);
                    if (bmp == null) return null;
                    var alpha = InflateSafe(alphaZ);
                    return ApplyAlpha(bmp, alpha);
                }
                var built = Build();
                if (built == null)
                    return new SwfImage(null) { Id = id, Tag = tag, FileBytes = jpeg, Extension = ".jpg", Format = "JPEG+alpha" };
                var png = EncodePng(built);
                return new SwfImage(() => built) { Id = id, Tag = tag, FileBytes = png, Extension = ".png", Format = "JPEG+alpha" };
            }
            case TagCodes.DefineBitsLossless:
            case TagCodes.DefineBitsLossless2:
            {
                var bmp = DecodeLossless(tag);
                var png = bmp != null ? EncodePng(bmp) : [];
                return new SwfImage(() => bmp)
                {
                    Id = id, Tag = tag, FileBytes = png, Extension = ".png",
                    Format = tag.Code == TagCodes.DefineBitsLossless2 ? "Lossless RGBA" : "Lossless RGB",
                };
            }
        }
        return null;
    }

    static SwfImage FromEncoded(int id, SwfTag tag, byte[] data)
    {
        if (IsPng(data)) return new SwfImage(() => SKBitmap.Decode(data)) { Id = id, Tag = tag, FileBytes = data, Extension = ".png", Format = "PNG" };
        if (IsGif(data)) return new SwfImage(() => SKBitmap.Decode(data)) { Id = id, Tag = tag, FileBytes = data, Extension = ".gif", Format = "GIF" };
        var jpeg = CleanJpeg(data);
        return new SwfImage(() => SKBitmap.Decode(jpeg)) { Id = id, Tag = tag, FileBytes = jpeg, Extension = ".jpg", Format = "JPEG" };
    }

    public static bool IsPng(byte[] d) => d.Length > 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G';
    public static bool IsGif(byte[] d) => d.Length > 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F';
    public static bool IsJpeg(byte[] d) => d.Length > 4 && d[0] == 0xFF && (d[1] == 0xD8 || d[1] == 0xD9);

    static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    /// <summary>
    /// SWF JPEGs often contain stray EOI/SOI markers (FFD9FFD8 header, concatenated
    /// JPEGTables): the stream is rebuilt with a single SOI and a single EOI.
    /// </summary>
    public static byte[] CleanJpeg(byte[] d)
    {
        var o = new MemoryStream(d.Length + 4);
        o.WriteByte(0xFF); o.WriteByte(0xD8);
        int i = 0, n = d.Length;
        while (i + 1 < n)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m is 0xD8 or 0xD9 or 0x01 || m is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            if (i + 3 >= n) break;
            int len = d[i + 2] << 8 | d[i + 3];
            int segEnd = Math.Min(n, i + 2 + len);
            o.Write(d, i, segEnd - i);
            i = segEnd;
            if (m == 0xDA)
            {
                // Entropy-coded data: up to the next marker that is neither byte stuffing nor RST
                int start = i;
                while (i + 1 < n && !(d[i] == 0xFF && d[i + 1] != 0x00 && d[i + 1] is not (>= 0xD0 and <= 0xD7))) i++;
                if (i + 1 >= n) i = n;
                o.Write(d, start, i - start);
            }
        }
        o.WriteByte(0xFF); o.WriteByte(0xD9);
        return o.ToArray();
    }

    public static byte[] InflateSafe(byte[] z)
    {
        try
        {
            using var input = new MemoryStream(z);
            using var s = new ZLibStream(input, CompressionMode.Decompress);
            var o = new MemoryStream();
            var buf = new byte[65536];
            try
            {
                int k;
                while ((k = s.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, k);
            }
            catch (InvalidDataException) { }
            return o.ToArray();
        }
        catch
        {
            return [];
        }
    }

    static SKBitmap ApplyAlpha(SKBitmap src, byte[] alpha)
    {
        var bmp = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        int count = src.Width * src.Height;
        var px = new byte[count * 4];
        using (var rgba = src.Copy(SKColorType.Rgba8888) ?? src)
        {
            var srcBytes = rgba.Bytes;
            for (int i = 0; i < count; i++)
            {
                int a = i < alpha.Length ? alpha[i] : 255;
                int r = srcBytes[i * 4], g = srcBytes[i * 4 + 1], b = srcBytes[i * 4 + 2];
                // Colors in JPEGs with alpha are premultiplied
                if (a > 0 && a < 255)
                {
                    r = Math.Min(255, r * 255 / a);
                    g = Math.Min(255, g * 255 / a);
                    b = Math.Min(255, b * 255 / a);
                }
                px[i * 4] = (byte)r; px[i * 4 + 1] = (byte)g; px[i * 4 + 2] = (byte)b; px[i * 4 + 3] = (byte)a;
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        src.Dispose();
        return bmp;
    }

    public static SKBitmap? DecodeLossless(SwfTag tag)
    {
        var r = new SwfReader(tag.Data);
        r.U16();
        int format = r.U8();
        int w = r.U16(), h = r.U16();
        bool alpha = tag.Code == TagCodes.DefineBitsLossless2;
        int colors = format == 3 ? r.U8() + 1 : 0;
        var data = InflateSafe(r.Rest());
        if (w == 0 || h == 0) return null;
        var px = new byte[w * h * 4];
        int p = 0;
        switch (format)
        {
            case 3:
            {
                int entry = alpha ? 4 : 3;
                int tableLen = colors * entry;
                int stride = (w + 3) & ~3;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int idx = tableLen + y * stride + x;
                    int c = idx < data.Length ? data[idx] : 0;
                    int o = (y * w + x) * 4;
                    if (c < colors && c * entry + entry - 1 < data.Length)
                    {
                        int ci = c * entry;
                        byte a = alpha ? data[ci + 3] : (byte)255;
                        Unpremul(px, o, data[ci], data[ci + 1], data[ci + 2], a, alpha);
                    }
                }
                break;
            }
            case 4:
            {
                int stride = (w * 2 + 3) & ~3;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 2;
                    if (i + 1 >= data.Length) break;
                    int v = data[i] << 8 | data[i + 1];
                    int o = (y * w + x) * 4;
                    px[o] = (byte)((v >> 10 & 31) * 255 / 31);
                    px[o + 1] = (byte)((v >> 5 & 31) * 255 / 31);
                    px[o + 2] = (byte)((v & 31) * 255 / 31);
                    px[o + 3] = 255;
                }
                break;
            }
            case 5:
                for (int i = 0; i < w * h; i++, p += 4)
                {
                    if (p + 3 >= data.Length) break;
                    byte a = alpha ? data[p] : (byte)255;
                    Unpremul(px, i * 4, data[p + 1], data[p + 2], data[p + 3], a, alpha);
                }
                break;
            default:
                return null;
        }
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        return bmp;
    }

    static void Unpremul(byte[] px, int o, byte r, byte g, byte b, byte a, bool premultiplied)
    {
        if (premultiplied && a > 0 && a < 255)
        {
            px[o] = (byte)Math.Min(255, r * 255 / a);
            px[o + 1] = (byte)Math.Min(255, g * 255 / a);
            px[o + 2] = (byte)Math.Min(255, b * 255 / a);
        }
        else
        {
            px[o] = r; px[o + 1] = g; px[o + 2] = b;
        }
        px[o + 3] = a;
    }

    public static byte[] EncodePng(SKBitmap bmp)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
