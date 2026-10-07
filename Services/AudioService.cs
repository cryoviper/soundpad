using Avalonia.Threading;
using BoomBx.Models;
using BoomBx.ViewModels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BoomBx.Services
{
    public class AudioService : IDisposable
    {
        private bool _isRestarting;
        private readonly object _restartLock = new();

        private readonly AppSettings _settings;
        private readonly MainWindowViewModel _viewModel;
        private readonly DeviceManager _deviceManager;

        private WasapiCapture? _micCapture;
        private IWavePlayer? _persistentOutput;
        private MixingSampleProvider? _persistentMixer;
        private VolumeSampleProvider? _micVolume;
        private float _micLevel = 1f;
        private BufferedWaveProvider? _micBuffer;
        private byte[] _micTrash = Array.Empty<byte>();

        /// <summary>
        /// Everything is mixed at 48 kHz stereo - what VB-Cable, Discord and games use -
        /// so there's less resampling (= less noise and crackle).
        /// </summary>
        public static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        /// <summary>Mic cleanup (high-pass + noise gate). Null until devices start.</summary>
        public MicCleanupSampleProvider? MicCleanup { get; private set; }

        private bool _gateEnabled = true;
        private float _gateThresholdDb = -45f;

        // ---------------- voice changer ----------------

        private VoicePreset _voicePreset = VoicePreset.All[0];
        private float _voiceExtraPitch = 1f;
        private bool _voiceEnabled;
        private bool _monitorEnabled;
        private VoiceChangerSampleProvider? _voiceChanger;
        private MonitorTapSampleProvider? _monitorTap;
        private WasapiOut? _monitorOutput;

        /// <summary>Changes your live mic voice. Works right away, no restart.</summary>
        public void SetVoice(VoicePreset preset, float extraPitch, bool enabled)
        {
            _voicePreset = preset;
            _voiceExtraPitch = extraPitch;
            _voiceEnabled = enabled;
            _voiceChanger?.Configure(preset, extraPitch, enabled);
        }

        /// <summary>Hear your own (changed) voice in your headphones.</summary>
        public void SetMonitor(bool enabled)
        {
            _monitorEnabled = enabled;
            if (_monitorTap != null) _monitorTap.Enabled = enabled;
            if (enabled) StartMonitorOutput();
            else StopMonitorOutput();
        }

        private void StartMonitorOutput()
        {
            if (_monitorOutput != null || _monitorTap == null) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var speakers = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _monitorTap.Monitor.ClearBuffer();
                _monitorOutput = new WasapiOut(speakers, AudioClientShareMode.Shared, true, 40);
                _monitorOutput.Init(_monitorTap.Monitor);
                _monitorOutput.Play();
            }
            catch (Exception ex)
            {
                Logger.Log($"Monitor output failed: {ex.Message}");
                StopMonitorOutput();
            }
        }

        private void StopMonitorOutput()
        {
            try
            {
                _monitorOutput?.Stop();
                _monitorOutput?.Dispose();
            }
            catch { /* ignore */ }
            _monitorOutput = null;
        }

        public void SetNoiseGate(bool enabled, float thresholdDb)
        {
            _gateEnabled = enabled;
            _gateThresholdDb = thresholdDb;
            if (MicCleanup != null)
            {
                MicCleanup.GateEnabled = enabled;
                MicCleanup.ThresholdDb = thresholdDb;
            }
        }

        /// <summary>Your real mic level in the virtual mic (0 = muted, 1 = normal, 1.5 = boosted).</summary>
        public void SetMicLevel(float level)
        {
            _micLevel = Math.Clamp(level, 0f, 2f);
            if (_micVolume != null) _micVolume.Volume = _micLevel;
        }
                
        public bool IsUsingVirtualOutput => 
        _deviceManager.SelectedPlaybackDevice?.FriendlyName?.Contains("CABLE Input") == true;

        public AudioService(MainWindowViewModel viewModel, AppSettings settings, DeviceManager deviceManager)
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));
            _viewModel = viewModel;
            _settings = settings;
            _deviceManager = deviceManager;
        }

        public void InitializeDevices()
        {
            StartPersistentAudioRouting();
        }

        public void HandlePlaybackDeviceChanged(MMDevice? device)
        {
            _deviceManager.SetPlaybackDevice(device);
            RestartPersistentAudioRouting();
        }

        private void HandleCaptureError(object? sender, StoppedEventArgs e)
        {
            if (e.Exception == null) return;
            
            Console.WriteLine($"Capture error: {e.Exception.Message}");
            ScheduleAudioRestart();
        }

        private void HandleOutputError(object? sender, StoppedEventArgs e)
        {
            if (e.Exception == null) return;
            
            Console.WriteLine($"Output error: {e.Exception.Message}");
            ScheduleAudioRestart();
        }

        private void ScheduleAudioRestart()
        {
            lock (_restartLock)
            {
                if (_isRestarting) return;
                _isRestarting = true;
            }

            // Restart on UI thread after delay
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await Task.Delay(1000);
                    Console.WriteLine("Attempting audio restart...");
                    RestartPersistentAudioRouting();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Restart failed: {ex.Message}");
                }
                finally
                {
                    lock (_restartLock) _isRestarting = false;
                }
            });
        }


        public void HandleCaptureDeviceChanged(MMDevice? device)
        {
            _deviceManager.SetCaptureDevice(device);
            RestartPersistentAudioRouting();
        }

        public void RestartPersistentAudioRouting()
        {
            StopPersistentAudioRouting();
            StartPersistentAudioRouting();
        }

        private void StartPersistentAudioRouting()
        {
            if (_deviceManager.SelectedPlaybackDevice == null || _deviceManager.SelectedCaptureDevice == null)
                return;

            try
            {
                StopPersistentAudioRouting();

                var targetFormat = MixFormat;
                _persistentMixer = new MixingSampleProvider(targetFormat)
                {
                    ReadFully = true
                };
                _deviceManager.SetPersistentMixer(_persistentMixer);

                _micCapture = new WasapiCapture(_deviceManager.SelectedCaptureDevice);
                _micCapture.RecordingStopped += HandleCaptureError;
                // Small buffer that never grows: keeps your voice in sync (no slowly rising delay,
                // no "buffer full" crash after long sessions when mic and cable clocks drift).
                var capture = _micCapture;
                var micBuffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(1),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                _micBuffer = micBuffer;
                capture.DataAvailable += (_, a) => OnMicData(micBuffer, a);

                var micSampleProvider = ConvertFormat(micBuffer.ToSampleProvider(), targetFormat);
                MicCleanup = new MicCleanupSampleProvider(micSampleProvider)
                {
                    GateEnabled = _gateEnabled,
                    ThresholdDb = _gateThresholdDb
                };
                _voiceChanger = new VoiceChangerSampleProvider(MicCleanup);
                _voiceChanger.Configure(_voicePreset, _voiceExtraPitch, _voiceEnabled);
                _monitorTap = new MonitorTapSampleProvider(_voiceChanger) { Enabled = _monitorEnabled };
                _micVolume = new VolumeSampleProvider(_monitorTap) { Volume = _micLevel };
                _persistentMixer.AddMixerInput(_micVolume);

                _persistentOutput = new WasapiOut(
                    _deviceManager.SelectedPlaybackDevice,
                    AudioClientShareMode.Shared,
                    false,
                    100
                );
                _persistentOutput.PlaybackStopped += HandleOutputError;

                _persistentOutput.Init(new SoftLimiterSampleProvider(_persistentMixer));
                _micCapture.StartRecording();
                _persistentOutput.Play();
                if (_monitorEnabled) StartMonitorOutput();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting persistent routing: {ex}");
            }
        }

        private void OnMicData(BufferedWaveProvider buffer, WaveInEventArgs a)
        {
            try
            {
                OnMicDataCore(buffer, a);
            }
            catch (Exception ex)
            {
                Logger.Log($"Mic data error: {ex.Message}"); // never crash the capture thread
            }
        }

        private void OnMicDataCore(BufferedWaveProvider buffer, WaveInEventArgs a)
        {
            buffer.AddSamples(a.Buffer, 0, a.BytesRecorded);

            // If more than ~150 ms piles up, drop the oldest part so the delay stays low.
            var format = buffer.WaveFormat;
            int maxBytes = format.AverageBytesPerSecond * 150 / 1000;
            int keepBytes = format.AverageBytesPerSecond * 60 / 1000;
            int extra = buffer.BufferedBytes - keepBytes;
            if (buffer.BufferedBytes > maxBytes && extra > 0)
            {
                extra -= extra % format.BlockAlign;
                if (_micTrash.Length < extra) _micTrash = new byte[extra];
                buffer.Read(_micTrash, 0, extra);
            }
        }

        private void StopPersistentAudioRouting()
        {
            StopMonitorOutput();
            if (_micCapture != null)
            {
                _micCapture.RecordingStopped -= HandleCaptureError;
                _micCapture.StopRecording();
                _micCapture.Dispose();
                _micCapture = null;
            }

            if (_persistentOutput != null)
            {
                _persistentOutput.PlaybackStopped -= HandleOutputError;
                _persistentOutput.Stop();
                _persistentOutput.Dispose();
                _persistentOutput = null;
            }

            _persistentMixer?.RemoveAllMixerInputs();
        }


        public static ISampleProvider ConvertFormat(ISampleProvider input, WaveFormat targetFormat)
        {
            if (input.WaveFormat.Channels != targetFormat.Channels)
            {
                if (targetFormat.Channels == 2)
                {
                    input = input.WaveFormat.Channels switch
                    {
                        1 => new MonoToStereoSampleProvider(input),
                        > 2 => new DownmixToStereoSampleProvider(input),
                        _ => input
                    };
                }
                else if (targetFormat.Channels == 1)
                {
                    input = input.WaveFormat.Channels switch
                    {
                        2 => new StereoToMonoSampleProvider(input),
                        > 2 => throw new NotSupportedException(
                            "Downmixing to mono from multi-channel not implemented"),
                        _ => input
                    };
                }
            }

            if (input.WaveFormat.SampleRate != targetFormat.SampleRate)
            {
                input = new WdlResamplingSampleProvider(input, targetFormat.SampleRate);
            }

            // Never hand back a short buffer mid-sound (the mixer would drop the sound).
            return new FullReadSampleProvider(input);
        }

        public void Dispose()
        {
            StopPersistentAudioRouting();
            _persistentOutput?.Dispose();
            _micCapture?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}