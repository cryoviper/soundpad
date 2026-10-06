using System;
using System.Collections.Generic;
using System.IO;

public class AppSettings
{
    public string? LastPlaybackDevice { get; set; }
    public string? LastCaptureDevice { get; set; }

    public string PlayPauseHotkey { get; set; } = "F9";
    public string StopHotkey { get; set; } = "F10";
    public string PauseHotkey { get; set; } = "F11";

    // Mixer
    /// <summary>How loud sounds are for your friends (virtual mic), 0-100.</summary>
    public double MasterVirtualVolume { get; set; } = 100;
    /// <summary>How loud sounds are for you (speakers/headphones), 0-100.</summary>
    public double MasterSpeakerVolume { get; set; } = 70;
    /// <summary>Your real mic level going into the virtual mic, 0-150.</summary>
    public double MicVolume { get; set; } = 100;
    public bool MicMuted { get; set; }
    /// <summary>Noise gate: mutes the mic between words so games don't pick up hiss.</summary>
    public bool NoiseGateEnabled { get; set; } = true;
    public double NoiseGateDb { get; set; } = -45;

    // Voice changer
    public bool VoiceEnabled { get; set; }
    public string VoicePresetId { get; set; } = "normal";
    /// <summary>Fine pitch tune in semitones (-6..+6).</summary>
    public double VoicePitchTune { get; set; }
    public bool MonitorEnabled { get; set; }
    public string VoiceToggleHotkey { get; set; } = "F8";

    // YouTube
    public string DownloadFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Boobies Soundpad");
    public List<RecentClip> YouTubeRecent { get; set; } = new();

    // Text to speech
    public string TtsVoiceId { get; set; } = "hi-IN-SwaraNeural";
    public double TtsRate { get; set; }
    public double TtsPitchHz { get; set; }
    public List<string> TtsRecent { get; set; } = new();
}

public class RecentClip
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public double DurationSeconds { get; set; }
}
