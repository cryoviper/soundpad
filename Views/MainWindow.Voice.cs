using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BoomBx.Models;
using BoomBx.Services;
using System;
using System.Linq;

namespace BoomBx.Views
{
    /// <summary>Voice Changer tab: live effects on your mic (deep, robot, radio, echo...).</summary>
    public partial class MainWindow : Window
    {
        private static readonly string[] VoiceColors =
        {
            "#6E6E7C", "#7B5CFF", "#B83A3A", "#F5A524", "#36C2B4", "#5B8DEF",
            "#3DDC84", "#C98A4B", "#FF7A45", "#4A6CF7", "#E0569B", "#8E2DE2"
        };

        private DispatcherTimer? _voiceSaveTimer;

        private void InitializeVoiceChanger()
        {
            for (int i = 0; i < VoicePreset.All.Length; i++)
                ViewModel.VoiceCards.Add(new VoicePresetCard(VoicePreset.All[i], VoiceColors[i % VoiceColors.Length]));

            ViewModel.SelectedVoiceCard = ViewModel.VoiceCards.FirstOrDefault(c => c.Preset.Id == _settings.VoicePresetId)
                                          ?? ViewModel.VoiceCards.First();
            ViewModel.VoicePitchTune = _settings.VoicePitchTune;
            ViewModel.VoiceEnabled = _settings.VoiceEnabled;
            ViewModel.MonitorEnabled = _settings.MonitorEnabled;
            ApplyVoice();

            _voiceSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _voiceSaveTimer.Tick += (_, _) =>
            {
                _voiceSaveTimer.Stop();
                SettingsManager.SaveSettings(_settings);
            };

            ViewModel.VoiceChanged += () =>
            {
                // Picking an effect turns the changer on.
                if (!ViewModel.VoiceEnabled && ViewModel.SelectedVoiceCard is { } card && card.Preset.Id != "normal"
                    && _voiceUserPicked)
                {
                    ViewModel.VoiceEnabled = true; // raises VoiceChanged again
                    return;
                }

                ApplyVoice();
                _settings.VoiceEnabled = ViewModel.VoiceEnabled;
                _settings.VoicePresetId = ViewModel.SelectedVoiceCard?.Preset.Id ?? "normal";
                _settings.VoicePitchTune = ViewModel.VoicePitchTune;
                _settings.MonitorEnabled = ViewModel.MonitorEnabled;
                _voiceSaveTimer.Stop();
                _voiceSaveTimer.Start();
            };

            // Only auto-enable when the user clicks a card (not while loading settings).
            VoiceCardsList.AddHandler(PointerPressedEvent, (_, _) => _voiceUserPicked = true, RoutingStrategies.Tunnel);
        }

        private bool _voiceUserPicked;

        private void ApplyVoice()
        {
            var preset = ViewModel.SelectedVoiceCard?.Preset ?? VoicePreset.All[0];
            float extra = (float)Math.Pow(2, ViewModel.VoicePitchTune / 12.0);
            _audioService?.SetVoice(preset, extra, ViewModel.VoiceEnabled);
            _audioService?.SetMonitor(ViewModel.MonitorEnabled);
        }

        private void VoiceResetPitch_Click(object? sender, RoutedEventArgs e) => ViewModel.VoicePitchTune = 0;

        /// <summary>Global hotkey action: voice changer on/off.</summary>
        private void ToggleVoiceChanger()
        {
            ViewModel.VoiceEnabled = !ViewModel.VoiceEnabled;
            UpdateStatus(ViewModel.VoiceEnabled
                ? $"Voice changer ON ({ViewModel.SelectedVoiceCard?.Name})"
                : "Voice changer OFF");
        }
    }
}
