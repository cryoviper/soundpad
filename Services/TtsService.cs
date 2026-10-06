using BoomBx.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BoomBx.Services
{
    /// <summary>
    /// Text to speech: Microsoft neural voices (online, natural, Hindi + 100 languages)
    /// with eSpeak as an offline fallback.
    /// </summary>
    public sealed class TtsService
    {
        private ESpeakGenerator? _espeak;
        private bool _espeakTried;
        private readonly object _cacheLock = new();
        private readonly LinkedList<(string Key, TtsAudio Audio)> _cache = new();

        public List<TtsVoice> Voices { get; private set; } = NeuralTtsClient.BuiltInVoices();

        /// <summary>Downloads the full Microsoft voice list and adds offline voices. Safe to call many times.</summary>
        public async Task<List<TtsVoice>> LoadAllVoicesAsync(CancellationToken ct)
        {
            var voices = new List<TtsVoice>();
            try
            {
                voices.AddRange(await NeuralTtsClient.GetVoicesAsync(ct));
            }
            catch (Exception ex)
            {
                Logger.Log($"[TTS] voice list download failed, using built-in list: {ex.Message}");
                voices.AddRange(NeuralTtsClient.BuiltInVoices());
            }

            foreach (var name in await Task.Run(GetESpeakVoices, ct))
                voices.Add(new TtsVoice { Id = name, Locale = "espeak", Engine = TtsEngine.ESpeak });

            Voices = voices;
            return voices;
        }

        public async Task<TtsAudio> SpeakAsync(string text, TtsVoice voice, int ratePercent, int pitchHz, CancellationToken ct)
        {
            text = text.Trim();
            if (text.Length == 0) throw new InvalidOperationException("Type something first.");
            if (text.Length > 2000) text = text[..2000];

            var key = $"{voice.Engine}|{voice.Id}|{ratePercent}|{pitchHz}|{text}";
            lock (_cacheLock)
            {
                var hit = _cache.FirstOrDefault(c => c.Key == key);
                if (hit.Audio != null) return hit.Audio;
            }

            TtsAudio audio;
            if (voice.Engine == TtsEngine.Neural)
            {
                var mp3 = await NeuralTtsClient.SynthesizeAsync(text, voice.Id, ratePercent, pitchHz, ct);
                audio = new TtsAudio(mp3, ".mp3");
            }
            else
            {
                var espeak = await Task.Run(GetESpeak, ct) ?? throw new InvalidOperationException("Offline voices are not available.");
                // eSpeak takes speed/pitch as multipliers around 1.0
                using var wav = await espeak.GenerateSpeechAsync(text, voice.Id,
                    (float)Math.Clamp(1 + ratePercent / 100.0, 0.3, 3.0),
                    (float)Math.Clamp(1 + pitchHz / 100.0, 0.3, 2.0));
                audio = new TtsAudio(wav.ToArray(), ".wav");
            }

            lock (_cacheLock)
            {
                _cache.AddFirst((key, audio));
                while (_cache.Count > 30) _cache.RemoveLast();
            }
            return audio;
        }

        public void Cleanup()
        {
            try { _espeak?.Cleanup(); } catch { /* ignore */ }
        }

        private IEnumerable<string> GetESpeakVoices()
        {
            var espeak = GetESpeak();
            if (espeak == null) return Array.Empty<string>();
            try { return espeak.GetAvailableVoices().ToList(); }
            catch { return Array.Empty<string>(); }
        }

        private ESpeakGenerator? GetESpeak()
        {
            lock (_cacheLock)
            {
                if (_espeakTried) return _espeak;
                _espeakTried = true;
            }
            try
            {
                var e = new ESpeakGenerator();
                e.Initialize();
                _espeak = e;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TTS] eSpeak not available: {ex.Message}");
            }
            return _espeak;
        }
    }

    /// <summary>Spoken audio in memory plus its file type (.mp3 or .wav).</summary>
    public sealed record TtsAudio(byte[] Data, string Extension);
}
