using System;
using NAudio.Wave;

/// <summary>
/// Turns 3+ channel audio (laptop mic arrays often report 4 channels) into clean stereo.
/// Old version read more samples than the caller's buffer could hold and summed extra
/// channels on top, which made mics sound noisy and too loud in games.
/// </summary>
public class DownmixToStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = Array.Empty<float>();

    public DownmixToStereoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        if (_sourceChannels < 2)
            throw new ArgumentException("Source must have at least 2 channels for downmixing");
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        int needed = frames * _sourceChannels;
        if (_sourceBuffer.Length < needed) _sourceBuffer = new float[needed];

        int read = _source.Read(_sourceBuffer, 0, needed);
        int readFrames = read / _sourceChannels;

        for (int i = 0; i < readFrames; i++)
        {
            float sum = 0;
            int baseIndex = i * _sourceChannels;
            for (int ch = 0; ch < _sourceChannels; ch++) sum += _sourceBuffer[baseIndex + ch];
            float mixed = sum / _sourceChannels; // average, never louder than the input
            buffer[offset + i * 2] = mixed;
            buffer[offset + i * 2 + 1] = mixed;
        }
        return readFrames * 2;
    }
}
