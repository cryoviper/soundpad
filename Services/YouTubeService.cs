using BoomBx.Models;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos;

namespace BoomBx.Services
{
    /// <summary>
    /// Searches YouTube and opens audio streams without saving anything to disk.
    /// Main engine: YoutubeExplode. Backup engine: yt-dlp (fetched once on demand),
    /// used only when YouTube changes something and YoutubeExplode breaks.
    /// </summary>
    public sealed class YouTubeService
    {
        private const int MaxResults = 25;
        private const string YtDlpDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

        private static readonly HttpClient Http = CreateHttpClient();
        private readonly YoutubeClient _client = new();
        private readonly SemaphoreSlim _ytDlpLock = new(1, 1);

        public event Action<string>? Log;

        // The reader is created and read on the same decode thread, so one reader object is safe + faster.
        private static MediaFoundationReader.MediaFoundationReaderSettings ReaderSettings =>
            new() { SingleReaderObject = true };

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) BoobiesSoundpad");
            return http;
        }

        // ------------------------------------------------------------------ search

        public async Task<List<YouTubeResult>> SearchAsync(string query, CancellationToken ct)
        {
            query = query.Trim();
            if (query.Length == 0) return new List<YouTubeResult>();

            try
            {
                return await SearchWithYoutubeExplodeAsync(query, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log?.Invoke($"Main search failed ({ex.Message}), trying backup...");
                return await SearchWithYtDlpAsync(query, ct);
            }
        }

        private async Task<List<YouTubeResult>> SearchWithYoutubeExplodeAsync(string query, CancellationToken ct)
        {
            // Pasted a link or a video id? Show just that video.
            if (query.Contains("youtu", StringComparison.OrdinalIgnoreCase) && VideoId.TryParse(query) is VideoId directId)
            {
                var video = await _client.Videos.GetAsync(directId, ct);
                return new List<YouTubeResult>
                {
                    new()
                    {
                        Id = video.Id.Value,
                        Title = video.Title,
                        Channel = video.Author.ChannelTitle,
                        DurationSeconds = video.Duration?.TotalSeconds ?? 0
                    }
                };
            }

            var results = new List<YouTubeResult>();
            await foreach (var item in _client.Search.GetVideosAsync(query, ct))
            {
                // Skip live streams (no duration) - they can't be played as a clip.
                if (item.Duration is null) continue;

                results.Add(new YouTubeResult
                {
                    Id = item.Id.Value,
                    Title = item.Title,
                    Channel = item.Author.ChannelTitle,
                    DurationSeconds = item.Duration.Value.TotalSeconds
                });

                if (results.Count >= MaxResults) break;
            }
            return results;
        }

        private async Task<List<YouTubeResult>> SearchWithYtDlpAsync(string query, CancellationToken ct)
        {
            var exe = await EnsureYtDlpAsync(ct);
            var lines = await RunYtDlpAsync(exe, ct,
                $"ytsearch{MaxResults}:{query}", "--flat-playlist", "--dump-json", "--no-warnings", "--skip-download");

            var results = new List<YouTubeResult>();
            foreach (var line in lines)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;

                    double duration = 0;
                    if (root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
                        duration = durEl.GetDouble();
                    if (duration <= 0) continue; // live / unknown

                    string channel = "";
                    if (root.TryGetProperty("channel", out var chEl) && chEl.ValueKind == JsonValueKind.String)
                        channel = chEl.GetString() ?? "";
                    else if (root.TryGetProperty("uploader", out var upEl) && upEl.ValueKind == JsonValueKind.String)
                        channel = upEl.GetString() ?? "";

                    results.Add(new YouTubeResult
                    {
                        Id = id,
                        Title = root.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? id : id,
                        Channel = channel,
                        DurationSeconds = duration
                    });
                }
                catch (JsonException) { /* skip bad line */ }
            }
            return results;
        }

        // ------------------------------------------------------------------ audio

        // Recently played clips stay in RAM so replays / pinned sounds start instantly.
        private const long CacheLimitBytes = 150L * 1024 * 1024;
        private readonly object _cacheLock = new();
        private readonly LinkedList<(string Id, byte[] Data)> _cache = new();
        private long _cacheBytes;

        /// <summary>
        /// Pulls the audio of a video into memory (never to disk) and opens a decoder on it.
        /// Blocking - call from a background thread.
        /// </summary>
        public WaveStream OpenAudio(string videoId, Action<double>? progress, CancellationToken ct)
            => OpenDecoder(GetAudioBytes(videoId, progress, ct));

        /// <summary>
        /// The raw audio file of a video (usually m4a), from RAM cache or downloaded into memory.
        /// Tries the main engine first, then yt-dlp. Only returns data Windows can decode.
        /// Blocking - call from a background thread.
        /// </summary>
        public byte[] GetAudioBytes(string videoId, Action<double>? progress, CancellationToken ct)
        {
            var cached = GetCached(videoId);
            if (cached != null)
            {
                progress?.Invoke(1);
                return cached;
            }

            var errors = new List<string>();

            // 1) YoutubeExplode
            try
            {
                var bytes = DownloadWithYoutubeExplode(videoId, progress, ct);
                if (CanDecode(bytes, errors, "main"))
                {
                    AddToCache(videoId, bytes);
                    return bytes;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                errors.Add($"main: {ex.Message}");
                Logger.Log($"[YouTube] main engine failed for {videoId}: {ex}");
                Log?.Invoke("Main engine failed, trying backup...");
            }

            // 2) yt-dlp backup (sends the file to us through stdout, still nothing saved)
            try
            {
                var bytes = DownloadWithYtDlp(videoId, progress, ct);
                if (CanDecode(bytes, errors, "backup"))
                {
                    AddToCache(videoId, bytes);
                    return bytes;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                errors.Add($"backup: {ex.Message}");
                Logger.Log($"[YouTube] backup engine failed for {videoId}: {ex}");
            }

            throw new InvalidOperationException("Couldn't play this video. " + string.Join(" | ", errors));
        }

        private byte[] DownloadWithYoutubeExplode(string videoId, Action<double>? progress, CancellationToken ct)
        {
            using var watchdog = new StallWatchdog(ct, TimeSpan.FromSeconds(20));

            var manifest = _client.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), watchdog.Token)
                .AsTask().GetAwaiter().GetResult();

            // m4a (AAC) first - Windows decodes it natively.
            var audio = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Container.Name.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(s => s.Bitrate.BitsPerSecond)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No audio stream found for this video");

            long size = audio.Size.Bytes;
            using var memory = new MemoryStream(size > 0 && size < int.MaxValue ? (int)size : 0);
            var reporter = new SyncProgress(p =>
            {
                watchdog.Alive();
                progress?.Invoke(p);
            });

            _client.Videos.Streams.CopyToAsync(audio, memory, reporter, watchdog.Token)
                .AsTask().GetAwaiter().GetResult();

            Logger.Log($"[YouTube] {videoId}: got {memory.Length / 1024} KB ({audio.Container.Name}, {audio.Bitrate})");
            if (memory.Length == 0) throw new InvalidOperationException("Downloaded 0 bytes");
            return memory.ToArray();
        }

        private byte[] DownloadWithYtDlp(string videoId, Action<double>? progress, CancellationToken ct)
        {
            var exe = EnsureYtDlpAsync(ct).GetAwaiter().GetResult();
            Log?.Invoke("Loading with backup engine...");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in new[]
                     {
                         "-f", "bestaudio[ext=m4a]/bestaudio", "--no-playlist", "--no-warnings",
                         "--no-part", "--quiet", "-o", "-", $"https://www.youtube.com/watch?v={videoId}"
                     })
                psi.ArgumentList.Add(a);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start yt-dlp");
            using var watchdog = new StallWatchdog(ct, TimeSpan.FromSeconds(25));
            using var reg = watchdog.Token.Register(() => { try { process.Kill(true); } catch { } });

            var stderrTask = process.StandardError.ReadToEndAsync();
            using var memory = new MemoryStream();
            var buffer = new byte[64 * 1024];
            var output = process.StandardOutput.BaseStream;
            int read;
            while ((read = output.Read(buffer, 0, buffer.Length)) > 0)
            {
                memory.Write(buffer, 0, read);
                watchdog.Alive();
            }
            process.WaitForExit();
            watchdog.Token.ThrowIfCancellationRequested();

            if (process.ExitCode != 0 || memory.Length == 0)
            {
                var err = stderrTask.GetAwaiter().GetResult().Trim();
                throw new InvalidOperationException(string.IsNullOrEmpty(err) ? $"yt-dlp exit code {process.ExitCode}" : err);
            }

            progress?.Invoke(1);
            Logger.Log($"[YouTube] {videoId}: backup got {memory.Length / 1024} KB");
            return memory.ToArray();
        }

        private static bool CanDecode(byte[] bytes, List<string> errors, string engine)
        {
            try
            {
                using var reader = OpenDecoder(bytes);
                return true;
            }
            catch (Exception ex)
            {
                errors.Add($"{engine} decode: {ex.Message}");
                Logger.Log($"[YouTube] decode failed ({engine}): {ex}");
                return false;
            }
        }

        /// <summary>Windows Media Foundation decoder over in-memory audio.</summary>
        public static WaveStream OpenDecoder(byte[] bytes)
        {
            var reader = new StreamMediaFoundationReader(new MemoryStream(bytes, writable: false), ReaderSettings);
            if (reader.WaveFormat == null || reader.WaveFormat.SampleRate <= 0)
            {
                reader.Dispose();
                throw new InvalidOperationException("Audio format not supported");
            }
            return reader;
        }

        private byte[]? GetCached(string id)
        {
            lock (_cacheLock)
            {
                var node = _cache.First;
                while (node != null)
                {
                    if (node.Value.Id == id)
                    {
                        _cache.Remove(node);
                        _cache.AddFirst(node);
                        return node.Value.Data;
                    }
                    node = node.Next;
                }
                return null;
            }
        }

        private void AddToCache(string id, byte[] data)
        {
            if (data.LongLength > CacheLimitBytes / 2) return;
            lock (_cacheLock)
            {
                _cache.AddFirst((id, data));
                _cacheBytes += data.LongLength;
                while (_cacheBytes > CacheLimitBytes && _cache.Last != null)
                {
                    _cacheBytes -= _cache.Last.Value.Data.LongLength;
                    _cache.RemoveLast();
                }
            }
        }

        /// <summary>Cancels when no progress is reported for a while (stuck network).</summary>
        private sealed class StallWatchdog : IDisposable
        {
            private readonly CancellationTokenSource _cts;
            private readonly Timer _timer;
            private readonly TimeSpan _limit;
            private long _lastAlive = Environment.TickCount64;

            public StallWatchdog(CancellationToken outer, TimeSpan limit)
            {
                _limit = limit;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
                _timer = new Timer(_ =>
                {
                    if (Environment.TickCount64 - Interlocked.Read(ref _lastAlive) > _limit.TotalMilliseconds)
                    {
                        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
                    }
                }, null, 1000, 1000);
            }

            public CancellationToken Token => _cts.Token;
            public void Alive() => Interlocked.Exchange(ref _lastAlive, Environment.TickCount64);

            public void Dispose()
            {
                _timer.Dispose();
                _cts.Dispose();
            }
        }

        /// <summary>IProgress that reports right away on the calling thread.</summary>
        private sealed class SyncProgress : IProgress<double>
        {
            private readonly Action<double> _report;
            public SyncProgress(Action<double> report) => _report = report;
            public void Report(double value) => _report(value);
        }

        public static async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken ct = default)
        {
            try
            {
                return await Http.GetByteArrayAsync(url, ct);
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ yt-dlp backup

        private async Task<string> EnsureYtDlpAsync(CancellationToken ct)
        {
            // Next to the app (user can drop a newer one there) or in our tools folder.
            var besideApp = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
            if (File.Exists(besideApp)) return besideApp;

            var target = Path.Combine(AppPaths.ToolsDir, "yt-dlp.exe");

            await _ytDlpLock.WaitAsync(ct);
            try
            {
                var fresh = File.Exists(target) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(target)).TotalDays < 7;
                if (fresh) return target;

                Log?.Invoke("Getting backup player (yt-dlp), one-time ~15 MB...");
                try
                {
                    var bytes = await Http.GetByteArrayAsync(YtDlpDownloadUrl, ct);
                    var temp = target + ".tmp";
                    await File.WriteAllBytesAsync(temp, bytes, ct);
                    File.Move(temp, target, overwrite: true);
                }
                catch (Exception) when (File.Exists(target))
                {
                    // Update failed but an older copy exists - use it.
                }
                return target;
            }
            finally
            {
                _ytDlpLock.Release();
            }
        }

        private static async Task<List<string>> RunYtDlpAsync(string exe, CancellationToken ct, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start yt-dlp");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { /* already gone */ }
                throw;
            }

            var stdout = await stdoutTask;
            if (process.ExitCode != 0)
            {
                var err = (await stderrTask).Trim();
                throw new InvalidOperationException(string.IsNullOrEmpty(err) ? $"yt-dlp exit code {process.ExitCode}" : err);
            }

            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
    }
}
