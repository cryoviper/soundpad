using System;
using NAudio.Wave;

/// <summary>
/// Always fills the whole buffer unless the sound really ended.
/// Resamplers sometimes return a few samples less than asked; NAudio's mixer treats that as
/// "sound finished" and drops it - that's what cut sounds off in Discord / games.
/// </summary>
public sealed class FullReadSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public FullReadSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int total = 0;
        int emptyReads = 0;
        while (total < count)
        {
            int read = _source.Read(buffer, offset + total, count - total);
            if (read <= 0)
            {
                // two empty reads in a row = really finished
                if (++emptyReads >= 2) break;
                continue;
            }
            emptyReads = 0;
            total += read;
        }
        return total;
    }
}

/// <summary>
/// Gentle limiter on the final mix: when mic + sound together go over full scale,
/// it rounds the peaks off instead of harsh digital clipping (crackle).
/// </summary>
public sealed class SoftLimiterSampleProvider : ISampleProvider
{
    private const float Knee = 0.85f;
    private readonly ISampleProvider _source;

    public SoftLimiterSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        for (int i = 0; i < read; i++)
        {
            float x = buffer[offset + i];
            float a = MathF.Abs(x);
            if (a > Knee)
            {
                // smooth curve from the knee up to 1.0
                float over = (a - Knee) / (1f - Knee);
                float shaped = Knee + (1f - Knee) * MathF.Tanh(over);
                buffer[offset + i] = MathF.Sign(x) * shaped;
            }
        }
        return read;
    }
}
