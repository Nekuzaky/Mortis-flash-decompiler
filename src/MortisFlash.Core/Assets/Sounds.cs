using MortisFlash.Swf;

namespace MortisFlash.Assets;

public sealed class SwfSound
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Codec { get; init; }
    public required int Rate { get; init; }
    public required bool Stereo { get; init; }
    public required bool Is16Bit { get; init; }
    public required int SampleCount { get; init; }
    /// <summary>Exportable file (MP3, WAV, or raw data for Nellymoser/Speex).</summary>
    public required byte[] FileBytes { get; init; }
    public required string Extension { get; init; }
    public SwfTag? Tag { get; init; }
    public double Duration => Rate > 0 ? SampleCount / (double)Rate : 0;
}

public static class SoundDecoder
{
    static readonly int[] Rates = [5512, 11025, 22050, 44100];
    static readonly string[] Codecs =
        ["PCM (native)", "ADPCM", "MP3", "PCM", "Nellymoser 16 kHz", "Nellymoser 8 kHz", "Nellymoser", "?", "?", "?", "?", "Speex", "?", "?", "?", "?"];

    public static SwfSound? DecodeDefineSound(SwfTag tag)
    {
        var r = new SwfReader(tag.Data);
        int id = r.U16();
        int format = (int)r.UB(4);
        int rateIdx = (int)r.UB(2);
        bool is16 = r.Bit();
        bool stereo = r.Bit();
        int samples = (int)r.U32();
        var data = r.Rest();
        int rate = format switch { 4 => 16000, 5 => 8000, 11 => 16000, _ => Rates[rateIdx] };
        var (bytes, ext) = Encode(format, data, rate, is16, stereo, samples, isStream: false);
        return new SwfSound
        {
            Id = id, Name = $"sound_{id}", Codec = Codecs[format], Rate = rate, Stereo = stereo, Is16Bit = is16,
            SampleCount = samples, FileBytes = bytes, Extension = ext, Tag = tag,
        };
    }

    /// <summary>Streaming sound (SoundStreamHead + SoundStreamBlock) of a timeline.</summary>
    public static SwfSound? DecodeStream(SwfTag head, IEnumerable<SwfTag> blocks, string name)
    {
        var r = new SwfReader(head.Data);
        r.U8(); // recommended playback format: ignored
        int format = (int)r.UB(4);
        int rateIdx = (int)r.UB(2);
        bool is16 = r.Bit();
        bool stereo = r.Bit();
        int rate = format switch { 4 => 16000, 5 => 8000, 11 => 16000, _ => Rates[rateIdx] };
        var all = new MemoryStream();
        int total = 0;
        var pcmParts = new List<short[]>();
        foreach (var b in blocks)
        {
            if (format == 2)
            {
                if (b.Data.Length < 4) continue;
                total += b.Data[0] | b.Data[1] << 8;
                all.Write(b.Data, 4, b.Data.Length - 4);
            }
            else if (format == 1)
            {
                var pcm = DecodeAdpcm(b.Data, stereo, int.MaxValue);
                pcmParts.Add(pcm);
                total += pcm.Length / (stereo ? 2 : 1);
            }
            else
            {
                all.Write(b.Data);
            }
        }
        byte[] bytes;
        string ext;
        if (format == 2) { bytes = all.ToArray(); ext = ".mp3"; }
        else if (format == 1)
        {
            var merged = pcmParts.SelectMany(p => p).ToArray();
            bytes = Wav(ToBytes(merged), rate, 16, stereo ? 2 : 1);
            ext = ".wav";
        }
        else
        {
            (bytes, ext) = Encode(format, all.ToArray(), rate, is16, stereo, 0, isStream: true);
            total = (int)(all.Length / ((is16 ? 2 : 1) * (stereo ? 2 : 1)));
        }
        if (bytes.Length == 0) return null;
        return new SwfSound
        {
            Id = -1, Name = name, Codec = Codecs[format], Rate = rate, Stereo = stereo, Is16Bit = is16,
            SampleCount = total, FileBytes = bytes, Extension = ext, Tag = head,
        };
    }

