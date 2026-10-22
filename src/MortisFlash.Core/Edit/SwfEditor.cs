using System.IO.Compression;
using System.Text;
using MortisFlash.Assets;
using MortisFlash.Avm1;
using MortisFlash.Avm2;
using MortisFlash.Swf;
using SkiaSharp;

namespace MortisFlash.Edit;

/// <summary>
/// SWF editing: each operation modifies tags in the document; <see cref="Rebuild"/> rewrites the file
/// and parses it again, which guarantees that what is displayed matches exactly what will be saved or played.
/// </summary>
public static partial class SwfEditor
{
    /// <summary>Writes the modified SWF, then parses it back into a new document.</summary>
    public static (SwfDocument Doc, byte[] Bytes) Rebuild(SwfDocument doc)
    {
        var bytes = SwfWriter.Write(doc.Swf);
        var newDoc = new SwfDocument(SwfFile.Parse(bytes), doc.FilePath) { IsModified = true };
        return (newDoc, bytes);
    }

    static void ReplaceTag(SwfDocument doc, SwfTag old, SwfTag replacement)
    {
        var list = old.Parent?.Children ?? doc.Swf.Tags;
        int i = list.IndexOf(old);
        if (i < 0) throw new InvalidOperationException("Tag not found in the document");
        list[i] = replacement;
    }

    static byte[] U16(int v) => [(byte)v, (byte)(v >> 8)];

    // ------------------------------------------------------------------ images

    /// <summary>
    /// Replaces an image with a PNG, JPEG or GIF file. A JPEG stays a JPEG (DefineBitsJPEG2) if the original
    /// image had no transparency; otherwise the image is stored losslessly (DefineBitsLossless2).
    /// </summary>
    public static void ReplaceImage(SwfDocument doc, SwfImage img, byte[] file)
    {
        using var bmp = SKBitmap.Decode(file) ?? throw new InvalidDataException("Unreadable image (PNG, JPEG or GIF expected).");
        if (bmp.Width > 8191 || bmp.Height > 8191) throw new InvalidDataException("Image too large for Flash (8191 px maximum).");
        if (bmp.Width != img.Width || bmp.Height != img.Height)
            doc.Errors.Add($"Image {img.Id}: new size {bmp.Width}×{bmp.Height} (originally {img.Width}×{img.Height}) — shapes that use it will tile or crop it.");
        bool jpeg = file.Length > 3 && file[0] == 0xFF && file[1] == 0xD8;
        bool originalOpaque = img.Tag.Code is TagCodes.DefineBits or TagCodes.DefineBitsJPEG2 or TagCodes.DefineBitsLossless;
        SwfTag tag;
        if (jpeg && originalOpaque)
            tag = new SwfTag { Code = TagCodes.DefineBitsJPEG2, Data = [.. U16(img.Id), .. file], Parent = img.Tag.Parent };
        else
        {
            // Premultiplied ARGB, zlib-compressed
            using var rgba = bmp.Copy(SKColorType.Rgba8888) ?? bmp;
            var src = rgba.Bytes;
            bool premul = rgba.AlphaType == SKAlphaType.Premul;
            var argb = new byte[bmp.Width * bmp.Height * 4];
            for (int i = 0; i < bmp.Width * bmp.Height; i++)
            {
                int r = src[i * 4], g = src[i * 4 + 1], b = src[i * 4 + 2], a = src[i * 4 + 3];
                if (!premul && a < 255)
                {
                    r = r * a / 255;
                    g = g * a / 255;
                    b = b * a / 255;
                }
                argb[i * 4] = (byte)a;
                argb[i * 4 + 1] = (byte)r;
                argb[i * 4 + 2] = (byte)g;
                argb[i * 4 + 3] = (byte)b;
            }
            var ms = new MemoryStream();
            ms.Write(U16(img.Id));
            ms.WriteByte(5);
            ms.Write(U16(bmp.Width));
            ms.Write(U16(bmp.Height));
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(argb);
            tag = new SwfTag { Code = TagCodes.DefineBitsLossless2, Data = ms.ToArray(), Parent = img.Tag.Parent };
        }
        ReplaceTag(doc, img.Tag, tag);
    }

    // ------------------------------------------------------------------ sounds

