using System;
using System.Globalization;

namespace BoomBx.Models
{
    public enum TtsEngine
    {
        /// <summary>Microsoft neural voices (online, natural sounding, 100+ languages).</summary>
        Neural,
        /// <summary>eSpeak (offline, robotic).</summary>
        ESpeak
    }

    public sealed class TtsVoice
    {
        public string Id { get; init; } = "";
        public string Locale { get; init; } = "";
        public string Gender { get; init; } = "";
        public TtsEngine Engine { get; init; }

        /// <summary>"Swara" from "hi-IN-SwaraNeural".</summary>
        public string ShortName
        {
            get
            {
                if (Engine == TtsEngine.ESpeak) return Id;
                var parts = Id.Split('-');
                var name = parts.Length >= 3 ? string.Join("-", parts, 2, parts.Length - 2) : Id;
                return name.Replace("MultilingualNeural", " (multilingual)").Replace("Neural", "");
            }
        }

        public string Display => string.IsNullOrEmpty(Gender) ? ShortName : $"{ShortName}  ·  {Gender}";

        public string Language => LanguageName(Locale, Engine);

        public static string LanguageName(string locale, TtsEngine engine)
        {
            if (engine == TtsEngine.ESpeak) return "Offline robot voices (eSpeak)";
            try
            {
                return new CultureInfo(locale).EnglishName;
            }
            catch (CultureNotFoundException)
            {
                return locale;
            }
        }

        public override string ToString() => Display;
    }
}
