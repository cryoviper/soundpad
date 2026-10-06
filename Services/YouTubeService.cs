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

        /// <summary>
        /// Opens a decodable audio stream for a video. Blocking - call it from a background thread.
        /// Tries 3 ways, fastest first.
        /// </summary>
        public WaveStream OpenAudio(string videoId, CancellationToken ct)
        {
            Exception? lastError = null;

            // 1) YoutubeExplode stream, decoded by Windows Media Foundation (AAC/m4a).
            string? directUrl = null;
            try
            {
                var manifest = _client.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct)
                    .AsTask().GetAwaiter().GetResult();

                var audio = manifest.GetAudioOnlyStreams()
                    .OrderByDescending(s => s.Container.Name.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(s => s.Bitrate.BitsPerSecond)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException("No audio stream found for this video");

                directUrl = audio.Url;

                var stream = _client.Videos.Streams.GetAsync(audio, ct).AsTask().GetAwaiter().GetResult();
                return new StreamMediaFoundationReader(stream, ReaderSettings);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                Log?.Invoke($"Stream method 1 failed: {ex.Message}");
            }

            // 2) Let Media Foundation stream the URL by itself.
            if (directUrl != null)
            {
                try
                {
                    return new MediaFoundationReader(directUrl, ReaderSettings);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Log?.Invoke($"Stream method 2 failed: {ex.Message}");
                }
            }

            // 3) Backup: ask yt-dlp for a playable URL.
            try
            {
                ct.ThrowIfCancellationRequested();
                var exe = EnsureYtDlpAsync(ct).GetAwaiter().GetResult();
                var lines = RunYtDlpAsync(exe, ct,
                        "-f", "bestaudio[ext=m4a]/bestaudio", "-g", "--no-playlist", "--no-warnings",
                        $"https://www.youtube.com/watch?v={videoId}")
                    .GetAwaiter().GetResult();

                var url = lines.FirstOrDefault(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidOperationException("yt-dlp returned no URL");
                return new MediaFoundationReader(url, ReaderSettings);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
            }

            throw new InvalidOperationException($"Couldn't play this video: {lastError?.Message}", lastError);
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