    /// <summary>Replaces a sound (DefineSound) with an MP3 or an 8/16-bit PCM WAV.</summary>
    public static void ReplaceSound(SwfDocument doc, SwfSound snd, byte[] file)
    {
        if (snd.Tag is not { Code: TagCodes.DefineSound } old) throw new InvalidOperationException("Only DefineSound sounds can be replaced (not streaming sounds).");
        var ms = new MemoryStream();
        ms.Write(U16(snd.Id));
        if (file.Length > 12 && file[0] == 'R' && file[1] == 'I' && file[2] == 'F' && file[3] == 'F')
        {
            var (rate, bits, channels, pcm) = ReadWav(file);
            ms.WriteByte((byte)(3 << 4 | RateIndex(rate) << 2 | (bits == 16 ? 2 : 0) | (channels == 2 ? 1 : 0)));
            ms.Write(BitConverter.GetBytes(pcm.Length / (bits / 8 * channels)));
            ms.Write(pcm);
        }
        else
        {
            var (rate, stereo, samples, start) = ScanMp3(file);
            ms.WriteByte((byte)(2 << 4 | RateIndex(rate) << 2 | 2 | (stereo ? 1 : 0)));
            ms.Write(BitConverter.GetBytes(samples));
            ms.Write(U16(0)); // SeekSamples
            ms.Write(file, start, file.Length - start);
        }
        ReplaceTag(doc, old, new SwfTag { Code = TagCodes.DefineSound, Data = ms.ToArray(), Parent = old.Parent });
    }

    static int RateIndex(int rate) => rate switch
    {
        <= 8000 => 0,
        <= 16000 => 1,
        <= 32000 => 2,
        _ => 3,
    };

    static (int Rate, int Bits, int Channels, byte[] Pcm) ReadWav(byte[] d)
    {
        int pos = 12, rate = 0, bits = 0, channels = 0, format = 0;
        byte[]? data = null;
        while (pos + 8 <= d.Length)
        {
            string id = Encoding.ASCII.GetString(d, pos, 4);
            int len = BitConverter.ToInt32(d, pos + 4);
            int body = pos + 8;
            if (id == "fmt ")
            {
                format = BitConverter.ToInt16(d, body);
                channels = BitConverter.ToInt16(d, body + 2);
                rate = BitConverter.ToInt32(d, body + 4);
                bits = BitConverter.ToInt16(d, body + 14);
            }
            else if (id == "data") data = d[body..Math.Min(d.Length, body + len)];
            pos = body + len + (len & 1);
        }
        if (format != 1 || data == null) throw new InvalidDataException("Unsupported WAV: only uncompressed PCM is accepted.");
        if (bits is not (8 or 16) || channels is not (1 or 2)) throw new InvalidDataException("WAV: 8 or 16 bits, mono or stereo only.");
        if (rate is not (5512 or 11025 or 22050 or 44100)) throw new InvalidDataException($"WAV at {rate} Hz: Flash only accepts 5512, 11025, 22050 or 44100 Hz.");
        return (rate, bits, channels, data);
    }

    static readonly int[] Mp3Bitrates1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    static readonly int[] Mp3Bitrates2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

    /// <summary>Scans the MP3 frames: sample rate, stereo, sample count and start of the frames (after ID3).</summary>
    static (int Rate, bool Stereo, int Samples, int Start) ScanMp3(byte[] d)
    {
        int pos = 0;
        if (d.Length > 10 && d[0] == 'I' && d[1] == 'D' && d[2] == '3')
            pos = 10 + (d[6] << 21 | d[7] << 14 | d[8] << 7 | d[9]);
        int start = -1, rate = 0, samples = 0;
        bool stereo = false;
        while (pos + 4 <= d.Length)
        {
            if (d[pos] != 0xFF || (d[pos + 1] & 0xE0) != 0xE0) { pos++; continue; }
            int version = d[pos + 1] >> 3 & 3; // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
            int layer = d[pos + 1] >> 1 & 3;   // 1 = layer III
            int brIdx = d[pos + 2] >> 4, srIdx = d[pos + 2] >> 2 & 3, padding = d[pos + 2] >> 1 & 1;
            if (version == 1 || layer != 1 || brIdx is 0 or 15 || srIdx == 3) { pos++; continue; }
            int[] baseRates = [44100, 48000, 32000];
            int sr = baseRates[srIdx] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            int br = (version == 3 ? Mp3Bitrates1 : Mp3Bitrates2)[brIdx] * 1000;
            int len = (version == 3 ? 144 : 72) * br / sr + padding;
            if (len < 4) { pos++; continue; }
            if (start < 0)
            {
                start = pos;
                rate = sr;
                stereo = (d[pos + 3] >> 6) != 3;
            }
            samples += version == 3 ? 1152 : 576;
            pos += len;
        }
        if (start < 0) throw new InvalidDataException("No MP3 frames found.");
        if (rate is not (11025 or 22050 or 44100 or 5512))
            throw new InvalidDataException($"MP3 at {rate} Hz: Flash only accepts 11025, 22050 or 44100 Hz.");
        return (rate, stereo, samples, start);
    }

