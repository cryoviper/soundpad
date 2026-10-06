using System;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

/// <summary>One voice effect recipe. All "0" values mean "off".</summary>
public sealed record VoicePreset(
    string Id,
    string Name,
    string Description,
    float Pitch = 1f,          // 0.5 = an octave down, 2 = an octave up
    float RingHz = 0f,         // robot / alien buzz
    float RingMix = 0f,        // 0..1
    float BandLowHz = 0f,      // radio / phone filter
    float BandHighHz = 0f,
    float Drive = 0f,          // distortion amount (0 = clean)
    float EchoMs = 0f,         // echo delay
    float EchoFeedback = 0f,   // 0..0.9
    float EchoMix = 0f,        // 0..1
    float Gain = 1f)
{
    public static readonly VoicePreset[] All =
    {
        new("normal",    "Normal",     "Your real voice"),
        new("deep",      "Deep",       "Low, heavy voice",               Pitch: 0.78f),
        new("monster",   "Monster",    "Huge, growly beast",             Pitch: 0.62f, Drive: 1.6f, Gain: 0.9f),
        new("chipmunk",  "Chipmunk",   "Tiny, squeaky voice",            Pitch: 1.6f),
        new("kid",       "Kid",        "Young, higher voice",            Pitch: 1.28f),
        new("robot",     "Robot",      "Metal, buzzy machine",           RingHz: 55f, RingMix: 1f, EchoMs: 18f, EchoFeedback: 0.35f, EchoMix: 0.35f),
        new("alien",     "Alien",      "Wobbly voice from space",        Pitch: 1.18f, RingHz: 420f, RingMix: 0.55f),
        new("radio",     "Radio",      "Old walkie-talkie",              BandLowHz: 450f, BandHighHz: 2800f, Drive: 3f, Gain: 0.9f),
        new("megaphone", "Megaphone",  "Loud announcer / police",        BandLowHz: 650f, BandHighHz: 3600f, Drive: 6f, Gain: 0.8f),
        new("cave",      "Cave",       "Big echo",                       EchoMs: 230f, EchoFeedback: 0.45f, EchoMix: 0.5f),
        new("stadium",   "Stadium",    "Announcer in a huge arena",      EchoMs: 110f, EchoFeedback: 0.6f, EchoMix: 0.35f, Pitch: 0.95f),
        new("demon",     "Demon",      "Deep + echo, scary",             Pitch: 0.55f, EchoMs: 160f, EchoFeedback: 0.4f, EchoMix: 0.35f, Drive: 1.2f),
    };
}

/// <summary>
/// Real-time voice changer for the mic (pitch, robot, radio, echo...).
/// Settings can be changed from the UI thread while audio runs.
/// </summary>
public sealed class VoiceChangerSampleProvider : ISampleProvider
{
    private readonly SmbPitchShiftingSampleProvider _pitch;
    private readonly int _channels;
    private readonly int _sampleRate;

    private volatile State _state;
    private double _ringPhase;

    private sealed class State
    {
        public required VoicePreset Preset;
        public required float ExtraPitch;   // user's fine-tune multiplier
        public required bool Enabled;
        public BiQuadFilter[]? LowCut;
        public BiQuadFilter[]? HighCut;
        public float[][]? Echo;
        public int EchoPos;
        public int EchoLength;
    }

    public VoiceChangerSampleProvider(ISampleProvider source)
    {
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        // 2048 FFT keeps the delay small (~40 ms) - fine for talking.
        _pitch = new SmbPitchShiftingSampleProvider(source, 2048, 4, 1f);
        _state = Build(VoicePreset.All[0], 1f, false);
    }

    public WaveFormat WaveFormat => _pitch.WaveFormat;

    public VoicePreset Preset => _state.Preset;
    public bool Enabled => _state.Enabled;

    /// <summary>Switch effect. extraPitch fine-tunes on top of the preset (1 = as is).</summary>
    public void Configure(VoicePreset preset, float extraPitch, bool enabled)
    {
        _state = Build(preset, extraPitch, enabled);
    }

