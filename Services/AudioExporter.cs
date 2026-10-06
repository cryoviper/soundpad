using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BoomBx.Services
{
    /// <summary>
    /// Turns in-memory audio (YouTube m4a etc.) into a normal MP3 file on disk, optionally trimmed.
    /// </summary>
    public static class AudioExporter
    {
        private static readonly WaveFormat Target = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        /// <summary>
        /// Saves <paramref name="audio"/> into <paramref name="folder"/> as MP3 (WAV if this PC has no MP3 encoder).
        /// Returns the full path of the new file.
        /// </summary>
        public static Task<string> SaveAsync(byte[] audio, string folder, string title,
                                             double startSeconds, double endSeconds, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                MediaFoundationApi.Startup();

                var baseName = SafeFileName(title);
                if (startSeconds > 0 || endSeconds > 0)
                    baseName += $" ({TimeText(startSeconds)}-{(endSeconds > 0 ? TimeText(endSeconds) : "end")})";

                var mp3Path = UniquePath(folder, baseName, ".mp3");
                try
                {
                    var (mp3Source, mp3Reader) = BuildProvider(audio, startSeconds, endSeconds);
                    using (mp3Reader)
                        MediaFoundationEncoder.EncodeToMp3(mp3Source.ToWaveProvider16(), mp3Path, 192000);
                    ct.ThrowIfCancellationRequested();
                    return mp3Path;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Windows "N" editions ship without the MP3 encoder - fall back to WAV.
                    Logger.Log($"MP3 encode failed, saving WAV instead: {ex.Message}");
                    try { if (File.Exists(mp3Path)) File.Delete(mp3Path); } catch { }

                    var wavPath = UniquePath(folder, baseName, ".wav");
                    var (wavSource, wavReader) = BuildProvider(audio, startSeconds, endSeconds);
                    using (wavReader)
                        WaveFileWriter.CreateWaveFile16(wavPath, wavSource);
                    return wavPath;
                }
            }, ct);
        }

        private static (ISampleProvider Samples, WaveStream Reader) BuildProvider(byte[] audio, double startSeconds, double endSeconds)
        {
            var reader = YouTubeService.OpenDecoder(audio);
            var samples = AudioService.ConvertFormat(reader.ToSampleProvider(), Target);
            var trimmed = new OffsetSampleProvider(samples);
            if (startSeconds > 0) trimmed.SkipOver = TimeSpan.FromSeconds(startSeconds);
            if (endSeconds > startSeconds) trimmed.Take = TimeSpan.FromSeconds(endSeconds - startSeconds);
            return (trimmed, reader);
        }

        public static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
            while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
            if (clean.Length > 80) clean = clean[..80].Trim();
            return clean.Length == 0 ? "sound" : clean;
        }

        public static string UniquePath(string folder, string baseName, string ext)
        {
            var path = Path.Combine(folder, baseName + ext);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(folder, $"{baseName} {i}{ext}");
            return path;
        }

        private static string TimeText(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}.{t.Minutes:00}.{t.Seconds:00}" : $"{t.Minutes}.{t.Seconds:00}";
        }
    }
}
