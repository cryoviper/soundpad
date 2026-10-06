using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BoomBx.Models;
using System;

namespace BoomBx.Views
{
    /// <summary>
    /// Mini player bar at the bottom: control whatever is playing from any tab
    /// (pause, replay, stop, seek) without scrolling back up.
    /// </summary>
    public partial class MainWindow : Window
    {
        private enum LastPlayed { None, Sound, Tts }

        private LastPlayed _lastPlayedKind;
        private SoundItem? _lastPlayedSound;
        private DispatcherTimer? _playerTimer;
        private bool _playerSeekDragging;
        private bool _playerUpdatingSeek;

        private void InitializePlayer()
        {
            _playerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _playerTimer.Tick += (_, _) => UpdatePlayer();
            _playerTimer.Start();

            PlayerSeekSlider.AddHandler(InputElement.PointerPressedEvent, (_, _) => _playerSeekDragging = true,
                                        RoutingStrategies.Tunnel, handledEventsToo: true);
            PlayerSeekSlider.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
            {
                _playerSeekDragging = false;
                SeekPlayer(PlayerSeekSlider.Value);
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            PlayerSeekSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || _playerUpdatingSeek) return;
                if (_playerSeekDragging) ViewModel.PlayerElapsedText = TimeText.Format(PlayerSeekSlider.Value);
                else SeekPlayer(PlayerSeekSlider.Value);
            };

            Closing += (_, _) => _playerTimer?.Stop();
        }

        /// <summary>Called when a soundboard / YouTube sound starts.</summary>
        private void ShowPlayerForSound(SoundItem sound)
        {
            _lastPlayedKind = LastPlayed.Sound;
            _lastPlayedSound = sound;
            ViewModel.PlayerTitle = sound.Name;
            ViewModel.PlayerSubtitle = sound.IsYouTube ? "YouTube  ·  live" : "Soundboard";
            ViewModel.PlayerIconPath = sound.IconPath;
            ViewModel.PlayerThumb = null;
            ViewModel.PlayerElapsedText = TimeText.Format(sound.StartSeconds);
            ViewModel.PlayerTotalText = "";
            ViewModel.PlayerVisible = true;
        }

        /// <summary>Called when text to speech starts.</summary>
        private void ShowPlayerForTts(string text, string voiceName)
        {
            _lastPlayedKind = LastPlayed.Tts;
            _lastPlayedSound = null;
            ViewModel.PlayerTitle = $"\"{text.TrimTo(60)}\"";
            ViewModel.PlayerSubtitle = $"Text to speech  ·  {voiceName}";
            ViewModel.PlayerIconPath = AppPaths.DefaultIcon;
            ViewModel.PlayerThumb = null;
            ViewModel.PlayerVisible = true;
        }

        private void UpdatePlayer()
        {
            ViewModel.PlayerIsPlaying = _currentPlaybackState == PlaybackState.Playing;
            if (!ViewModel.PlayerVisible) return;

            double elapsed, total;
            if (_ytSource != null)
            {
                if (_ytSource.IsBuffering)
                {
                    ViewModel.PlayerCanSeek = false;
                    ViewModel.PlayerElapsedText = "Loading...";
                    return;
                }
                elapsed = _ytSource.ElapsedSeconds;
                total = _ytSource.TotalSeconds;
            }
            else if (_speakerLoopStream != null)
            {
                try
                {
                    var src = _speakerLoopStream.Source;
                    elapsed = src.CurrentTime.TotalSeconds;
                    total = src.TotalTime.TotalSeconds;
                }
                catch
                {
                    return;
                }
            }
            else
            {
                // stopped: keep the bar so Replay still works
                ViewModel.PlayerCanSeek = false;
                return;
            }

            ViewModel.PlayerCanSeek = total > 0;
            if (total > 0) ViewModel.PlayerTotalText = TimeText.Format(total);
            if (_playerSeekDragging) return;

            ViewModel.PlayerElapsedText = TimeText.Format(elapsed);
            _playerUpdatingSeek = true;
            try
            {
                if (total > 0) PlayerSeekSlider.Maximum = total;
                PlayerSeekSlider.Value = Math.Clamp(elapsed, 0, PlayerSeekSlider.Maximum);
            }
            finally
            {
                _playerUpdatingSeek = false;
            }
        }

        private void SeekPlayer(double seconds)
        {
            try
            {
                if (_ytSource != null)
                {
                    _ytSource.Seek(seconds);
                }
                else if (_virtualLoopStream != null && _speakerLoopStream != null)
                {
                    var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
                    if (t >= _speakerLoopStream.Source.TotalTime) return;
                    _virtualLoopStream.Source.CurrentTime = t;
                    _speakerLoopStream.Source.CurrentTime = t;
                }
                ViewModel.PlayerElapsedText = TimeText.Format(seconds);
            }
            catch (Exception ex)
            {
                Logger.Log($"Seek failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ buttons

        private void PlayerPlayPause_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentPlaybackState == PlaybackState.Stopped) PlayerReplay();
            else TogglePauseResume();
        }

        private void PlayerReplay_Click(object? sender, RoutedEventArgs e) => PlayerReplay();

        private void PlayerReplay()
        {
            if (_lastPlayedKind == LastPlayed.Tts && _lastTtsAudio != null)
                StartTtsPlayback(_lastTtsAudio);
            else if (_lastPlayedKind == LastPlayed.Sound && _lastPlayedSound != null)
                StartAudioProcessing(_lastPlayedSound);
        }

        private void PlayerStop_Click(object? sender, RoutedEventArgs e) => StopAudioProcessing();

        private void PlayerClose_Click(object? sender, RoutedEventArgs e)
        {
            StopAudioProcessing(updateStatus: false);
            ViewModel.PlayerVisible = false;
        }

        /// <summary>Clicking the title jumps to the tab the sound came from.</summary>
        private void PlayerOpenSource_Click(object? sender, RoutedEventArgs e)
        {
            if (_lastPlayedKind == LastPlayed.Tts)
                TextToSpeechNav.IsChecked = true;
            else if (_lastPlayedSound != null && _lastPlayedSound == ViewModel.YtCurrentSound)
                YouTubeNav.IsChecked = true;
            else
                SoundsNav.IsChecked = true;
        }
    }
}
