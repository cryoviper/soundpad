using System;
using NAudio.Dsp;
using NAudio.Wave;

/// <summary>
/// Cleans the real mic before it goes into the virtual mic:
/// - high-pass filter removes rumble/hum (fan, desk bumps, 50 Hz hum)
/// - noise gate makes it fully silent when you're not talking (no hiss for your team)
/// Also measures the input level for the meter in Settings.
/// </summary>
public sealed class MicCleanupSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BiQuadFilter[] _highPass;
    private readonly int _channels;
    private readonly float _attackCoeff;
    private readonly float _releaseCoeff;
    private readonly int _holdSamples;

    private float _gain;           // current gate gain 0..1
    private int _holdCounter;
    private float _peak;           // for the meter

    public MicCleanupSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        int rate = source.WaveFormat.SampleRate;

        _highPass = new BiQuadFilter[_channels];
        for (int c = 0; c < _channels; c++)
            _highPass[c] = BiQuadFilter.HighPassFilter(rate, 85f, 0.707f);

        _attackCoeff = 1f - MathF.Exp(-1f / (rate * 0.003f));  // opens in ~3 ms
        _releaseCoeff = 1f - MathF.Exp(-1f / (rate * 0.12f));  // closes in ~120 ms
        _holdSamples = (int)(rate * 0.25f);                     // stays open 250 ms after you stop
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Gate on/off.</summary>
    public bool GateEnabled { get; set; } = true;

    /// <summary>Below this level (dBFS) the mic is muted. -100..0, typical -45.</summary>
    public float ThresholdDb { get; set; } = -45f;

    /// <summary>Loudest input level since the last call, in dBFS (for the meter).</summary>
    public float TakePeakDb()
    {
        float p = _peak;
        _peak = 0;
        return p <= 0.000001f ? -100f : 20f * MathF.Log10(p);
    }

    /// <summary>True while the gate lets your voice through.</summary>
    public bool IsOpen => _gain > 0.5f;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        float threshold = MathF.Pow(10f, ThresholdDb / 20f);
        bool gate = GateEnabled;

        for (int i = 0; i < read; i += _channels)
        {
            // filter + frame level
            float frameLevel = 0;
            for (int c = 0; c < _channels && i + c < read; c++)
            {
                int idx = offset + i + c;
                float s = _highPass[c].Transform(buffer[idx]);
                buffer[idx] = s;
                float a = MathF.Abs(s);
                if (a > frameLevel) frameLevel = a;
            }
            if (frameLevel > _peak) _peak = frameLevel;

            float target;
            if (!gate)
            {
                target = 1f;
            }
            else if (frameLevel >= threshold)
            {
                target = 1f;
                _holdCounter = _holdSamples;
            }
            else if (_holdCounter > 0)
            {
                target = 1f;
                _holdCounter--;
            }
            else
            {
                target = 0f;
            }

            _gain += (target - _gain) * (target > _gain ? _attackCoeff : _releaseCoeff);

            for (int c = 0; c < _channels && i + c < read; c++)
                buffer[offset + i + c] *= _gain;
        }
        return read;
    }
}
