using BoomBx.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace BoomBx.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        public MainWindowViewModel()
        {
            _appVersion = $"Version {AppVersionHelper.GetInformationalVersion()}";
            TtsRecent.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TtsHasRecent));
        }

        [ObservableProperty]
        private string? _appVersion;

        // ================= Soundboards =================

        public ObservableCollection<Soundboard> Soundboards { get; } = new();

        [ObservableProperty]
        private Soundboard? _selectedSoundboard;

        [ObservableProperty]
        private SoundItem? _selectedSound;

        [ObservableProperty]
        private bool _isLoopingEnabled;

        [ObservableProperty]
        private string? _selectedFilePath;

        /// <summary>Sounds of the current board after the search filter.</summary>
        public ObservableCollection<SoundItem> FilteredSounds { get; } = new();

        [ObservableProperty]
        private string _soundFilter = "";

        [ObservableProperty]
        private bool _boardIsEmpty = true;

        partial void OnSelectedSoundChanged(SoundItem? value)
        {
            SelectedFilePath = value?.Path ?? string.Empty;
        }

        partial void OnSelectedSoundboardChanged(Soundboard? oldValue, Soundboard? newValue)
        {
            if (oldValue != null) oldValue.Sounds.CollectionChanged -= OnBoardSoundsChanged;
            if (newValue != null) newValue.Sounds.CollectionChanged += OnBoardSoundsChanged;
            RefreshFilteredSounds();
        }

        partial void OnSoundFilterChanged(string value) => RefreshFilteredSounds();

        private void OnBoardSoundsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshFilteredSounds();

        public void RefreshFilteredSounds()
        {
            var keep = SelectedSound;
            var filter = SoundFilter?.Trim() ?? "";
            var source = SelectedSoundboard?.Sounds ?? new ObservableCollection<SoundItem>();
            var items = filter.Length == 0
                ? source.ToList()
                : source.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            FilteredSounds.Clear();
            foreach (var s in items) FilteredSounds.Add(s);
            BoardIsEmpty = source.Count == 0;

            // ListBox may clear the selection when items are rebuilt - put it back.
            if (keep != null && FilteredSounds.Contains(keep) && SelectedSound != keep) SelectedSound = keep;
        }

        // ================= Hotkeys =================

        [ObservableProperty]
        private string _playPauseHotkey = "F9";

        [ObservableProperty]
        private string _stopHotkey = "F10";

        [ObservableProperty]
        private string _pauseHotkey = "F11";

        // ================= Devices + mixer =================

        [ObservableProperty]
        private ObservableCollection<MMDevice> _playbackDevices = new();

        [ObservableProperty]
        private ObservableCollection<MMDevice> _captureDevices = new();

        [ObservableProperty]
        private MMDevice? _selectedPlaybackDevice;

        [ObservableProperty]
        private MMDevice? _selectedCaptureDevice;

        /// <summary>How loud sounds are for friends (virtual mic).</summary>
        [ObservableProperty]
        private double _masterVirtualVolume = 100;

        /// <summary>How loud sounds are for you (speakers).</summary>
        [ObservableProperty]
        private double _masterSpeakerVolume = 70;

        [ObservableProperty]
        private double _micVolume = 100;

        [ObservableProperty]
        private bool _micMuted;

        [ObservableProperty]
        private string _downloadFolder = "";

        [ObservableProperty]
        private bool _noiseGateEnabled = true;

        /// <summary>Gate threshold in dB (-80 = very sensitive, -20 = only loud voice).</summary>
        [ObservableProperty]
        private double _noiseGateDb = -45;

        /// <summary>Live mic level for the meter, 0-100 (maps -80..0 dB).</summary>
        [ObservableProperty]
        private double _micMeter;

        /// <summary>Where the gate threshold sits on the meter, 0-100.</summary>
        public double NoiseGateMeterPosition => Math.Clamp((NoiseGateDb + 80) / 80 * 100, 0, 100);

        [ObservableProperty]
        private bool _micGateOpen;

        /// <summary>Raised when any mixer value changes, so the window can apply + save it.</summary>
        public event Action? MixerChanged;

        partial void OnMasterVirtualVolumeChanged(double value) => MixerChanged?.Invoke();
        partial void OnMasterSpeakerVolumeChanged(double value) => MixerChanged?.Invoke();
        partial void OnMicVolumeChanged(double value) => MixerChanged?.Invoke();
        partial void OnMicMutedChanged(bool value) => MixerChanged?.Invoke();
        partial void OnNoiseGateEnabledChanged(bool value) => MixerChanged?.Invoke();
        partial void OnNoiseGateDbChanged(double value)
        {
            OnPropertyChanged(nameof(NoiseGateMeterPosition));
            MixerChanged?.Invoke();
        }

        // ================= Voice changer =================

        public ObservableCollection<VoicePresetCard> VoiceCards { get; } = new();

        [ObservableProperty]
        private VoicePresetCard? _selectedVoiceCard;

        [ObservableProperty]
        private bool _voiceEnabled;

        /// <summary>Extra pitch in semitones on top of the effect (-6..+6).</summary>
        [ObservableProperty]
        private double _voicePitchTune;

        [ObservableProperty]
        private bool _monitorEnabled;

        [ObservableProperty]
        private string _voiceToggleHotkey = "F8";

        /// <summary>Raised when any voice setting changes.</summary>
        public event Action? VoiceChanged;

        partial void OnSelectedVoiceCardChanged(VoicePresetCard? oldValue, VoicePresetCard? newValue)
        {
            if (oldValue != null) oldValue.IsSelected = false;
            if (newValue != null) newValue.IsSelected = true;
            VoiceChanged?.Invoke();
        }

        partial void OnVoiceEnabledChanged(bool value) => VoiceChanged?.Invoke();
        partial void OnVoicePitchTuneChanged(double value) => VoiceChanged?.Invoke();
        partial void OnMonitorEnabledChanged(bool value) => VoiceChanged?.Invoke();

        // ================= Mini player (bottom bar) =================

        [ObservableProperty]
        private bool _playerVisible;

        [ObservableProperty]
        private string _playerTitle = "";

        [ObservableProperty]
        private string _playerSubtitle = "";

        [ObservableProperty]
        private string _playerIconPath = BoomBx.AppPaths.DefaultIcon;

        [ObservableProperty]
        private Avalonia.Media.Imaging.Bitmap? _playerThumb;

        [ObservableProperty]
        private bool _playerIsPlaying;

        [ObservableProperty]
        private string _playerElapsedText = "0:00";

        [ObservableProperty]
        private string _playerTotalText = "0:00";

        [ObservableProperty]
        private bool _playerCanSeek;

        // ================= Text to speech =================

        [ObservableProperty]
        private string _ttsText = "";

        [ObservableProperty]
        private double _ttsVolume = 100;

        /// <summary>Speed change in percent (-50 = half speed, +100 = double).</summary>
        [ObservableProperty]
        private double _ttsRate;

        /// <summary>Pitch change in Hz.</summary>
        [ObservableProperty]
        private double _ttsPitchHz;

        [ObservableProperty]
        private bool _ttsBusy;

        [ObservableProperty]
        private string _ttsStatus = "";

        public ObservableCollection<string> TtsLanguages { get; } = new();
        public ObservableCollection<TtsVoice> TtsVoices { get; } = new();
        public ObservableCollection<string> TtsRecent { get; } = new();
        public bool TtsHasRecent => TtsRecent.Count > 0;

        [ObservableProperty]
        private string? _ttsSelectedLanguage;

        [ObservableProperty]
        private TtsVoice? _ttsSelectedVoice;

        private List<TtsVoice> _allTtsVoices = new();

        /// <summary>Fills the language + voice pickers. Keeps the chosen voice when possible.</summary>
        public void SetTtsVoices(List<TtsVoice> voices, string? preferredVoiceId)
        {
            _allTtsVoices = voices;

            // Hindi + Indian English first, then the rest A-Z, offline voices last.
            var first = new[] { "Hindi (India)", "English (India)", "English (United States)", "English (United Kingdom)" };
            var languages = voices.Select(v => v.Language).Distinct()
                .OrderBy(l => Rank(first, l))
                .ThenBy(l => l.StartsWith("Offline", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var wanted = voices.FirstOrDefault(v => v.Id == preferredVoiceId) ?? TtsSelectedVoice;

            TtsLanguages.Clear();
            foreach (var l in languages) TtsLanguages.Add(l);

            TtsSelectedLanguage = wanted?.Language ?? languages.FirstOrDefault();
            RefreshTtsVoiceList(wanted?.Id);
        }

        private static int Rank(string[] order, string value)
        {
            int i = Array.IndexOf(order, value);
            return i >= 0 ? i : 100;
        }

        partial void OnTtsSelectedLanguageChanged(string? value) => RefreshTtsVoiceList(TtsSelectedVoice?.Id);

        private void RefreshTtsVoiceList(string? keepId)
        {
            var list = _allTtsVoices.Where(v => v.Language == TtsSelectedLanguage)
                                    .OrderBy(v => v.ShortName, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
            TtsVoices.Clear();
            foreach (var v in list) TtsVoices.Add(v);
            TtsSelectedVoice = list.FirstOrDefault(v => v.Id == keepId) ?? list.FirstOrDefault();
        }

        // ================= YouTube tab =================

        [ObservableProperty]
        private string _ytQuery = "";

        [ObservableProperty]
        private bool _ytIsSearching;

        [ObservableProperty]
        private string _ytStatus = "";

        public ObservableCollection<YouTubeResult> YtResults { get; } = new();
        public ObservableCollection<YouTubeResult> YtRecent { get; } = new();

        [ObservableProperty]
        private bool _ytHasResults;

        [ObservableProperty]
        private bool _ytHasRecent;

        /// <summary>The video playing from the YouTube tab right now (null = nothing).</summary>
        [ObservableProperty]
        private YouTubeResult? _ytNowPlaying;

        [ObservableProperty]
        private string _ytNowPlayingTitle = "";

        [ObservableProperty]
        private string _ytElapsedText = "0:00";

        [ObservableProperty]
        private string _ytTotalText = "0:00";

        /// <summary>True while the clip is loading (before sound starts).</summary>
        [ObservableProperty]
        private bool _ytIsLoading;

        /// <summary>True = show the pause icon, false = show play.</summary>
        [ObservableProperty]
        private bool _ytIsPlaying;

        [ObservableProperty]
        private double _ytVolume = 80;

        /// <summary>Clip start, e.g. "0:12" (empty = beginning).</summary>
        [ObservableProperty]
        private string _ytStartText = "";

        /// <summary>Clip end, e.g. "0:20" (empty = till the end).</summary>
        [ObservableProperty]
        private string _ytEndText = "";

        [ObservableProperty]
        private bool _ytIsSaving;

        /// <summary>Sound settings used for YouTube-tab playback (volume etc.).</summary>
        public SoundItem? YtCurrentSound { get; set; }

        partial void OnYtVolumeChanged(double value)
        {
            if (YtCurrentSound != null) YtCurrentSound.Volume = value;
        }
    }
}
