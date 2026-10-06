using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BoomBx.Models;
using BoomBx.Services;
using NAudio.Wave;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BoomBx.Views
{
    /// <summary>
    /// Text to Speech tab: Microsoft neural voices (Hindi, English India, 100+ languages) + offline eSpeak.
    /// </summary>
    public partial class MainWindow : Window
    {
        private TtsService? _ttsService;
        private TtsAudio? _lastTtsAudio;
        private string _lastTtsText = "";
        private bool _ttsVoicesLoaded;
        private CancellationTokenSource? _ttsCts;

        private void InitializeTts()
        {
            _ttsService = new TtsService();

            ViewModel.TtsRate = _settings.TtsRate;
            ViewModel.TtsPitchHz = _settings.TtsPitchHz;
            ViewModel.SetTtsVoices(_ttsService.Voices, _settings.TtsVoiceId);
            foreach (var t in _settings.TtsRecent) ViewModel.TtsRecent.Add(t);

            // Chips are inside a template - listen on the list.
            TtsRecentList.AddHandler(Button.ClickEvent, TtsRecent_Click);

            // Ctrl+Enter in the text box = speak
            TtsTextBox.AddHandler(KeyDownEvent, async (_, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    e.Handled = true;
                    await SpeakTtsAsync();
                }
            }, RoutingStrategies.Tunnel);
        }

        private async void TextToSpeechNav_Checked(object? sender, RoutedEventArgs e)
        {
            if (_ttsVoicesLoaded || _ttsService == null) return;
            _ttsVoicesLoaded = true;

            ViewModel.TtsStatus = "Loading all voices...";
            var voices = await _ttsService.LoadAllVoicesAsync(CancellationToken.None);
            ViewModel.SetTtsVoices(voices, ViewModel.TtsSelectedVoice?.Id ?? _settings.TtsVoiceId);
            ViewModel.TtsStatus = $"{voices.Count(v => v.Engine == TtsEngine.Neural)} natural voices ready";
        }

        private async void PreviewTts_Click(object? sender, RoutedEventArgs e) => await SpeakTtsAsync();

        private async void TtsRecent_Click(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not Button { Tag: string text } button || !button.Classes.Contains("tts-recent")) return;
            e.Handled = true;
            ViewModel.TtsText = text;
            await SpeakTtsAsync();
        }

        private void TtsReset_Click(object? sender, RoutedEventArgs e)
        {
            ViewModel.TtsRate = 0;
            ViewModel.TtsPitchHz = 0;
        }

        /// <summary>Makes the audio (or reuses it) and plays it into the mic.</summary>
        private async Task SpeakTtsAsync()
        {
            var audio = await MakeTtsAudioAsync();
            if (audio == null) return;
            StartTtsPlayback(audio);
        }

        private async Task<TtsAudio?> MakeTtsAudioAsync()
        {
            if (_ttsService == null) return null;
            var text = ViewModel.TtsText?.Trim() ?? "";
            var voice = ViewModel.TtsSelectedVoice;
            if (text.Length == 0)
            {
                ViewModel.TtsStatus = "Type something first.";
                return null;
            }
            if (voice == null)
            {
                ViewModel.TtsStatus = "Pick a voice first.";
                return null;
            }

            _ttsCts?.Cancel();
            var cts = new CancellationTokenSource();
            _ttsCts = cts;
            ViewModel.TtsBusy = true;
            ViewModel.TtsStatus = "Making voice...";

            try
            {
                var audio = await _ttsService.SpeakAsync(text, voice, (int)Math.Round(ViewModel.TtsRate),
                                                         (int)Math.Round(ViewModel.TtsPitchHz), cts.Token);
                _lastTtsAudio = audio;
                _lastTtsText = text;
                ViewModel.TtsStatus = "";
                RememberTts(text, voice);
                return audio;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TTS] {ex}");
                ViewModel.TtsStatus = voice.Engine == TtsEngine.Neural
                    ? $"Couldn't reach the voice server ({ex.Message}). Check internet, or pick an offline voice."
                    : $"Voice failed: {ex.Message}";
                return null;
            }
            finally
            {
                if (_ttsCts == cts) ViewModel.TtsBusy = false;
            }
        }

        private void RememberTts(string text, TtsVoice voice)
        {
            var existing = ViewModel.TtsRecent.IndexOf(text);
            if (existing >= 0) ViewModel.TtsRecent.RemoveAt(existing);
            ViewModel.TtsRecent.Insert(0, text);
            while (ViewModel.TtsRecent.Count > 10) ViewModel.TtsRecent.RemoveAt(ViewModel.TtsRecent.Count - 1);

            _settings.TtsRecent = ViewModel.TtsRecent.ToList();
            _settings.TtsVoiceId = voice.Id;
            _settings.TtsRate = ViewModel.TtsRate;
            _settings.TtsPitchHz = ViewModel.TtsPitchHz;
            SettingsManager.SaveSettings(_settings);
        }

        private static WaveStream OpenTtsReader(TtsAudio audio)
        {
            var stream = new MemoryStream(audio.Data, writable: false);
            return audio.Extension == ".mp3" ? new Mp3FileReader(stream) : new WaveFileReader(stream);
        }

        private void StartTtsPlayback(TtsAudio audio)
        {
            try
            {
                StopAudioProcessing(updateStatus: false);

                bool loop = ViewModel.IsLoopingEnabled;
                // Each output needs its own reader, otherwise both pull from one stream and the speech gets chopped.
                _virtualLoopStream = new LoopStream(OpenTtsReader(audio)) { Loop = loop };
                _speakerLoopStream = new LoopStream(OpenTtsReader(audio)) { Loop = loop };

                var targetFormat = AudioService.MixFormat;
                var soundParams = new SoundItem { Volume = ViewModel.TtsVolume, Pitch = 1.0 };

                var virtualChain = CreateProcessingChain(_virtualLoopStream, targetFormat, soundParams, 1f);
                var speakerChain = CreateProcessingChain(_speakerLoopStream, targetFormat, soundParams, 1f);
                if (virtualChain?.Volume == null || speakerChain?.Volume == null)
                {
                    StopAudioProcessing(updateStatus: false);
                    return;
                }

                _equalizerVirtual = virtualChain.Value.Eq;
                _pitchShifterVirtual = virtualChain.Value.Pitch;
                _volumeProviderVirtual = virtualChain.Value.Volume;
                _equalizerSpeaker = speakerChain.Value.Eq;
                _pitchShifterSpeaker = speakerChain.Value.Pitch;
                _volumeProviderSpeaker = speakerChain.Value.Volume;
                _currentSoundVolume = ViewModel.TtsVolume;
                ApplyVolumes();

                InitializeVirtualOutput();
                InitializeSpeakerOutput();
                if (_audioWaveOutSpeaker == null) return;

                _currentPlaybackState = PlaybackState.Playing;
                ShowPlayerForTts(_lastTtsText, ViewModel.TtsSelectedVoice?.ShortName ?? "voice");
                UpdateStatus($"Speaking: {_lastTtsText.TrimTo(40)}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[TTS] playback: {ex}");
                UpdateStatus($"TTS playback error: {ex.Message}", true);
                StopAudioProcessing(updateStatus: false);
            }
            finally
            {
                UpdatePlayPauseButtonState();
            }
        }

        private void TtsStop_Click(object? sender, RoutedEventArgs e) => StopAudioProcessing();

        /// <summary>Saves the speech as a file and adds it to the current soundboard.</summary>
        private async void SaveTtsAudio_Click(object? sender, RoutedEventArgs e)
        {
            var text = ViewModel.TtsText?.Trim() ?? "";
            var audio = (_lastTtsAudio != null && _lastTtsText == text) ? _lastTtsAudio : await MakeTtsAudioAsync();
            if (audio == null) return;

            try
            {
                var folder = Path.Combine(AppPaths.DataDir, "tts");
                Directory.CreateDirectory(folder);
                var name = AudioExporter.SafeFileName(text.TrimTo(40));
                var path = AudioExporter.UniquePath(folder, name, audio.Extension);
                await File.WriteAllBytesAsync(path, audio.Data);

                var board = ViewModel.SelectedSoundboard ?? ViewModel.Soundboards.FirstOrDefault();
                if (board == null) return;

                var item = new SoundItem
                {
                    Path = path,
                    Name = $"TTS: {text.TrimTo(24)}",
                    Volume = ViewModel.TtsVolume
                };
                item.PropertyChanged += SoundItem_PropertyChanged;
                board.Sounds.Add(item);
                SaveSoundLibrary();
                ViewModel.TtsStatus = $"Saved to \"{board.Name}\". You can give it a hotkey there.";
            }
            catch (Exception ex)
            {
                ViewModel.TtsStatus = $"Couldn't save: {ex.Message}";
            }
        }
    }
}
