using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BoomBx.Models;
using BoomBx.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BoomBx.Views
{
    /// <summary>
    /// YouTube tab: search YouTube and play any result live through VB-Cable + speakers.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly YouTubeService _youTubeService = new();
        private CancellationTokenSource? _ytSearchCts;
        private DispatcherTimer? _ytTimer;
        private YouTubeAudioSource? _ytAnnouncedSource;

        private void InitializeYouTube()
        {
            _youTubeService.Log += message => Dispatcher.UIThread.Post(() => ViewModel.YtStatus = message);

            _ytTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _ytTimer.Tick += (_, _) => UpdateYouTubeProgress();
            _ytTimer.Start();

            // Row buttons live inside a template, so listen for their clicks on the list itself.
            YtResultsList.AddHandler(Button.ClickEvent, YtResultButton_Click);

            // Tunnel so Enter is caught before the text box can eat it.
            YtSearchBox.AddHandler(InputElement.KeyDownEvent, YtSearchBox_KeyDown, RoutingStrategies.Tunnel);

            Closing += (_, _) =>
            {
                _ytTimer?.Stop();
                _ytSearchCts?.Cancel();
            };
        }

        // ------------------------------------------------------------------ search

        private async void YtSearch_Click(object? sender, RoutedEventArgs e) => await RunYouTubeSearchAsync();

        private async void YtSearchBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await RunYouTubeSearchAsync();
        }

        private async Task RunYouTubeSearchAsync()
        {
            var query = ViewModel.YtQuery?.Trim() ?? "";
            if (query.Length == 0)
            {
                ViewModel.YtStatus = "Type something to search first.";
                return;
            }

            _ytSearchCts?.Cancel();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _ytSearchCts = cts;

            ViewModel.YtIsSearching = true;
            ViewModel.YtStatus = $"Searching \"{query}\"...";

            try
            {
                var results = await _youTubeService.SearchAsync(query, cts.Token);
                if (cts.IsCancellationRequested) return;

                ViewModel.YtResults.Clear();
                foreach (var r in results)
                {
                    r.IsPlaying = ViewModel.YtNowPlaying?.Id == r.Id && _ytSource != null;
                    ViewModel.YtResults.Add(r);
                }

                ViewModel.YtStatus = results.Count == 0
                    ? "No results. Try other words."
                    : $"{results.Count} results. Hit ▶ to play live, ⭐ to pin to your soundboard.";

                _ = LoadThumbnailsAsync(results, cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (_ytSearchCts == cts) ViewModel.YtStatus = "Search timed out. Check your internet and try again.";
            }
            catch (Exception ex)
            {
                ViewModel.YtStatus = $"❌ Search failed: {ex.Message}";
            }
            finally
            {
                if (_ytSearchCts == cts) ViewModel.YtIsSearching = false;
            }
        }

        private static async Task LoadThumbnailsAsync(IEnumerable<YouTubeResult> results, CancellationToken ct)
        {
            using var gate = new SemaphoreSlim(6);
            var jobs = results.Select(async r =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var bytes = await YouTubeService.DownloadBytesAsync(r.ThumbnailUrl, ct);
                    if (bytes == null) return;
                    var bitmap = new Bitmap(new MemoryStream(bytes));
                    await Dispatcher.UIThread.InvokeAsync(() => r.Thumbnail = bitmap);
                }
                catch { /* thumbnail is optional */ }
                finally
                {
                    gate.Release();
                }
            });

            try { await Task.WhenAll(jobs); }
            catch { /* cancelled */ }
        }

        // ------------------------------------------------------------------ playback

        private async void YtResultButton_Click(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not Button { Tag: YouTubeResult result } button) return;
            e.Handled = true;

            if (button.Classes.Contains("yt-play")) PlayOrToggle(result);
            else if (button.Classes.Contains("yt-pin")) await PinToSoundboardAsync(result);
        }

        private void PlayOrToggle(YouTubeResult result)
        {

            // Clicking the one that's already playing = pause/resume.
            if (ViewModel.YtNowPlaying == result && _ytSource != null && _playingSound == ViewModel.YtCurrentSound)
            {
                TogglePauseResume();
                return;
            }

            PlayYouTubeResult(result);
        }

        private void PlayYouTubeResult(YouTubeResult result)
        {
            if (!TimeText.TryParse(ViewModel.YtStartText, out var start))
            {
                ViewModel.YtStatus = "Start time looks wrong. Use like 0:15 or 1:02.";
                return;
            }
            if (result.DurationSeconds > 0 && start >= result.DurationSeconds) start = 0;

            var sound = new SoundItem
            {
                Name = result.Title,
                Path = $"youtube:{result.Id}",
                YouTubeId = result.Id,
                Volume = ViewModel.YtVolume,
                StartSeconds = start
            };

            ViewModel.YtCurrentSound = sound;
            StartAudioProcessing(sound);

            if (_playingSound != sound) return; // failed to start - status already shown

            ViewModel.YtNowPlaying = result;
            ViewModel.YtNowPlayingTitle = result.Title;
            ViewModel.YtProgressText = "Buffering...";
            ViewModel.YtProgress = 0;
            ViewModel.YtStatus = $"Loading \"{result.Title}\"...";
            foreach (var r in ViewModel.YtResults) r.IsPlaying = r == result;
        }

        private void YtPlayPause_Click(object? sender, RoutedEventArgs e)
        {
            if (_playingSound != null && _playingSound == ViewModel.YtCurrentSound)
            {
                TogglePauseResume();
            }
            else if (ViewModel.YtNowPlaying != null)
            {
                PlayYouTubeResult(ViewModel.YtNowPlaying); // play again
            }
        }

        private void YtStop_Click(object? sender, RoutedEventArgs e)
        {
            StopAudioProcessing();
        }

        private void TogglePauseResume()
        {
            if (_currentPlaybackState == PlaybackState.Playing) PauseAudioProcessing();
            else if (_currentPlaybackState == PlaybackState.Paused) ResumeAudioProcessing();
        }

        /// <summary>Called by StopAudioProcessing for every stop.</summary>
        private void OnPlaybackEndedForYouTubeTab()
        {
            void Reset()
            {
                foreach (var r in ViewModel.YtResults) r.IsPlaying = false;
                ViewModel.YtProgressText = "";
                ViewModel.YtProgress = 0;
            }

            if (Dispatcher.UIThread.CheckAccess()) Reset();
            else Dispatcher.UIThread.Post(Reset);
        }

        private void UpdateYouTubeProgress()
        {
            var source = _ytSource;
            if (source == null) return;

            // First audio arrived -> tell the user it's live.
            if (!source.IsBuffering && _ytAnnouncedSource != source)
            {
                _ytAnnouncedSource = source;
                UpdateStatus($"🎵 Playing {_playingSound?.Name}");
                if (_playingSound == ViewModel.YtCurrentSound)
                    ViewModel.YtStatus = "🔴 Live in your mic. Friends can hear it now.";
            }

            if (_playingSound != ViewModel.YtCurrentSound) return;

            if (source.IsBuffering)
            {
                ViewModel.YtProgressText = "Buffering...";
                return;
            }

            var total = source.TotalSeconds;
            var elapsed = source.ElapsedSeconds;
            ViewModel.YtProgressText = total > 0
                ? $"{TimeText.Format(elapsed)} / {TimeText.Format(total)}"
                : TimeText.Format(elapsed);
            ViewModel.YtProgress = total > 0 ? Math.Clamp(elapsed / total * 100, 0, 100) : 0;
        }

        // ------------------------------------------------------------------ pin to soundboard

        private async void YtPinNowPlaying_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.YtNowPlaying is { } result) await PinToSoundboardAsync(result);
        }

        private async Task PinToSoundboardAsync(YouTubeResult result)
        {
            var board = ViewModel.SelectedSoundboard ?? ViewModel.Soundboards.FirstOrDefault();
            if (board == null)
            {
                ViewModel.YtStatus = "Make a soundboard first (Sounds tab).";
                return;
            }

            TimeText.TryParse(ViewModel.YtStartText, out var start);
            if (result.DurationSeconds > 0 && start >= result.DurationSeconds) start = 0;

            if (board.Sounds.Any(s => s.YouTubeId == result.Id && Math.Abs(s.StartSeconds - start) < 0.5))
            {
                ViewModel.YtStatus = $"Already on \"{board.Name}\".";
                return;
            }

            var sound = new SoundItem
            {
                Name = result.Title.Length > 40 ? result.Title[..40].TrimEnd() + "…" : result.Title,
                Path = $"youtube:{result.Id}",
                YouTubeId = result.Id,
                Volume = ViewModel.YtVolume,
                StartSeconds = start
            };

            // Save the thumbnail as the button icon (small jpg, not the audio).
            var bytes = await YouTubeService.DownloadBytesAsync(result.ThumbnailUrl);
            if (bytes != null)
            {
                try
                {
                    var fileName = $"yt_{result.Id}.jpg";
                    await File.WriteAllBytesAsync(Path.Combine(AppPaths.IconsDir, fileName), bytes);
                    sound.IconPath = fileName;
                }
                catch { /* keep default icon */ }
            }

            sound.PropertyChanged += SoundItem_PropertyChanged;
            board.Sounds.Add(sound);
            SaveSoundLibrary();
            ViewModel.YtStatus = $"⭐ Pinned to \"{board.Name}\". Hotkeys work on it too.";
        }

        /// <summary>Start/End boxes for pinned YouTube clips: apply when the box loses focus.</summary>
        private void ClipTimeBox_LostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || ViewModel.SelectedSound is not { IsYouTube: true } sound) return;

            bool isStart = Equals(box.Tag, "start");
            if (!TimeText.TryParse(box.Text, out var seconds))
            {
                UpdateStatus("Time looks wrong. Use like 0:15 or 1:02.", true);
            }
            else if (isStart)
            {
                sound.StartSeconds = seconds;
            }
            else
            {
                sound.EndSeconds = seconds;
            }

            box.SetCurrentValue(TextBox.TextProperty, isStart ? sound.StartText : sound.EndText);
            SaveSoundLibrary();
        }

        private void OpenOnYouTube_Click(object? sender, RoutedEventArgs e)
        {
            var id = ViewModel.SelectedSound?.YouTubeId;
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                Process.Start(new ProcessStartInfo($"https://www.youtube.com/watch?v={id}") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                UpdateStatus($"Couldn't open browser: {ex.Message}", true);
            }
        }
    }
}
