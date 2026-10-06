using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    /// YouTube tab: search, play live into the mic, seek, trim a clip, pin it or save it as MP3.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly YouTubeService _youTubeService = new();
        private CancellationTokenSource? _ytSearchCts;
        private DispatcherTimer? _ytTimer;
        private YouTubeAudioSource? _ytAnnouncedSource;
        private bool _ytSeekDragging;
        private bool _ytUpdatingSeek;

        private void InitializeYouTube()
        {
            _youTubeService.Log += message => Dispatcher.UIThread.Post(() => ViewModel.YtStatus = message);

            _ytTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _ytTimer.Tick += (_, _) => UpdateYouTubeProgress();
            _ytTimer.Start();

            // Row buttons live inside a template, so listen for their clicks on the lists.
            YtResultsList.AddHandler(Button.ClickEvent, YtResultButton_Click);
            YtRecentList.AddHandler(Button.ClickEvent, YtResultButton_Click);

            // Tunnel so Enter is caught before the text box can eat it.
            YtSearchBox.AddHandler(InputElement.KeyDownEvent, YtSearchBox_KeyDown, RoutingStrategies.Tunnel);

            // Seek bar: jump when the user lets go (or uses arrow keys).
            YtSeekSlider.AddHandler(InputElement.PointerPressedEvent, (_, _) => _ytSeekDragging = true,
                                    RoutingStrategies.Tunnel, handledEventsToo: true);
            YtSeekSlider.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
            {
                _ytSeekDragging = false;
                SeekYouTube(YtSeekSlider.Value);
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            YtSeekSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || _ytUpdatingSeek) return;
                if (_ytSeekDragging) ViewModel.YtElapsedText = TimeText.Format(YtSeekSlider.Value); // preview while dragging
                else SeekYouTube(YtSeekSlider.Value);
            };

            // Recently played
            foreach (var clip in _settings.YouTubeRecent)
                ViewModel.YtRecent.Add(new YouTubeResult
                {
                    Id = clip.Id,
                    Title = clip.Title,
                    Channel = clip.Channel,
                    DurationSeconds = clip.DurationSeconds
                });
            ViewModel.YtHasRecent = ViewModel.YtRecent.Count > 0;
            _ = LoadThumbnailsAsync(ViewModel.YtRecent.ToList(), CancellationToken.None);

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

        /// <summary>Quick-search chips on the empty screen.</summary>
        private async void YtChip_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string query }) return;
            ViewModel.YtQuery = query;
            await RunYouTubeSearchAsync();
        }

        private void YtClearResults_Click(object? sender, RoutedEventArgs e)
        {
            ViewModel.YtResults.Clear();
            ViewModel.YtHasResults = false;
            ViewModel.YtQuery = "";
            ViewModel.YtStatus = "";
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
                ViewModel.YtHasResults = results.Count > 0;
                ViewModel.YtStatus = results.Count == 0 ? "No results. Try other words." : "";

                _ = LoadThumbnailsAsync(results, cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (_ytSearchCts == cts) ViewModel.YtStatus = "Search timed out. Check your internet and try again.";
            }
            catch (Exception ex)
            {
                Logger.Log($"[YouTube] search failed: {ex}");
                ViewModel.YtStatus = $"Search failed: {ex.Message}";
            }
            finally
            {
                if (_ytSearchCts == cts) ViewModel.YtIsSearching = false;
            }
        }

        private static async Task LoadThumbnailsAsync(IEnumerable<YouTubeResult> results, CancellationToken ct)
        {
            using var gate = new SemaphoreSlim(6);
            var jobs = results.Where(r => r.Thumbnail == null).Select(async r =>
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

        // ------------------------------------------------------------------ row buttons

        private async void YtResultButton_Click(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not Button { Tag: YouTubeResult result } button) return;
            e.Handled = true;

            if (button.Classes.Contains("yt-play")) PlayOrToggle(result);
            else if (button.Classes.Contains("yt-pin")) await PinToSoundboardAsync(result, useClip: false);
            else if (button.Classes.Contains("yt-save")) await SaveYouTubeAsync(result, useClip: false);
        }

        private void PlayOrToggle(YouTubeResult result)
        {
            // Clicking the one that's already playing = pause/resume.
            if (ViewModel.YtNowPlaying?.Id == result.Id && _ytSource != null && _playingSound == ViewModel.YtCurrentSound)
            {
                TogglePauseResume();
                return;
            }

            // New video: forget the old clip marks.
            if (ViewModel.YtNowPlaying?.Id != result.Id)
            {
                ViewModel.YtStartText = "";
                ViewModel.YtEndText = "";
            }
            PlayYouTubeResult(result, 0, 0);
        }

        // ------------------------------------------------------------------ playback

        private void PlayYouTubeResult(YouTubeResult result, double start, double end)
        {
            if (result.DurationSeconds > 0 && start >= result.DurationSeconds) start = 0;

            var sound = new SoundItem
            {
                Name = result.Title,
                Path = $"youtube:{result.Id}",
                YouTubeId = result.Id,
                Volume = ViewModel.YtVolume,
                StartSeconds = start,
                EndSeconds = end
            };

            ViewModel.YtCurrentSound = sound;
            StartAudioProcessing(sound);

            if (_playingSound != sound) return; // failed to start - status already shown

            ViewModel.YtNowPlaying = result;
            ViewModel.YtNowPlayingTitle = result.Title;
            ViewModel.PlayerThumb = result.Thumbnail;
            ViewModel.YtElapsedText = TimeText.Format(start);
            ViewModel.YtTotalText = result.DurationText;
            ViewModel.YtIsLoading = true;
            ViewModel.YtStatus = "Loading...";
            SetSeekBar(start, result.DurationSeconds);
            foreach (var r in ViewModel.YtResults.Concat(ViewModel.YtRecent)) r.IsPlaying = r.Id == result.Id;
        }

        private void YtPlayPause_Click(object? sender, RoutedEventArgs e)
        {
            if (_playingSound != null && _playingSound == ViewModel.YtCurrentSound)
                TogglePauseResume();
            else if (ViewModel.YtNowPlaying != null)
                PlayYouTubeResult(ViewModel.YtNowPlaying, 0, 0); // play again
        }

        private void YtStop_Click(object? sender, RoutedEventArgs e) => StopAudioProcessing();

        private void TogglePauseResume()
        {
            if (_currentPlaybackState == PlaybackState.Playing) PauseAudioProcessing();
            else if (_currentPlaybackState == PlaybackState.Paused) ResumeAudioProcessing();
        }

        private void SeekYouTube(double seconds)
        {
            if (_ytSource == null || _playingSound != ViewModel.YtCurrentSound) return;
            _ytSource.Seek(seconds);
            ViewModel.YtElapsedText = TimeText.Format(seconds);
        }

        private void SetSeekBar(double value, double maximum)
        {
            _ytUpdatingSeek = true;
            try
            {
                if (maximum > 0) YtSeekSlider.Maximum = maximum;
                YtSeekSlider.Value = Math.Clamp(value, 0, YtSeekSlider.Maximum);
            }
            finally
            {
                _ytUpdatingSeek = false;
            }
        }

        /// <summary>Called by StopAudioProcessing for every stop.</summary>
        private void OnPlaybackEndedForYouTubeTab()
        {
            void Reset()
            {
                foreach (var r in ViewModel.YtResults.Concat(ViewModel.YtRecent)) r.IsPlaying = false;
                ViewModel.YtIsLoading = false;
                ViewModel.YtIsPlaying = false;
                if (ViewModel.YtStatus is "Loading..." or "Playing in your mic" || ViewModel.YtStatus.StartsWith("Loading "))
                    ViewModel.YtStatus = "";
            }

            if (Dispatcher.UIThread.CheckAccess()) Reset();
            else Dispatcher.UIThread.Post(Reset);
        }

        private void UpdateYouTubeProgress()
        {
            var source = _ytSource;
            if (source == null) return;
            bool isTabSound = _playingSound == ViewModel.YtCurrentSound;

            // Safety net: the speaker output stopped asking for audio (device error) -> stop instead of hanging.
            if (_currentPlaybackState == PlaybackState.Playing && source.MsSinceSpeakerRead > 4000)
            {
                Logger.Log("[YouTube] speaker output stopped reading - stopping playback");
                StopAudioProcessing(updateStatus: false);
                UpdateStatus("Audio output stopped. Check the playback device in Settings.", true);
                if (isTabSound) ViewModel.YtStatus = "Audio output stopped. Check the playback device in Settings.";
                return;
            }

            if (source.IsBuffering)
            {
                if (isTabSound)
                {
                    var pct = (int)Math.Round(source.LoadProgress * 100);
                    ViewModel.YtStatus = pct > 0 && pct < 100 ? $"Loading {pct}%" : "Loading...";
                }
                return;
            }

            // First audio arrived -> tell the user it's live.
            if (_ytAnnouncedSource != source)
            {
                _ytAnnouncedSource = source;
                UpdateStatus($"Playing {_playingSound?.Name}");
                if (isTabSound)
                {
                    ViewModel.YtIsLoading = false;
                    ViewModel.YtStatus = "Playing in your mic";
                    if (ViewModel.YtNowPlaying != null) AddToRecent(ViewModel.YtNowPlaying);
                }
            }

            if (!isTabSound || _ytSeekDragging) return;

            var total = source.TotalSeconds;
            var elapsed = source.ElapsedSeconds;
            ViewModel.YtElapsedText = TimeText.Format(elapsed);
            if (total > 0) ViewModel.YtTotalText = TimeText.Format(total);
            SetSeekBar(elapsed, total);
        }

        private void AddToRecent(YouTubeResult result)
        {
            var existing = ViewModel.YtRecent.FirstOrDefault(r => r.Id == result.Id);
            if (existing != null) ViewModel.YtRecent.Remove(existing);
            var copy = new YouTubeResult
            {
                Id = result.Id,
                Title = result.Title,
                Channel = result.Channel,
                DurationSeconds = result.DurationSeconds,
                Thumbnail = result.Thumbnail,
                IsPlaying = result.IsPlaying
            };
            ViewModel.YtRecent.Insert(0, copy);
            while (ViewModel.YtRecent.Count > 12) ViewModel.YtRecent.RemoveAt(ViewModel.YtRecent.Count - 1);
            ViewModel.YtHasRecent = true;

            _settings.YouTubeRecent = ViewModel.YtRecent.Select(r => new RecentClip
            {
                Id = r.Id,
                Title = r.Title,
                Channel = r.Channel,
                DurationSeconds = r.DurationSeconds
            }).ToList();
            SettingsManager.SaveSettings(_settings);
        }

        // ------------------------------------------------------------------ clip marks

        private void YtMarkStart_Click(object? sender, RoutedEventArgs e)
        {
            if (!TryGetPlayPosition(out var now)) return;
            ViewModel.YtStartText = TimeText.Format(now);
            if (TimeText.TryParse(ViewModel.YtEndText, out var end) && end > 0 && end <= now) ViewModel.YtEndText = "";
        }

        private void YtMarkEnd_Click(object? sender, RoutedEventArgs e)
        {
            if (!TryGetPlayPosition(out var now)) return;
            TimeText.TryParse(ViewModel.YtStartText, out var start);
            if (now <= start)
            {
                ViewModel.YtStatus = "End must be after the start.";
                return;
            }
            ViewModel.YtEndText = TimeText.Format(now);
        }

        private void YtClearClip_Click(object? sender, RoutedEventArgs e)
        {
            ViewModel.YtStartText = "";
            ViewModel.YtEndText = "";
        }

        private void YtPreviewClip_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.YtNowPlaying is not { } result) return;
            if (!TryGetClip(out var start, out var end)) return;
            PlayYouTubeResult(result, start, end);
        }

        private bool TryGetPlayPosition(out double seconds)
        {
            seconds = 0;
            if (_ytSource == null || _playingSound != ViewModel.YtCurrentSound)
            {
                ViewModel.YtStatus = "Play the video first, then mark the spot.";
                return false;
            }
            seconds = Math.Round(_ytSource.ElapsedSeconds, 1);
            return true;
        }

        private bool TryGetClip(out double start, out double end)
        {
            end = 0;
            if (!TimeText.TryParse(ViewModel.YtStartText, out start) || !TimeText.TryParse(ViewModel.YtEndText, out end))
            {
                ViewModel.YtStatus = "Clip time looks wrong. Use like 0:15 or 1:02.";
                return false;
            }
            if (end > 0 && end <= start)
            {
                ViewModel.YtStatus = "End must be after the start.";
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ pin + save

        private async void YtPinNowPlaying_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.YtNowPlaying is { } result) await PinToSoundboardAsync(result, useClip: true);
        }

        private async void YtSaveNowPlaying_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.YtNowPlaying is { } result) await SaveYouTubeAsync(result, useClip: true);
        }

        /// <summary>Adds the video to the soundboard as a live (streamed) sound.</summary>
        private async Task PinToSoundboardAsync(YouTubeResult result, bool useClip)
        {
            var board = ViewModel.SelectedSoundboard ?? ViewModel.Soundboards.FirstOrDefault();
            if (board == null)
            {
                ViewModel.YtStatus = "Make a soundboard first (Sounds tab).";
                return;
            }

            double start = 0, end = 0;
            if (useClip && !TryGetClip(out start, out end)) return;

            if (board.Sounds.Any(s => s.YouTubeId == result.Id && Math.Abs(s.StartSeconds - start) < 0.5
                                      && Math.Abs(s.EndSeconds - end) < 0.5))
            {
                ViewModel.YtStatus = $"Already on \"{board.Name}\".";
                return;
            }

            var sound = new SoundItem
            {
                Name = ShortTitle(result.Title),
                Path = $"youtube:{result.Id}",
                YouTubeId = result.Id,
                Volume = ViewModel.YtVolume,
                StartSeconds = start,
                EndSeconds = end,
                IconPath = await SaveThumbnailIconAsync(result) ?? AppPaths.DefaultIcon
            };

            sound.PropertyChanged += SoundItem_PropertyChanged;
            board.Sounds.Add(sound);
            SaveSoundLibrary();
            ViewModel.YtStatus = $"Pinned to \"{board.Name}\" (streams live). Give it a hotkey on the Sounds tab.";
        }

        /// <summary>Saves the audio as an MP3 in the download folder and adds it to the soundboard.</summary>
        private async Task SaveYouTubeAsync(YouTubeResult result, bool useClip)
        {
            if (ViewModel.YtIsSaving) return;

            double start = 0, end = 0;
            if (useClip && !TryGetClip(out start, out end)) return;

            ViewModel.YtIsSaving = true;
            ViewModel.YtStatus = $"Saving \"{ShortTitle(result.Title)}\"...";
            try
            {
                var bytes = await Task.Run(() => _youTubeService.GetAudioBytes(result.Id,
                    p => Dispatcher.UIThread.Post(() => ViewModel.YtStatus = $"Saving... {(int)(p * 100)}%"),
                    CancellationToken.None));

                var folder = string.IsNullOrWhiteSpace(ViewModel.DownloadFolder) ? AppPaths.DataDir : ViewModel.DownloadFolder;
                var path = await AudioExporter.SaveAsync(bytes, folder, result.Title, start, end);

                var board = ViewModel.SelectedSoundboard ?? ViewModel.Soundboards.FirstOrDefault();
                if (board != null)
                {
                    var item = new SoundItem
                    {
                        Path = path,
                        Name = ShortTitle(result.Title),
                        Volume = ViewModel.YtVolume,
                        IconPath = await SaveThumbnailIconAsync(result) ?? AppPaths.DefaultIcon
                    };
                    item.PropertyChanged += SoundItem_PropertyChanged;
                    board.Sounds.Add(item);
                    SaveSoundLibrary();
                }

                ViewModel.YtStatus = $"Saved {Path.GetFileName(path)} and added it to your board (works offline).";
            }
            catch (Exception ex)
            {
                Logger.Log($"[YouTube] save failed: {ex}");
                ViewModel.YtStatus = $"Couldn't save: {ex.Message}";
            }
            finally
            {
                ViewModel.YtIsSaving = false;
            }
        }

        private static string ShortTitle(string title) =>
            title.Length > 40 ? title[..40].TrimEnd() + "..." : title;

        /// <summary>Saves the video thumbnail as a button icon. Returns the icon file name.</summary>
        private static async Task<string?> SaveThumbnailIconAsync(YouTubeResult result)
        {
            var bytes = await YouTubeService.DownloadBytesAsync(result.ThumbnailUrl);
            if (bytes == null) return null;
            try
            {
                var fileName = $"yt_{result.Id}.jpg";
                await File.WriteAllBytesAsync(Path.Combine(AppPaths.IconsDir, fileName), bytes);
                return fileName;
            }
            catch
            {
                return null;
            }
        }

        private void OpenDownloadFolderFromYt_Click(object? sender, RoutedEventArgs e) => OpenDownloadFolder_Click(sender, e);

        // ------------------------------------------------------------------ pinned clip editing (Sounds tab)

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
