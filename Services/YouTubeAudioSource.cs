using NAudio.Wave;
using System;
using System.Diagnostics;
using System.Threading;

namespace BoomBx.Services
{
    /// <summary>
    /// Live-streams one YouTube video's audio.
    /// A background thread pulls + decodes the audio once and feeds two outputs
    /// (VB-Cable mic and your speakers), so both stay in sync and the audio
    /// threads never wait on the network. Nothing is written to disk.
    /// </summary>
    public sealed class YouTubeAudioSource : IDisposable
    {
        private const double BufferSeconds = 6;
        private const double PrerollSeconds = 0.35;

        private readonly YouTubeService _service;
        private readonly string _videoId;
        private readonly double _startSeconds;
        private readonly double _endSeconds;
        private readonly WaveFormat _format;
        private readonly CancellationTokenSource _cts = new();
        private readonly StreamingSampleProvider _virtual;
        private readonly StreamingSampleProvider _speaker;
        private Thread? _thread;

        private volatile bool _loop;
        private volatile bool _paused;
        private double _decodedSeconds;

        public ISampleProvider VirtualOutput => _virtual;
        public ISampleProvider SpeakerOutput => _speaker;

        public bool Loop { get => _loop; set => _loop = value; }
        public bool Paused
        {
            get => _paused;
            set
            {
                _paused = value;
                if (!value) _speaker.MarkAlive(); // just resumed - don't treat the pause gap as a dead output
            }
        }

        /// <summary>Video length in seconds once known (0 before).</summary>
        public double TotalSeconds { get; private set; }

        /// <summary>Where playback roughly is right now, in video time.</summary>
        public double ElapsedSeconds
        {
            get
            {
                var buffered = _speaker.BufferedSamples / (double)(_format.SampleRate * _format.Channels);
                return Math.Max(_startSeconds, Volatile.Read(ref _decodedSeconds) - buffered);
            }
        }

        public bool IsBuffering => !_speaker.HasStarted;

        /// <summary>0..1 while the audio is being pulled into memory.</summary>
        public double LoadProgress { get; private set; }

        /// <summary>Milliseconds since the speaker output last asked for audio.</summary>
        public long MsSinceSpeakerRead => Environment.TickCount64 - _speaker.LastReadTick;

        /// <summary>Raised on the decode thread when something breaks.</summary>
        public event Action<string>? Failed;

        public YouTubeAudioSource(YouTubeService service, string videoId, double startSeconds, double endSeconds,
                                  WaveFormat format, bool loop)
        {
            _service = service;
            _videoId = videoId;
            _startSeconds = Math.Max(0, startSeconds);
            _endSeconds = endSeconds > _startSeconds ? endSeconds : 0;
            _format = format;
            _loop = loop;
            _decodedSeconds = _startSeconds;

            int capacity = (int)(format.SampleRate * format.Channels * BufferSeconds);
            int preroll = (int)(format.SampleRate * format.Channels * PrerollSeconds);
            _virtual = new StreamingSampleProvider(format, capacity, preroll);
            _speaker = new StreamingSampleProvider(format, capacity, preroll);
        }

        public void Start()
        {
            _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "YouTube decode" };
            try { _thread.SetApartmentState(ApartmentState.MTA); } // Media Foundation likes MTA
            catch (PlatformNotSupportedException) { }
            _thread.Start();
        }

