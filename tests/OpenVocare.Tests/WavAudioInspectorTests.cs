using System.Buffers.Binary;
using OpenVocare.Services;

namespace OpenVocare.Tests;

public sealed class WavAudioInspectorTests
{
    [Fact]
    public void DigitalSilence_IsRejected()
    {
        byte[] wav = CreatePcm16Wav([0, 0, 0, 0]);

        Assert.False(WavAudioInspector.HasAudibleSignal(wav));
    }

    [Fact]
    public void QuietSpeechLikeSignal_IsAccepted()
    {
        byte[] wav = CreatePcm16Wav([0, 18, -34, 50]);

        Assert.True(WavAudioInspector.HasAudibleSignal(wav));
    }

    [Fact]
    public void UnknownAudioShape_IsNotRejectedLocally()
    {
        Assert.True(WavAudioInspector.HasAudibleSignal("not-a-wav"u8));
    }

    private static byte[] CreatePcm16Wav(short[] samples)
    {
        byte[] wav = new byte[44 + samples.Length * 2];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), 16_000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), 32_000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), samples.Length * 2);
        for (int index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                wav.AsSpan(44 + index * 2, 2),
                samples[index]);
        }
        return wav;
    }
}
