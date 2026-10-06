using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BoomBx.Models;
using BoomBx.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BoomBx.Views
{
    /// <summary>
    /// Soundboard extras: drag and drop, more formats, per-sound hotkeys, right-click menu, mixer + download folder.
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly string[] SupportedAudioPatterns =
            { "*.mp3", "*.wav", "*.m4a", "*.aac", "*.flac", "*.wma", "*.aiff", "*.aif" };

        private static readonly HashSet<string> SupportedAudioExtensions =
            SupportedAudioPatterns.Select(p => p.TrimStart('*')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        private SoundItem? _soundGettingHotkey;
        private DispatcherTimer? _settingsSaveTimer;

        private void InitializeSoundboardExtras()
        {
            // Drag audio files from Explorer onto the sounds area.
            DragDrop.SetAllowDrop(SoundsDropZone, true);
            SoundsDropZone.AddHandler(DragDrop.DragOverEvent, OnSoundsDragOver);
            SoundsDropZone.AddHandler(DragDrop.DropEvent, OnSoundsDrop);

            // Double-click a tile = play it.
            SoundsList.DoubleTapped += (_, e) =>
            {
                if ((e.Source as StyledElement)?.DataContext is SoundItem item) StartAudioProcessing(item);
            };

            // Right-click selects the sound under the mouse, so the menu acts on the right one.
            SoundsList.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(SoundsList).Properties.IsRightButtonPressed) return;
                if ((e.Source as StyledElement)?.DataContext is SoundItem item) ViewModel.SelectedSound = item;
            }, RoutingStrategies.Tunnel);
        }

        // ------------------------------------------------------------------ adding files

        private void AddFilesToBoard(IEnumerable<string> paths)
        {
            var board = ViewModel.SelectedSoundboard ?? ViewModel.Soundboards.FirstOrDefault();
            if (board == null) return;

            int added = 0, skipped = 0;
            foreach (var path in paths)
            {
                if (!File.Exists(path) || !SupportedAudioExtensions.Contains(Path.GetExtension(path)))
                {
                    skipped++;
                    continue;
                }
                if (board.Sounds.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

                var item = new SoundItem { Path = path, Name = Path.GetFileNameWithoutExtension(path) };
                item.PropertyChanged += SoundItem_PropertyChanged;
                board.Sounds.Add(item);
                added++;
            }

            if (added > 0) SaveSoundLibrary();
            UpdateStatus(skipped > 0
                ? $"Added {added} sound(s). Skipped {skipped} file(s) that aren't audio."
                : $"Added {added} sound(s) to \"{board.Name}\"");
        }

        private void OnSoundsDragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnSoundsDrop(object? sender, DragEventArgs e)
        {
            var files = e.Data.GetFiles();
            if (files == null) return;

            var paths = new List<string>();
            foreach (var item in files)
            {
                var path = item.TryGetLocalPath();
                if (path == null) continue;
                if (Directory.Exists(path))
                    paths.AddRange(Directory.EnumerateFiles(path)); // a whole folder of sounds
                else
                    paths.Add(path);
            }
            AddFilesToBoard(paths);
            e.Handled = true;
        }

        // ------------------------------------------------------------------ right-click menu

        private void CtxPlay_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedSound is { } s) StartAudioProcessing(s);
        }

        private void CtxHotkey_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedSound is { } s) StartSoundHotkeyCapture(s);
        }

        private void CtxShowFile_Click(object? sender, RoutedEventArgs e)
        {
            var s = ViewModel.SelectedSound;
            if (s == null) return;
            if (s.IsYouTube)
            {
                OpenOnYouTube_Click(sender, e);
                return;
            }
            if (File.Exists(s.Path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{s.Path}\"") { UseShellExecute = true });
        }

        // ------------------------------------------------------------------ per-sound hotkeys

        private void SetSoundHotkey_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedSound is { } s) StartSoundHotkeyCapture(s);
        }

        private void ClearSoundHotkey_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedSound is not { } s) return;
            s.Hotkey = null;
            SaveSoundLibrary();
            LoadAndRegisterHotkeys();
            UpdateStatus($"Hotkey removed from \"{s.Name}\"");
        }

        private void StartSoundHotkeyCapture(SoundItem sound)
        {
            _currentlySettingHotkey = null;
            _soundGettingHotkey = sound;
            _hotkeyManager?.UnregisterAll(); // so the key doesn't trigger something while we listen
            UpdateStatus($"Press the key for \"{sound.Name}\" (like F1, NumPad1, Ctrl+Q). Esc = cancel.", true);
            KeyDown -= Hotkey_KeyDown;
            KeyDown += Hotkey_KeyDown;
        }

        private void FinishSoundHotkey(KeyEventArgs e)
        {
            e.Handled = true;

            // Wait for the real key when only Ctrl/Shift/Alt is down.
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
                return;

            KeyDown -= Hotkey_KeyDown;
            var sound = _soundGettingHotkey;
            _soundGettingHotkey = null;
            if (sound == null) return;

            if (e.Key == Key.Escape)
            {
                LoadAndRegisterHotkeys();
                UpdateStatus("Hotkey not changed");
                return;
            }

            var gesture = new KeyGesture(e.Key, e.KeyModifiers).ToString();

            // One key = one sound.
            foreach (var other in ViewModel.Soundboards.SelectMany(b => b.Sounds))
                if (other != sound && other.Hotkey == gesture) other.Hotkey = null;

            sound.Hotkey = gesture;
            SaveSoundLibrary();
            LoadAndRegisterHotkeys();
            UpdateStatus($"\"{sound.Name}\" now plays with {gesture}");
        }

        private void RegisterSoundHotkeys()
        {
            if (_hotkeyManager == null) return;

            // Voice changer on/off
            try
            {
                var voiceGesture = KeyGesture.Parse(_settings.VoiceToggleHotkey);
                _hotkeyManager.RegisterHotkey(voiceGesture, () => Dispatcher.UIThread.Post(ToggleVoiceChanger));
                ViewModel.VoiceToggleHotkey = voiceGesture.ToString();
            }
            catch (Exception ex)
            {
                Logger.Log($"Bad voice hotkey '{_settings.VoiceToggleHotkey}': {ex.Message}");
            }

            foreach (var sound in ViewModel.Soundboards.SelectMany(b => b.Sounds).Where(s => s.HasHotkey))
            {
                try
                {
                    var target = sound;
                    _hotkeyManager.RegisterHotkey(KeyGesture.Parse(sound.Hotkey!),
                        () => Dispatcher.UIThread.Post(() => PlaySoundFromHotkey(target)));
                }
                catch (Exception ex)
                {
                    Logger.Log($"Bad hotkey '{sound.Hotkey}' on {sound.Name}: {ex.Message}");
                }
            }
        }

        /// <summary>Press once to play, again to stop.</summary>
        private void PlaySoundFromHotkey(SoundItem sound)
        {
            if (_playingSound == sound && _currentPlaybackState != PlaybackState.Stopped)
                StopAudioProcessing();
            else
                StartAudioProcessing(sound);
        }

        // ------------------------------------------------------------------ mixer + downloads

        private void InitializeMixer()
        {
            ViewModel.MasterVirtualVolume = _settings.MasterVirtualVolume;
            ViewModel.MasterSpeakerVolume = _settings.MasterSpeakerVolume;
            ViewModel.MicVolume = _settings.MicVolume;
            ViewModel.MicMuted = _settings.MicMuted;
            ViewModel.DownloadFolder = _settings.DownloadFolder;
            ViewModel.NoiseGateEnabled = _settings.NoiseGateEnabled;
            ViewModel.NoiseGateDb = _settings.NoiseGateDb;
            ApplyMicLevel();

            // Live mic meter on the Settings tab.
            var meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            meterTimer.Tick += (_, _) =>
            {
                var cleanup = _audioService?.MicCleanup;
                if (cleanup == null || (SettingsNav.IsChecked != true && VoiceChangerNav.IsChecked != true)) return;
                var db = cleanup.TakePeakDb();
                var level = Math.Clamp((db + 80) / 80 * 100, 0, 100);
                // fall slowly, rise fast - easier to read
                ViewModel.MicMeter = level > ViewModel.MicMeter ? level : ViewModel.MicMeter * 0.85;
                ViewModel.MicGateOpen = cleanup.IsOpen;
            };
            meterTimer.Start();

            _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _settingsSaveTimer.Tick += (_, _) =>
            {
                _settingsSaveTimer.Stop();
                SettingsManager.SaveSettings(_settings);
            };

            ViewModel.MixerChanged += () =>
            {
                ApplyVolumes();
                ApplyMicLevel();
                _settings.MasterVirtualVolume = ViewModel.MasterVirtualVolume;
                _settings.MasterSpeakerVolume = ViewModel.MasterSpeakerVolume;
                _settings.MicVolume = ViewModel.MicVolume;
                _settings.MicMuted = ViewModel.MicMuted;
                _settings.NoiseGateEnabled = ViewModel.NoiseGateEnabled;
                _settings.NoiseGateDb = ViewModel.NoiseGateDb;
                // Save a moment after the slider stops moving, not on every tick.
                _settingsSaveTimer.Stop();
                _settingsSaveTimer.Start();
            };
        }

        private void ApplyMicLevel()
        {
            _audioService?.SetMicLevel(ViewModel.MicMuted ? 0f : (float)(ViewModel.MicVolume / 100.0));
            _audioService?.SetNoiseGate(ViewModel.NoiseGateEnabled, (float)ViewModel.NoiseGateDb);
        }

        private async void ChangeDownloadFolder_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Where should downloaded sounds go?",
                AllowMultiple = false
            });
            var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (path == null) return;

            ViewModel.DownloadFolder = path;
            _settings.DownloadFolder = path;
            SettingsManager.SaveSettings(_settings);
        }

        /// <summary>Classic Windows sound panel (where VB-Cable format + default device are set).</summary>
        private void OpenSoundSettings_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,,1") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                UpdateStatus($"Couldn't open sound settings: {ex.Message}", true);
            }
        }

        private void OpenDownloadFolder_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(ViewModel.DownloadFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ViewModel.DownloadFolder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                UpdateStatus($"Couldn't open folder: {ex.Message}", true);
            }
        }
    }
}