    private State Build(VoicePreset p, float extraPitch, bool enabled)
    {
        var s = new State { Preset = p, ExtraPitch = extraPitch, Enabled = enabled };
        if (p.BandLowHz > 0)
        {
            s.LowCut = new BiQuadFilter[_channels];
            for (int c = 0; c < _channels; c++) s.LowCut[c] = BiQuadFilter.HighPassFilter(_sampleRate, p.BandLowHz, 0.9f);
        }
        if (p.BandHighHz > 0)
        {
            s.HighCut = new BiQuadFilter[_channels];
            for (int c = 0; c < _channels; c++) s.HighCut[c] = BiQuadFilter.LowPassFilter(_sampleRate, p.BandHighHz, 0.9f);
        }
        if (p.EchoMs > 0)
        {
            s.EchoLength = Math.Max(1, (int)(_sampleRate * p.EchoMs / 1000f));
            s.Echo = new float[_channels][];
            for (int c = 0; c < _channels; c++) s.Echo[c] = new float[s.EchoLength];
        }
        return s;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var s = _state;
        float pitch = s.Enabled ? Math.Clamp(s.Preset.Pitch * s.ExtraPitch, 0.4f, 2.5f) : 1f;
        _pitch.PitchFactor = pitch;

        int read = _pitch.Read(buffer, offset, count);
        if (!s.Enabled) return read;

        var p = s.Preset;
        double ringStep = p.RingHz > 0 ? 2 * Math.PI * p.RingHz / _sampleRate : 0;
        float driveNorm = p.Drive > 0 ? MathF.Tanh(p.Drive) : 1f;

        for (int i = 0; i + _channels <= read; i += _channels)
        {
            float ring = 1f;
            if (ringStep > 0)
            {
                ring = (1f - p.RingMix) + p.RingMix * (float)Math.Sin(_ringPhase);
                _ringPhase += ringStep;
                if (_ringPhase > 2 * Math.PI) _ringPhase -= 2 * Math.PI;
            }

            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + i + c;
                float x = buffer[idx];

                if (s.LowCut != null) x = s.LowCut[c].Transform(x);
                if (s.HighCut != null) x = s.HighCut[c].Transform(x);
                if (p.Drive > 0) x = MathF.Tanh(x * p.Drive) / driveNorm;
                x *= ring;

                if (s.Echo != null)
                {
                    var line = s.Echo[c];
                    float delayed = line[s.EchoPos];
                    line[s.EchoPos] = x + delayed * p.EchoFeedback;
                    x += delayed * p.EchoMix;
                }

                buffer[idx] = Math.Clamp(x * p.Gain, -1f, 1f);
            }

            if (s.Echo != null && ++s.EchoPos >= s.EchoLength) s.EchoPos = 0;
        }
        return read;
    }
}

/// <summary>
/// Passes audio through and copies it to a side buffer, so you can hear your own changed voice.
/// </summary>
public sealed class MonitorTapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private byte[] _bytes = Array.Empty<byte>();

    public MonitorTapSampleProvider(ISampleProvider source)
    {
        _source = source;
        Monitor = new BufferedWaveProvider(source.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Plays to your headphones when Enabled.</summary>
    public BufferedWaveProvider Monitor { get; }

    public bool Enabled { get; set; }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (Enabled && read > 0)
        {
            int byteCount = read * 4;
            if (_bytes.Length < byteCount) _bytes = new byte[byteCount];
            // Copy sample by sample: NAudio may hand us a byte[] dressed as float[], so BlockCopy is unsafe.
            for (int i = 0; i < read; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(i * 4, 4), buffer[offset + i]);
            Monitor.AddSamples(_bytes, 0, byteCount);

            // keep the delay short
            if (Monitor.BufferedDuration > TimeSpan.FromMilliseconds(120)) Monitor.ClearBuffer();
        }
        return read;
    }
}