    // ------------------------------------------------------------------ data, texts

    public static void ReplaceBinary(BinaryItem item, byte[] data)
    {
        var tag = item.Tag ?? throw new InvalidOperationException("Missing tag");
        tag.Data = [.. tag.Data[..6], .. data];
    }

    /// <summary>Changes the initial text of a text field (DefineEditText).</summary>
    public static void SetEditText(SwfText text, string value)
    {
        var tag = text.Tag ?? throw new InvalidOperationException("Missing tag");
        if (!text.IsEditText || text.VariableOffset < 0) throw new InvalidOperationException("Only text fields (DefineEditText) can be edited.");
        var data = tag.Data;
        var head = data[..text.VariableOffset];
        head[text.FlagsOffset] |= 0x80; // HasText
        var ms = new MemoryStream();
        ms.Write(head);
        ms.Write(Encoding.UTF8.GetBytes(text.VariableName));
        ms.WriteByte(0);
        ms.Write(Encoding.UTF8.GetBytes(value));
        ms.WriteByte(0);
        tag.Data = ms.ToArray();
    }

    // ------------------------------------------------------------------ scripts

    /// <summary>Assembles the AS2 p-code and replaces the actions at their original location.</summary>
    public static void ReplaceAs2(As2Location loc, string pcode) => ReplaceAs2Code(loc, Avm1Assembler.Assemble(pcode));

    /// <summary>Compiles ActionScript 1/2 source and replaces the actions at their original location.</summary>
    public static void CompileAs2(As2Location loc, string source, int swfVersion) => ReplaceAs2Code(loc, Compiler.Avm1Compiler.Compile(source, swfVersion));

    static void ReplaceAs2Code(As2Location loc, byte[] code)
    {
        var data = loc.Tag.Data;
        var result = new byte[data.Length - loc.Length + code.Length];
        Array.Copy(data, result, loc.Offset);
        code.CopyTo(result, loc.Offset);
        Array.Copy(data, loc.Offset + loc.Length, result, loc.Offset + code.Length, data.Length - loc.Offset - loc.Length);
        if (loc.SizeField >= 0)
        {
            int size = code.Length + loc.SizeExtra;
            if (loc.SizeBytes == 2 && size > 0xFFFF) throw new InvalidDataException("Button handler too long (64 KB maximum).");
            for (int i = 0; i < loc.SizeBytes; i++) result[loc.SizeField + i] = (byte)(size >> (8 * i));
        }
        loc.Tag.Data = result;
    }

    /// <summary>Assembles the p-code of an AS3 method and rewrites the DoABC tag that contains it.</summary>
    public static void ReplaceAs3Method(AbcFile abc, MethodInfo method, string pcode)
    {
        Avm2Assembler.Replace(abc, method, pcode);
        WriteAbc(abc);
    }

    /// <summary>Compiles the ActionScript 3 body of a method and rewrites the DoABC tag that contains it.</summary>
    public static void CompileAs3Method(AbcFile abc, MethodInfo method, MethodOwner owner, string source)
    {
        Compiler.Avm2Compiler.CompileMethodBody(abc, method, owner, source);
        WriteAbc(abc);
    }

    static void WriteAbc(AbcFile abc)
    {
        var tag = abc.SourceTag ?? throw new InvalidOperationException("DoABC tag not found");
        var bytes = AbcWriter.Write(abc);
        if (tag.Code == TagCodes.DoABC2)
        {
            var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(abc.DoAbcFlags));
            ms.Write(Encoding.UTF8.GetBytes(abc.Name));
            ms.WriteByte(0);
            ms.Write(bytes);
            tag.Data = ms.ToArray();
        }
        else tag.Data = bytes;
    }
}