    static (byte[], string) Encode(int format, byte[] data, int rate, bool is16, bool stereo, int samples, bool isStream)
    {
        int channels = stereo ? 2 : 1;
        switch (format)
        {
            case 0:
            case 3:
                return (Wav(data, rate, is16 ? 16 : 8, channels), ".wav");
            case 1:
                return (Wav(ToBytes(DecodeAdpcm(data, stereo, samples > 0 ? samples : int.MaxValue)), rate, 16, channels), ".wav");
            case 2:
                // DefineSound MP3: SeekSamples (SI16) followed by the frames
                return (isStream || data.Length < 2 ? data : data[2..], ".mp3");
            case 4:
            case 5:
            case 6:
                return (data, ".nellymoser");
            case 11:
                return (data, ".speex");
            default:
                return (data, ".bin");
        }
    }

    static byte[] ToBytes(short[] s)
    {
        var b = new byte[s.Length * 2];
        Buffer.BlockCopy(s, 0, b, 0, b.Length);
        return b;
    }

    public static byte[] Wav(byte[] pcm, int rate, int bits, int channels)
    {
        var ms = new MemoryStream(44 + pcm.Length);
        var w = new BinaryWriter(ms);
        int blockAlign = channels * bits / 8;
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * blockAlign);
        w.Write((short)blockAlign);
        w.Write((short)bits);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        return ms.ToArray();
    }

    static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107,
        118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876,
        963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
        5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086,
        29794, 32767,
    ];

    static readonly int[][] IndexTables =
    [
        [-1, 2],
        [-1, -1, 2, 4],
        [-1, -1, -1, -1, 2, 4, 6, 8],
        [-1, -1, -1, -1, -1, -1, -1, -1, 1, 2, 4, 6, 8, 10, 13, 16],
    ];

    /// <summary>Flash ADPCM (IMA variant with 2–5 bits per sample, packets of 4096 samples).</summary>
    public static short[] DecodeAdpcm(byte[] data, bool stereo, int maxSamples)
    {
        var r = new SwfReader(data);
        var output = new List<short>();
        int channels = stereo ? 2 : 1;
        try
        {
            int bits = (int)r.UB(2) + 2;
            var table = IndexTables[bits - 2];
            int signMask = 1 << (bits - 1);
            int k0 = 1 << (bits - 2);
            var pred = new int[channels];
            var index = new int[channels];
            int produced = 0;
            while (produced < maxSamples)
            {
                // At least one complete packet header is required
                if (r.Remaining * 8 < 22 * channels) break;
                for (int c = 0; c < channels; c++)
                {
                    pred[c] = r.SB(16);
                    index[c] = (int)r.UB(6);
                    output.Add((short)pred[c]);
                }
                produced++;
                for (int n = 1; n < 4096 && produced < maxSamples; n++)
                {
                    if (r.Remaining * 8 < bits * channels && r.Remaining == 0) break;
                    bool stop = false;
                    for (int c = 0; c < channels; c++)
                    {
                        int code;
                        try { code = (int)r.UB(bits); }
                        catch (EndOfStreamException) { stop = true; break; }
                        int step = StepTable[index[c]];
                        int diff = 0;
                        int k = k0;
                        do
                        {
                            if ((code & k) != 0) diff += step;
                            step >>= 1;
                            k >>= 1;
                        } while (k != 0);
                        diff += step;
                        pred[c] += (code & signMask) != 0 ? -diff : diff;
                        pred[c] = Math.Clamp(pred[c], -32768, 32767);
                        index[c] = Math.Clamp(index[c] + table[code & ~signMask], 0, 88);
                        output.Add((short)pred[c]);
                    }
                    if (stop) break;
                    produced++;
                }
            }
        }
        catch (EndOfStreamException) { }
        return output.ToArray();
    }
}
