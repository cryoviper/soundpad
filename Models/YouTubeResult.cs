using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BoomBx.Models
{
    /// <summary>
    /// One row in the YouTube search results.
    /// </summary>
    public partial class YouTubeResult : ObservableObject
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Channel { get; init; } = string.Empty;
        public double DurationSeconds { get; init; }

        public string DurationText => DurationSeconds > 0 ? TimeText.Format(DurationSeconds) : "";
        public string ThumbnailUrl => $"https://i.ytimg.com/vi/{Id}/mqdefault.jpg";
        public string WatchUrl => $"https://www.youtube.com/watch?v={Id}";

        [ObservableProperty]
        private Bitmap? _thumbnail;

        [ObservableProperty]
        private bool _isPlaying;
    }
}
