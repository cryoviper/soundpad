using BoomBx.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
using System.Collections.ObjectModel;
using System.Speech.Synthesis;
using VoiceInfo = BoomBx.Models.VoiceInfo;

namespace BoomBx.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {

        public MainWindowViewModel()
        {
            _appVersion = $"Version {AppVersionHelper.GetInformationalVersion()}";
            PlaybackDevices = new ObservableCollection<MMDevice>();
            CaptureDevices = new ObservableCollection<MMDevice>();
        }

        [ObservableProperty]
        private string? _appVersion;

        [ObservableProperty]
        private Soundboard? _selectedSoundboard;

        [ObservableProperty]
        private SoundItem? _selectedSound;

        [ObservableProperty]
        private bool _isLoopingEnabled;

        [ObservableProperty]
        private string? _selectedFilePath;

        [ObservableProperty]
        private string _playPauseHotkey = "F9";

        [ObservableProperty]
        private string _stopHotkey = "F10";
        
        [ObservableProperty]
        private string _pauseHotkey = "F11";

        public ObservableCollection<Soundboard> Soundboards { get; } = new();

        partial void OnSelectedSoundChanged(SoundItem? value)
        {
            SelectedFilePath = value?.Path ?? string.Empty;
        }

        [ObservableProperty]
        private string _ttsText = "";

        [ObservableProperty]
        private double _ttsVolume = 100;

        [ObservableProperty]
        private double _ttsPitch = 1.0;

        [ObservableProperty]
        private double _ttsSpeed = 0.5;

        [ObservableProperty]
        private ObservableCollection<VoiceInfo> _availableVoices = [];

        [ObservableProperty]
        private VoiceInfo? _selectedVoice;
        
        [ObservableProperty]
        private ObservableCollection<MMDevice> _playbackDevices = new();

        [ObservableProperty]
        private ObservableCollection<MMDevice> _captureDevices = new();

        [ObservableProperty]
        private MMDevice? _selectedPlaybackDevice;

        [ObservableProperty]
        private MMDevice? _selectedCaptureDevice;

        // ---------------- YouTube tab ----------------

        [ObservableProperty]
        private string _ytQuery = "";

        [ObservableProperty]
        private bool _ytIsSearching;

        [ObservableProperty]
        private string _ytStatus = "";

        public ObservableCollection<YouTubeResult> YtResults { get; } = new();

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

        [ObservableProperty]
        private bool _ytHasResults;

        [ObservableProperty]
        private double _ytProgress;

        /// <summary>True = show the pause icon, false = show play.</summary>
        [ObservableProperty]
        private bool _ytIsPlaying;

        [ObservableProperty]
        private double _ytVolume = 80;

        /// <summary>Optional start time typed by the user, e.g. "0:12".</summary>
        [ObservableProperty]
        private string _ytStartText = "";

        /// <summary>Sound settings used for YouTube-tab playback (volume etc.).</summary>
        public SoundItem? YtCurrentSound { get; set; }

        partial void OnYtVolumeChanged(double value)
        {
            if (YtCurrentSound != null) YtCurrentSound.Volume = value;
        }
    }
}