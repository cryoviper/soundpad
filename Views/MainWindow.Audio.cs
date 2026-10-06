using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BoomBx.Models;
using BoomBx.Services;
using BoomBx.ViewModels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.ComponentModel;
using System.IO;

namespace BoomBx.Views
{
    public partial class MainWindow : Window
    {
        private AudioService? _audioService;

        private IWavePlayer? _audioWaveOutSpeaker;
        private VolumeSampleProvider? _volumeProviderVirtual;
        private VolumeSampleProvider? _volumeProviderSpeaker;
        private SoundItem? _currentSoundSubscription;
        private LoopStream? _virtualLoopStream;
        private LoopStream? _speakerLoopStream;
        private EqualizerSampleProvider? _equalizerVirtual;
        private EqualizerSampleProvider? _equalizerSpeaker;
        private PitchShifter? _pitchShifterVirtual;
        private PitchShifter? _pitchShifterSpeaker;

        // YouTube live streaming
        private YouTubeAudioSource? _ytSource;
        private SoundItem? _playingSound;          // what is playing right now
        private SoundItem? _extraSoundSubscription; // a sound we listen to that isn't SelectedSound
        private double _currentSoundVolume = 100;   // per-sound volume of what's playing (0-100)

        /// <summary>
        /// Final volume = sound volume x master volume.
        /// Friends (virtual mic) and you (speakers) each have their own master.
        /// </summary>
        private void ApplyVolumes()
        {
            float sound = (float)(_currentSoundVolume / 100.0);
            if (_volumeProviderVirtual != null)
                _volumeProviderVirtual.Volume = sound * (float)(ViewModel.MasterVirtualVolume / 100.0);
            if (_volumeProviderSpeaker != null)
                _volumeProviderSpeaker.Volume = sound * (float)(ViewModel.MasterSpeakerVolume / 100.0);
        }


        private enum PlaybackState { Stopped, Playing, Paused }
        private PlaybackState _currentPlaybackState = PlaybackState.Stopped;

        private void InitializeAudioService()
        {
            try
            {
                if (DataContext == null) 
                    throw new InvalidOperationException("DataContext is not initialized");
                
                _audioService = new AudioService(
                    (MainWindowViewModel)DataContext,
                    _settings,
                    _deviceManager
                );
            }
            catch (Exception ex)
            {
                UpdateStatus($"Audio service init failed: {ex.Message}");
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (ViewModel == null) return;

            if (e.PropertyName == nameof(MainWindowViewModel.SelectedSound))
            {
                if (ViewModel.SelectedSound == null) return;

                if (_currentPlaybackState != PlaybackState.Stopped)
                {
                    StopAudioProcessing();
                }

                if (_currentSoundSubscription != null)
                {
                    _currentSoundSubscription.PropertyChanged -= SoundItem_VolumeChanged;
                }

                _currentSoundSubscription = ViewModel.SelectedSound;

                if (_currentSoundSubscription != null)
                {
                    _currentSoundSubscription.PropertyChanged += SoundItem_VolumeChanged;
                }
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsLoopingEnabled))
            {
                if (_virtualLoopStream != null)
                    _virtualLoopStream.Loop = ViewModel.IsLoopingEnabled;
                if (_speakerLoopStream != null)
                    _speakerLoopStream.Loop = ViewModel.IsLoopingEnabled;
                if (_ytSource != null)
                    _ytSource.Loop = ViewModel.IsLoopingEnabled;
            }
        }

        private void SoundItem_VolumeChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not SoundItem soundItem ||
                _currentPlaybackState != PlaybackState.Playing ||
                _volumeProviderVirtual == null ||
                _volumeProviderSpeaker == null)
            {
                return;
            }

            // Only the sound that's actually playing may change the live audio.
            if (_playingSound != null && !ReferenceEquals(soundItem, _playingSound)) return;

