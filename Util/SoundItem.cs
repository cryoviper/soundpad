using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BoomBx.Models
{
    public class SoundItem : INotifyPropertyChanged
    {
        public string Path { get; set; } = string.Empty;
        private string _name = string.Empty;
        private double _volume = 100;
        private string _iconPath = BoomBx.AppPaths.DefaultIcon;

        public string IconPath
        {
            get => _iconPath;
            set
            {
                if (_iconPath != value)
                {
                    _iconPath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Name
        {
            get => _name ?? System.IO.Path.GetFileNameWithoutExtension(Path);
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = value;
                OnPropertyChanged();
            }
        }

        private double _bass;
        public double Bass
        {
            get => _bass;
            set
            {
                _bass = Math.Clamp(value, -20, 20);
                OnPropertyChanged();
            }
        }

        private double _treble;
        public double Treble
        {
            get => _treble;
            set
            {
                _treble = Math.Clamp(value, -20, 20);
                OnPropertyChanged();
            }
        }

        private double _pitch = 1.0;
        public double Pitch
        {
            get => _pitch;
            set
            {
                _pitch = Math.Clamp(value, 0.5, 2.0);
                OnPropertyChanged();
            }
        }

        // ---------------- YouTube sounds (streamed live, never downloaded) ----------------

        private string? _youTubeId;
        /// <summary>Set for sounds pinned from the YouTube tab. Null for normal files.</summary>
        public string? YouTubeId
        {
            get => _youTubeId;
            set
            {
                _youTubeId = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsYouTube));
                OnPropertyChanged(nameof(IsLocalFile));
            }
        }

        private double _startSeconds;
        /// <summary>Clip start inside the video.</summary>
        public double StartSeconds
        {
            get => _startSeconds;
            set
            {
                _startSeconds = Math.Max(0, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(StartText));
            }
        }

        private double _endSeconds;
        /// <summary>Clip end inside the video. 0 = play to the end.</summary>
        public double EndSeconds
        {
            get => _endSeconds;
            set
            {
                _endSeconds = Math.Max(0, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(EndText));
            }
        }

        [JsonIgnore]
        public bool IsYouTube => !string.IsNullOrWhiteSpace(_youTubeId);

        [JsonIgnore]
        public bool IsLocalFile => !IsYouTube;

        /// <summary>Start time as "m:ss" for the text box.</summary>
        [JsonIgnore]
        public string StartText
        {
            get => TimeText.Format(StartSeconds);
            set
            {
                if (TimeText.TryParse(value, out var s)) StartSeconds = s;
            }
        }

        /// <summary>End time as "m:ss" for the text box. Empty = full video.</summary>
        [JsonIgnore]
        public string EndText
        {
            get => EndSeconds > 0 ? TimeText.Format(EndSeconds) : "";
            set
            {
                if (TimeText.TryParse(value, out var s)) EndSeconds = s;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
