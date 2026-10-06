using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BoomBx.Models
{
    /// <summary>A voice effect tile on the Voice Changer tab.</summary>
    public partial class VoicePresetCard : ObservableObject
    {
        public VoicePresetCard(VoicePreset preset, string color)
        {
            Preset = preset;
            Color = SolidColorBrush.Parse(color);
        }

        public VoicePreset Preset { get; }
        public string Name => Preset.Name;
        public string Description => Preset.Description;
        public string Letter => Preset.Name.Substring(0, 1);
        /// <summary>Badge color.</summary>
        public IBrush Color { get; }

        [ObservableProperty]
        private bool _isSelected;
    }
}