            try
            {
                switch (e.PropertyName)
                {
                    case nameof(SoundItem.Volume):
                        _currentSoundVolume = soundItem.Volume;
                        ApplyVolumes();
                        break;

                    case nameof(SoundItem.Bass):
                    case nameof(SoundItem.Treble):
                        _equalizerVirtual?.UpdateFilters(soundItem);
                        _equalizerSpeaker?.UpdateFilters(soundItem);
                        break;

                    case nameof(SoundItem.Pitch):
                        _pitchShifterVirtual?.SetPitch((float)soundItem.Pitch);
                        _pitchShifterSpeaker?.SetPitch((float)soundItem.Pitch);
                        break;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Update failed: {ex.Message}");
            }
        }

        private void PlayPauseHandler(object? sender, RoutedEventArgs e)
        {
            switch (_currentPlaybackState)
            {
                case PlaybackState.Stopped:
                    StartAudioProcessing();
                    break;
                case PlaybackState.Playing:
                    PauseAudioProcessing();
                    break;
                case PlaybackState.Paused:
                    ResumeAudioProcessing();
                    break;
            }
        }

        private void StopHandler(object? sender, RoutedEventArgs e)
        {
            StopAudioProcessing();
        }


        private void InitializeSpeakerOutput()
        {
            try
            {
                if (_volumeProviderSpeaker == null) return;

                using var enumerator = new MMDeviceEnumerator();
                var defaultSpeaker = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    ?? throw new InvalidOperationException("No output device found");

                _audioWaveOutSpeaker = new WasapiOut(defaultSpeaker, AudioClientShareMode.Shared, true, 100);
                _audioWaveOutSpeaker.PlaybackStopped += HandlePlaybackStopped;
                _audioWaveOutSpeaker.Init(_volumeProviderSpeaker);
                _audioWaveOutSpeaker.Play();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Speaker error: {ex.Message}");
                StopAudioProcessing(updateStatus: false);
            }
        }