        private void DecodeLoop()
        {
            var ct = _cts.Token;
            try
            {
                using var reader = _service.OpenAudio(_videoId, p => LoadProgress = p, ct);
                LoadProgress = 1;
                TotalSeconds = reader.TotalTime.TotalSeconds;
                Logger.Log($"[YouTube] {_videoId}: decoding {reader.WaveFormat} length {reader.TotalTime}");

                if (_startSeconds > 0 && (TotalSeconds <= 0 || _startSeconds < TotalSeconds))
                    reader.CurrentTime = TimeSpan.FromSeconds(_startSeconds);

                var samples = AudioService.ConvertFormat(reader.ToSampleProvider(), _format);
                int samplesPerSecond = _format.SampleRate * _format.Channels;
                int chunk = samplesPerSecond / 20; // 50 ms
                chunk -= chunk % _format.Channels;
                var buffer = new float[chunk];

                double position = _startSeconds;
                long writtenSinceRestart = 0;

                while (!ct.IsCancellationRequested)
                {
                    WaitForRoom(chunk, ct);
                    if (ct.IsCancellationRequested) break;

                    int read = samples.Read(buffer, 0, chunk);
                    bool reachedEnd = read == 0;

                    if (_endSeconds > 0 && read > 0)
                    {
                        double left = _endSeconds - position;
                        int allowed = (int)(Math.Max(0, left) * samplesPerSecond);
                        allowed -= allowed % _format.Channels;
                        if (allowed <= read)
                        {
                            read = allowed;
                            reachedEnd = true;
                        }
                    }

                    if (read > 0)
                    {
                        _virtual.Write(buffer, read);
                        _speaker.Write(buffer, read);
                        position += read / (double)samplesPerSecond;
                        writtenSinceRestart += read;
                        Volatile.Write(ref _decodedSeconds, position);
                    }

                    if (!reachedEnd) continue;

                    // Loop only if the last pass actually produced audio (avoids a spin on broken streams).
                    if (_loop && writtenSinceRestart > 0)
                    {
                        reader.CurrentTime = TimeSpan.FromSeconds(_startSeconds);
                        position = _startSeconds;
                        writtenSinceRestart = 0;
                        continue;
                    }
                    break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Log($"[YouTube] {_videoId}: {ex}");
                if (!ct.IsCancellationRequested)
                    Failed?.Invoke(ex.Message);
            }
            finally
            {
                _virtual.Complete();
                _speaker.Complete();
            }
        }

        /// <summary>Waits until both outputs have room. Never blocks forever unless paused.</summary>
        private void WaitForRoom(int needed, CancellationToken ct)
        {
            var stuck = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested && (_virtual.FreeSamples < needed || _speaker.FreeSamples < needed))
            {
                if (_paused)
                {
                    stuck.Restart();
                }
                else if (stuck.Elapsed > TimeSpan.FromSeconds(3))
                {
                    // One output stopped reading (device unplugged etc.) - drop old audio so the other keeps going.
                    _virtual.Discard(needed);
                    _speaker.Discard(needed);
                    return;
                }
                Thread.Sleep(10);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _virtual.Complete();
            _speaker.Complete();
            // Thread is background + checks the token; it closes the network stream itself.
        }
    }

    /// <summary>
    /// Thread-safe ring buffer that plays silence while buffering
    /// and returns 0 only when the stream is really finished.
    /// </summary>
    public sealed class StreamingSampleProvider : ISampleProvider
    {
        private readonly float[] _buffer;
        private readonly int _preroll;
        private readonly object _lock = new();
        private int _readPos;
        private int _writePos;
        private int _count;
        private bool _completed;
        private bool _started;

        public StreamingSampleProvider(WaveFormat format, int capacity, int preroll)
        {
            WaveFormat = format;
            _buffer = new float[capacity];
            _preroll = Math.Min(preroll, capacity / 2);
        }

        public WaveFormat WaveFormat { get; }

        public int BufferedSamples { get { lock (_lock) return _count; } }
        public int FreeSamples { get { lock (_lock) return _buffer.Length - _count; } }
        public bool HasStarted { get { lock (_lock) return _started; } }
        public long LastReadTick => Interlocked.Read(ref _lastReadTick);
        public void MarkAlive() => Interlocked.Exchange(ref _lastReadTick, Environment.TickCount64);
        private long _lastReadTick = Environment.TickCount64;

        public void Write(float[] source, int count)
        {
            lock (_lock)
            {
                if (_completed) return;

                int overflow = _count + count - _buffer.Length;
                if (overflow > 0) DiscardUnsafe(overflow);

                int offset = 0;
                while (count > 0)
                {
                    int part = Math.Min(count, _buffer.Length - _writePos);
                    Array.Copy(source, offset, _buffer, _writePos, part);
                    _writePos = (_writePos + part) % _buffer.Length;
                    _count += part;
                    offset += part;
                    count -= part;
                }
            }
        }

        public void Discard(int count)
        {
            lock (_lock) DiscardUnsafe(count);
        }

        private void DiscardUnsafe(int count)
        {
            count = Math.Min(count, _count);
            _readPos = (_readPos + count) % _buffer.Length;
            _count -= count;
        }

        public void Complete()
        {
            lock (_lock) _completed = true;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            Interlocked.Exchange(ref _lastReadTick, Environment.TickCount64);
            lock (_lock)
            {
                if (!_started)
                {
                    if (_count >= _preroll || _completed)
                    {
                        _started = true;
                    }
                    else
                    {
                        Array.Clear(buffer, offset, count);
                        return count; // still buffering - play silence
                    }
                }

                int toRead = Math.Min(count, _count);
                int done = 0;
                while (done < toRead)
                {
                    int part = Math.Min(toRead - done, _buffer.Length - _readPos);
                    Array.Copy(_buffer, _readPos, buffer, offset + done, part);
                    _readPos = (_readPos + part) % _buffer.Length;
                    done += part;
                }
                _count -= toRead;

                if (toRead < count)
                {
                    if (_completed) return toRead; // finished (0 = stop)
                    Array.Clear(buffer, offset + toRead, count - toRead); // network hiccup - pad with silence
                }
                return count;
            }
        }
    }
}
