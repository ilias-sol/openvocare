using System.Buffers.Binary;

namespace OpenVocare.Services;

internal static class WavAudioInspector
{
    private const int RiffHeaderLength = 12;
    private const short PcmFormat = 1;
    private const short IeeeFloatFormat = 3;

    public static bool HasAudibleSignal(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < RiffHeaderLength
            || !wav[..4].SequenceEqual("RIFF"u8)
            || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return true;
        }

        short format = 0;
        short bitsPerSample = 0;
        ReadOnlySpan<byte> samples = default;
        int offset = RiffHeaderLength;
        while (offset <= wav.Length - 8)
        {
            ReadOnlySpan<byte> id = wav.Slice(offset, 4);
            int length = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(offset + 4, 4));
            if (length < 0 || offset + 8L + length > wav.Length)
            {
                return true;
            }

            ReadOnlySpan<byte> chunk = wav.Slice(offset + 8, length);
            if (id.SequenceEqual("fmt "u8) && chunk.Length >= 16)
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(chunk);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(chunk.Slice(14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                samples = chunk;
            }

            offset += 8 + length + (length & 1);
        }

        if (samples.IsEmpty)
        {
            return false;
        }

        return (format, bitsPerSample) switch
        {
            (PcmFormat, 16) => HasPcm16Signal(samples),
            (IeeeFloatFormat, 32) => HasFloat32Signal(samples),
            _ => true
        };
    }

    private static bool HasPcm16Signal(ReadOnlySpan<byte> samples)
    {
        // A deliberately conservative floor: reject digital silence and capture
        // glitches without discarding a genuinely quiet speaker.
        for (int index = 0; index <= samples.Length - 2; index += 2)
        {
            int amplitude = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(
                samples.Slice(index, 2)));
            if (amplitude >= 32)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasFloat32Signal(ReadOnlySpan<byte> samples)
    {
        for (int index = 0; index <= samples.Length - 4; index += 4)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(samples.Slice(index, 4));
            float value = BitConverter.Int32BitsToSingle(bits);
            if (float.IsFinite(value) && Math.Abs(value) >= 0.001f)
            {
                return true;
            }
        }
        return false;
    }
}