        /// <summary>
        /// Plays a sound through VB-Cable + speakers.
        /// Pass a sound to play something that isn't selected (e.g. a YouTube search result).
        /// </summary>
        private void StartAudioProcessing(SoundItem? soundOverride = null)
        {
            try
            {
                StopAudioProcessing(updateStatus: false);

                var sound = soundOverride ?? ViewModel.SelectedSound;
                if (sound == null)
                {
                    UpdateStatus("No sound selected");
                    return;
                }

                var targetFormat = AudioService.MixFormat;
                float initialVolume = (float)(sound.Volume / 100.0);

                ISampleProvider virtualSource;
                ISampleProvider speakerSource;

                if (sound.IsYouTube)
                {
                    var source = new YouTubeAudioSource(
                        _youTubeService, sound.YouTubeId!, sound.StartSeconds, sound.EndSeconds,
                        targetFormat, ViewModel.IsLoopingEnabled);

                    source.Failed += message => Dispatcher.UIThread.Post(() =>
                    {
                        if (_ytSource != source) return;
                        StopAudioProcessing(updateStatus: false);
                        UpdateStatus($"{message}", true);
                        ViewModel.YtStatus = $"{message}";
                    });

                    _ytSource = source;
                    virtualSource = source.VirtualOutput;
                    speakerSource = source.SpeakerOutput;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(sound.Path) || !File.Exists(sound.Path))
                    {
                        UpdateStatus("Invalid audio file path");
                        return;
                    }

                    var tempVirtualStream = CreateAudioStream(sound.Path);
                    var tempSpeakerStream = CreateAudioStream(sound.Path);

                    if (tempVirtualStream == null || tempSpeakerStream == null)
                    {
                        UpdateStatus("Failed to initialize audio streams");
                        tempVirtualStream?.Dispose();
                        tempSpeakerStream?.Dispose();
                        return;
                    }

                    if (tempVirtualStream.WaveFormat == null || tempSpeakerStream.WaveFormat == null)
                    {
                        UpdateStatus("Audio stream has invalid format");
                        tempVirtualStream.Dispose();
                        tempSpeakerStream.Dispose();
                        return;
                    }

                    _virtualLoopStream = tempVirtualStream;
                    _speakerLoopStream = tempSpeakerStream;
                    _virtualLoopStream.Loop = ViewModel.IsLoopingEnabled;
                    _speakerLoopStream.Loop = ViewModel.IsLoopingEnabled;

                    virtualSource = _virtualLoopStream.ToSampleProvider();
                    speakerSource = _speakerLoopStream.ToSampleProvider();
                }

                var virtualChain = CreateProcessingChain(virtualSource, targetFormat, sound, initialVolume);
                var speakerChain = CreateProcessingChain(speakerSource, targetFormat, sound, initialVolume);

                if (virtualChain?.Volume == null || speakerChain?.Volume == null)
                {
                    UpdateStatus("Failed to create processing chain");
                    StopAudioProcessing(updateStatus: false);
                    return;
                }

                _equalizerVirtual = virtualChain.Value.Eq;
                _pitchShifterVirtual = virtualChain.Value.Pitch;
                _volumeProviderVirtual = virtualChain.Value.Volume;

                _equalizerSpeaker = speakerChain.Value.Eq;
                _pitchShifterSpeaker = speakerChain.Value.Pitch;
                _volumeProviderSpeaker = speakerChain.Value.Volume;
                _currentSoundVolume = sound.Volume;
                ApplyVolumes();

                // Live volume/EQ/pitch updates for sounds that aren't the selected one.
                if (sound != ViewModel.SelectedSound)
                {
                    _extraSoundSubscription = sound;
                    sound.PropertyChanged += SoundItem_VolumeChanged;
                }
                _playingSound = sound;

                InitializeVirtualOutput();
                InitializeSpeakerOutput();
                if (_audioWaveOutSpeaker == null) return; // speaker init failed and already stopped

                _ytSource?.Start();

                _currentPlaybackState = PlaybackState.Playing;
                ShowPlayerForSound(sound);
                UpdateStatus(sound.IsYouTube ? $"Loading {sound.Name}..." : $"Playing {sound.Name}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error: {ex.Message}");
                StopAudioProcessing(updateStatus: false);
            }
            finally
            {
                UpdatePlayPauseButtonState();
            }
        }

        // Kept for the TTS code path, which passes a LoopStream.
        private (EqualizerSampleProvider Eq, PitchShifter Pitch, VolumeSampleProvider Volume)?
            CreateProcessingChain(LoopStream stream, WaveFormat format, SoundItem sound, float volume)
        {
            if (stream?.WaveFormat == null)
            {
                UpdateStatus("Processing chain error: stream has no WaveFormat");
                return null;
            }
            return CreateProcessingChain(stream.ToSampleProvider(), format, sound, volume);
        }

        private (EqualizerSampleProvider Eq, PitchShifter Pitch, VolumeSampleProvider Volume)?
            CreateProcessingChain(ISampleProvider sampleProvider, WaveFormat format, SoundItem sound, float volume)
        {
            try
            {
                if (sampleProvider == null) throw new ArgumentNullException(nameof(sampleProvider));
                if (sound == null) throw new ArgumentNullException(nameof(sound));
                if (sampleProvider.WaveFormat == null) throw new InvalidOperationException("Sample provider has no WaveFormat");

                var provider = AudioService.ConvertFormat(sampleProvider, format);
                if (provider == null) throw new InvalidOperationException("Format conversion returned null");
                if (provider.WaveFormat == null) throw new InvalidOperationException("Converted provider has no WaveFormat");

                var eq = new EqualizerSampleProvider(provider, sound);
                var pitch = new PitchShifter(eq);
                pitch.SetPitch((float)sound.Pitch);
                var vol = new VolumeSampleProvider(pitch) { Volume = volume };
                return (eq, pitch, vol);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Processing chain error: {ex.Message} (Type: {ex.GetType().Name})");
                return null;
            }
        }

        private void HandlePlaybackStopped(object? sender, StoppedEventArgs args)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_currentPlaybackState != PlaybackState.Playing) return;

                StopAudioProcessing(updateStatus: false);
                UpdatePlayPauseButtonState();

                if (args.Exception != null)
                {
                    Logger.Log($"Speaker output stopped with error: {args.Exception}");
                    UpdateStatus($"Speaker output error: {args.Exception.Message}", true);
                }
                else
                {
                    UpdateStatus("Playback completed");
                }
            });
        }

        private void PauseAudioProcessing()
        {
            if (_currentPlaybackState != PlaybackState.Playing) return;

            _audioWaveOutSpeaker?.Pause();
            if (_ytSource != null) _ytSource.Paused = true;
            if (_volumeProviderVirtual != null)
            {
                _deviceManager.RemoveMixerInput(_volumeProviderVirtual);
            }
            _currentPlaybackState = PlaybackState.Paused;
            UpdatePlayPauseButtonState();
        }

        private void ResumeAudioProcessing()
        {
            if (_currentPlaybackState != PlaybackState.Paused) return;

            if (_volumeProviderVirtual != null)
            {
                _deviceManager.AddMixerInput(_volumeProviderVirtual);
            }
            if (_ytSource != null) _ytSource.Paused = false;
            _audioWaveOutSpeaker?.Play();
            _currentPlaybackState = PlaybackState.Playing;
            UpdatePlayPauseButtonState();
        }

        private void StopAudioProcessing(bool updateStatus = true)
        {
            try
            {
                _currentPlaybackState = PlaybackState.Stopped;

                if (_audioWaveOutSpeaker != null)
                {
                    _audioWaveOutSpeaker.PlaybackStopped -= HandlePlaybackStopped;
                    _audioWaveOutSpeaker.Stop();
                    _audioWaveOutSpeaker.Dispose();
                    _audioWaveOutSpeaker = null;
                }

                if (_volumeProviderVirtual != null)
                {
                    _deviceManager.RemoveMixerInput(_volumeProviderVirtual);
                }

                _virtualLoopStream?.Dispose();
                _speakerLoopStream?.Dispose();

                _ytSource?.Dispose();
                _ytSource = null;

                if (_extraSoundSubscription != null)
                {
                    _extraSoundSubscription.PropertyChanged -= SoundItem_VolumeChanged;
                    _extraSoundSubscription = null;
                }
                _playingSound = null;
                OnPlaybackEndedForYouTubeTab();

                _equalizerVirtual = null;
                _equalizerSpeaker = null;
                _pitchShifterVirtual = null;
                _pitchShifterSpeaker = null;
                _volumeProviderVirtual = null;
                _volumeProviderSpeaker = null;
                _virtualLoopStream = null;
                _speakerLoopStream = null;

                if (updateStatus) UpdateStatus("Playback stopped");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Stop error: {ex.Message}");
            }
            finally
            {
                UpdatePlayPauseButtonState();
            }
        }

        private void UpdatePlayPauseButtonState()
        {
            Dispatcher.UIThread.Post(() =>
            {
                PlayPauseButtonText.Text = _currentPlaybackState switch
                {
                    PlaybackState.Playing => "Pause",
                    PlaybackState.Paused => "Resume",
                    _ => "Play"
                };
                PlayIcon.IsVisible = _currentPlaybackState != PlaybackState.Playing;
                PauseIcon.IsVisible = _currentPlaybackState == PlaybackState.Playing;

                StopButton.IsEnabled = _currentPlaybackState != PlaybackState.Stopped;

                ViewModel.YtIsPlaying = _currentPlaybackState == PlaybackState.Playing;
                ViewModel.PlayerIsPlaying = _currentPlaybackState == PlaybackState.Playing;
            });
        }

        private LoopStream? CreateAudioStream(string filePath)
        {
            try
            {
                // AudioFileReader handles mp3/wav/aiff itself and m4a/aac/flac/wma through Windows.
                WaveStream reader = new AudioFileReader(filePath);
                if (reader.WaveFormat == null ||
                    reader.WaveFormat.Channels <= 0 ||
                    reader.WaveFormat.SampleRate <= 0)
                {
                    throw new InvalidOperationException("Audio file has an invalid format");
                }
                return new LoopStream(reader);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading audio: {ex.Message}");
                return null;
            }
        }
    }
}