using MortisFlash.Assets;
using MortisFlash.Swf;

namespace MortisFlash.Edit;

// Stream sounds (timeline music) and static texts.
public static partial class SwfEditor
{
    // ------------------------------------------------------------------ stream sounds

    /// <summary>
    /// Replaces the stream sound of a timeline (SoundStreamHead + one SoundStreamBlock per frame) with an MP3 or a
    /// 16-bit PCM WAV. The audio is cut into per-frame blocks at the movie's frame rate. Returns a note when the
    /// sound is longer than the timeline and had to be truncated.
    /// </summary>
    public static string? ReplaceStreamSound(SwfDocument doc, SwfSound snd, byte[] file)
    {
        var head = snd.Tag ?? throw new InvalidOperationException("Stream header not found");
        if (head.Code is not (TagCodes.SoundStreamHead or TagCodes.SoundStreamHead2))
            throw new InvalidOperationException("This sound is not a stream sound.");
        var timeline = head.Parent?.Children ?? doc.Swf.Tags;
        double fps = doc.Swf.FrameRate > 0 ? doc.Swf.FrameRate : 24;
        int frames = timeline.Count(t => t.Code == TagCodes.ShowFrame);
        if (frames == 0) throw new InvalidOperationException("The timeline has no frames.");

        bool isWav = file.Length > 12 && file[0] == 'R' && file[1] == 'I' && file[2] == 'F' && file[3] == 'F';
        int rate, format;
        bool stereo;
        var blocks = new List<byte[]>();
        double perFrame;
        long total;
        if (isWav)
        {
            var (r, bits, channels, pcm) = ReadWav(file);
            if (bits != 16) throw new InvalidDataException("Stream sounds need 16-bit WAV audio.");
            (rate, format, stereo) = (r, 3, channels == 2);
            perFrame = rate / fps;
            int frameBytes = 2 * channels;
            total = pcm.Length / frameBytes;
            long done = 0;
            for (int k = 0; k < frames && done < total; k++)
            {
                long target = Math.Min(total, (long)Math.Round((k + 1) * perFrame));
                blocks.Add(pcm[(int)(done * frameBytes)..(int)(target * frameBytes)]);
                done = target;
            }
        }
        else
        {
            var mp3 = Mp3Frames(file);
            if (mp3.Count == 0) throw new InvalidDataException("No MP3 frames found.");
            (rate, format, stereo) = (mp3[0].Rate, 2, mp3[0].Stereo);
            if (rate is not (5512 or 11025 or 22050 or 44100))
                throw new InvalidDataException($"MP3 at {rate} Hz: Flash only accepts 11025, 22050 or 44100 Hz.");
            perFrame = rate / fps;
            total = mp3.Sum(f => (long)f.Samples);
            int next = 0;
            long done = 0;
            for (int k = 0; k < frames && next < mp3.Count; k++)
            {
                long target = (long)Math.Round((k + 1) * perFrame);
                var ms = new MemoryStream();
                int samples = 0;
                while (next < mp3.Count && done < target)
                {
                    var f = mp3[next++];
                    ms.Write(file, f.Offset, f.Length);
                    samples += f.Samples;
                    done += f.Samples;
                }
                // SampleCount, SeekSamples, then the MP3 frames of this SWF frame
                blocks.Add([.. U16(samples), .. U16(0), .. ms.ToArray()]);
            }
        }

        int rateIndex = RateIndex(rate);
        int flags = rateIndex << 2 | 1 << 1 | (stereo ? 1 : 0);
        var headData = new List<byte> { (byte)flags, (byte)(format << 4 | flags) };
        headData.AddRange(U16((int)Math.Round(perFrame)));
        if (format == 2) headData.AddRange(U16(0)); // LatencySeek
        var newHead = new SwfTag { Code = TagCodes.SoundStreamHead2, Data = [.. headData], Parent = head.Parent };

        // Rebuild the timeline: new header in place of the old one, one block before each ShowFrame
        var rebuilt = new List<SwfTag>(timeline.Count + blocks.Count);
        int frame = 0;
        foreach (var t in timeline)
        {
            if (t.Code == TagCodes.SoundStreamBlock) continue;
            if (ReferenceEquals(t, head))
            {
                rebuilt.Add(newHead);
                continue;
            }
            if (t.Code == TagCodes.ShowFrame && frame < blocks.Count)
                rebuilt.Add(new SwfTag { Code = TagCodes.SoundStreamBlock, Data = blocks[frame], Parent = head.Parent });
            if (t.Code == TagCodes.ShowFrame) frame++;
            rebuilt.Add(t);
        }
        timeline.Clear();
        timeline.AddRange(rebuilt);

        double seconds = total / (double)rate, length = frames / fps;
        return seconds > length + 0.05
            ? $"The sound lasts {seconds:0.0} s but the timeline only {length:0.0} s ({frames} frames at {fps:0.##} fps): the end was cut."
            : null;
    }

    sealed record Mp3Frame(int Offset, int Length, int Samples, int Rate, bool Stereo);

    static List<Mp3Frame> Mp3Frames(byte[] d)
    {
        var list = new List<Mp3Frame>();
        int pos = 0;
        if (d.Length > 10 && d[0] == 'I' && d[1] == 'D' && d[2] == '3')
            pos = 10 + (d[6] << 21 | d[7] << 14 | d[8] << 7 | d[9]);
        int[] baseRates = [44100, 48000, 32000];
        while (pos + 4 <= d.Length)
        {
            if (d[pos] != 0xFF || (d[pos + 1] & 0xE0) != 0xE0) { pos++; continue; }
            int version = d[pos + 1] >> 3 & 3, layer = d[pos + 1] >> 1 & 3;
            int brIdx = d[pos + 2] >> 4, srIdx = d[pos + 2] >> 2 & 3, padding = d[pos + 2] >> 1 & 1;
            if (version == 1 || layer != 1 || brIdx is 0 or 15 || srIdx == 3) { pos++; continue; }
            int sr = baseRates[srIdx] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            int br = (version == 3 ? Mp3Bitrates1 : Mp3Bitrates2)[brIdx] * 1000;
            int len = (version == 3 ? 144 : 72) * br / sr + padding;
            if (len < 4 || pos + len > d.Length) break;
            list.Add(new Mp3Frame(pos, len, version == 3 ? 1152 : 576, sr, (d[pos + 3] >> 6) != 3));
            pos += len;
        }
        return list;
    }

    // ------------------------------------------------------------------ static texts

    /// <summary>
    /// Rewrites a static text (DefineText / DefineText2) with new content, using the glyphs embedded in its font.
    /// Keeps the position, size, color and font of the original first line; '\n' starts a new line.
    /// </summary>
    public static void SetStaticText(SwfDocument doc, SwfText text, string value)
    {
        var tag = text.Tag ?? throw new InvalidOperationException("Tag not found");
        if (text.IsEditText || text.RecordsOffset < 0) throw new InvalidOperationException("Not a static text.");
        var first = text.Runs.FirstOrDefault() ?? throw new InvalidOperationException("The text has no glyphs to take the font and size from.");
        if (!doc.Fonts.TryGetValue(first.FontId, out var font)) throw new InvalidOperationException($"Font {first.FontId} not found.");

        var index = new Dictionary<int, int>();
        for (int g = 0; g < font.Codes.Count; g++) index.TryAdd(font.Codes[g], g);
        double scale = first.Height / (double)font.EmSquare;
        var distinctY = text.Runs.Select(r => r.Y).Distinct().ToList();
        int lineHeight = distinctY.Count > 1 ? Math.Abs(distinctY[1] - distinctY[0])
            : font.Ascent + font.Descent > 0 ? (int)Math.Round((font.Ascent + font.Descent + font.Leading) * scale)
            : (int)(first.Height * 1.2);

        // Lay out every line: (glyph index, advance) pairs
        var lines = new List<List<(int Glyph, int Advance)>>();
        foreach (var line in value.Replace("\r\n", "\n").Split('\n'))
        {
            var glyphs = new List<(int, int)>();
            foreach (var rune in line.EnumerateRunes())
            {
                if (!index.TryGetValue(rune.Value, out int g))
                    throw new InvalidDataException($"The font “{font.Name}” does not contain the character “{rune}”: a static text can only use the characters embedded in its font.");
                int adv = g < font.Advances.Count
                    ? (int)Math.Round(font.Advances[g] * scale)
                    : (int)Math.Round(first.Glyphs.Count > 0 ? first.Glyphs.Average(x => x.Advance) : first.Height * 0.6);
                glyphs.Add((g, adv));
            }
            lines.Add(glyphs);
        }
        int maxGlyph = Math.Max(1, lines.SelectMany(l => l).Select(x => x.Glyph).DefaultIfEmpty(0).Max());
        int maxAdvance = Math.Max(1, lines.SelectMany(l => l).Select(x => Math.Abs(x.Advance)).DefaultIfEmpty(0).Max());
        int glyphBits = BitsFor(maxGlyph), advanceBits = BitsFor(maxAdvance) + 1;
        bool rgba = tag.Code == TagCodes.DefineText2;

        var w = new BitWriter();
        w.Byte(glyphBits);
        w.Byte(advanceBits);
        for (int li = 0; li < lines.Count; li++)
        {
            var glyphs = lines[li];
            int y = first.Y + li * lineHeight;
            // A record holds at most 255 glyphs
            for (int start = 0; start == 0 || start < glyphs.Count; start += 255)
            {
                var chunk = glyphs.Skip(start).Take(255).ToList();
                bool style = li == 0 && start == 0;
                int x = first.X + glyphs.Take(start).Sum(g => g.Advance);
                w.Byte(0x80 | (style ? 0x08 | 0x04 : 0) | 0x02 | 0x01);
                if (style)
                {
                    w.U16(first.FontId);
                    w.Byte(first.Color.R);
                    w.Byte(first.Color.G);
                    w.Byte(first.Color.B);
                    if (rgba) w.Byte(first.Color.A);
                }
                w.U16((ushort)(short)x);
                w.U16((ushort)(short)y);
                if (style) w.U16(first.Height);
                w.Byte(chunk.Count);
                foreach (var (g, adv) in chunk)
                {
                    w.Bits(g, glyphBits);
                    w.Bits(adv, advanceBits);
                }
                w.Align();
            }
        }
        w.Byte(0);
        tag.Data = [.. tag.Data[..text.RecordsOffset], .. w.ToArray()];
    }

    static int BitsFor(int value)
    {
        int bits = 1;
        while ((1 << bits) <= value) bits++;
        return bits;
    }

    /// <summary>MSB-first bit writer for SWF bit fields.</summary>
    sealed class BitWriter
    {
        readonly MemoryStream _out = new();
        int _acc, _count;

        public void Bits(int value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _acc = _acc << 1 | (value >> i & 1);
                if (++_count == 8)
                {
                    _out.WriteByte((byte)_acc);
                    _acc = _count = 0;
                }
            }
        }

        public void Align()
        {
            if (_count > 0) Bits(0, 8 - _count);
        }

        public void Byte(int v)
        {
            Align();
            _out.WriteByte((byte)v);
        }

        public void U16(int v)
        {
            Byte(v & 0xFF);
            Byte(v >> 8 & 0xFF);
        }

        public byte[] ToArray()
        {
            Align();
            return _out.ToArray();
        }
    }
}
