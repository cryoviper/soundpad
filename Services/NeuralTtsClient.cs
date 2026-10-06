using BoomBx.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BoomBx.Services
{
    /// <summary>
    /// Microsoft neural voices - the same free "Read aloud" voices Microsoft Edge uses.
    /// Natural sounding, supports Hindi and 100+ other languages. Needs internet.
    /// Protocol follows the open-source edge-tts project (github.com/rany2/edge-tts).
    /// </summary>
    public static class NeuralTtsClient
    {
        private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        private const string BaseUrl = "speech.platform.bing.com/consumer/speech/synthesize/readaloud";
        private const string ChromiumFullVersion = "143.0.3650.75";
        private const string ChromiumMajor = "143";
        private const string SecMsGecVersion = "1-" + ChromiumFullVersion;
        private const long WinEpochSeconds = 11644473600;

        private static readonly string UserAgent =
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            $"Chrome/{ChromiumMajor}.0.0.0 Safari/537.36 Edg/{ChromiumMajor}.0.0.0";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        /// <summary>Corrects the token if this PC's clock is off.</summary>
        private static double _clockSkewSeconds;

        // ------------------------------------------------------------------ synthesis

        /// <summary>Returns MP3 audio (24 kHz mono) for the text.</summary>
        public static async Task<byte[]> SynthesizeAsync(string text, string voice, int ratePercent, int pitchHz,
                                                         CancellationToken ct)
        {
            try
            {
                return await SynthesizeOnceAsync(text, voice, ratePercent, pitchHz, ct);
            }
            catch (ClockSkewException)
            {
                // Clock was off - token fixed, try once more.
                return await SynthesizeOnceAsync(text, voice, ratePercent, pitchHz, ct);
            }
        }

        private static async Task<byte[]> SynthesizeOnceAsync(string text, string voice, int ratePercent, int pitchHz,
                                                              CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var token = timeout.Token;

            using var ws = new ClientWebSocket();
            ws.Options.CollectHttpResponseDetails = true;
            ws.Options.SetRequestHeader("Pragma", "no-cache");
            ws.Options.SetRequestHeader("Cache-Control", "no-cache");
            ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
            ws.Options.SetRequestHeader("User-Agent", UserAgent);
            ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
            ws.Options.SetRequestHeader("Cookie", $"muid={NewId()};");

            var url = $"wss://{BaseUrl}/edge/v1?TrustedClientToken={TrustedClientToken}" +
                      $"&ConnectionId={NewRequestId()}&Sec-MS-GEC={SecMsGec()}&Sec-MS-GEC-Version={SecMsGecVersion}";

            try
            {
                await ws.ConnectAsync(new Uri(url), token);
            }
            catch (WebSocketException) when (ws.HttpStatusCode == System.Net.HttpStatusCode.Forbidden &&
                                             TryFixClock(ws.HttpResponseHeaders))
            {
                throw new ClockSkewException();
            }

            // 1) audio format
            await SendTextAsync(ws,
                $"X-Timestamp:{JsDate()}\r\n" +
                "Content-Type:application/json; charset=utf-8\r\n" +
                "Path:speech.config\r\n\r\n" +
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{" +
                "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
                "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n", token);

            // 2) the text
            var ssml =
                "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                $"<voice name='{voice}'>" +
                $"<prosody pitch='{Signed(pitchHz)}Hz' rate='{Signed(ratePercent)}%' volume='+0%'>" +
                $"{SecurityElement.Escape(CleanText(text))}" +
                "</prosody></voice></speak>";

            await SendTextAsync(ws,
                $"X-RequestId:{NewRequestId()}\r\n" +
                "Content-Type:application/ssml+xml\r\n" +
                $"X-Timestamp:{JsDate()}Z\r\n" +
                "Path:ssml\r\n\r\n" + ssml, token);

            // 3) collect audio until "turn.end"
            using var audio = new MemoryStream();
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();

            while (true)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new InvalidOperationException("Voice server closed the connection. Try another voice.");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var data = message.GetBuffer();
                int length = (int)message.Length;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var textMsg = Encoding.UTF8.GetString(data, 0, length);
                    if (textMsg.Contains("Path:turn.end", StringComparison.Ordinal)) break;
                }
                else if (length >= 2)
                {
                    // [2 bytes header length][headers][audio]
                    int headerLength = (data[0] << 8) | data[1];
                    if (2 + headerLength > length) continue;
                    var headers = Encoding.UTF8.GetString(data, 2, headerLength);
                    if (!headers.Contains("Path:audio", StringComparison.Ordinal)) continue;
                    int audioStart = 2 + headerLength;
                    if (length > audioStart) audio.Write(data, audioStart, length - audioStart);
                }
            }

            try
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { /* not important */ }

            if (audio.Length == 0)
                throw new InvalidOperationException("No audio came back. Try another voice or shorter text.");

            return audio.ToArray();
        }

        // ------------------------------------------------------------------ voice list

        /// <summary>Full voice list from Microsoft (400+ voices). Falls back to the built-in list on error.</summary>
        public static async Task<List<TtsVoice>> GetVoicesAsync(CancellationToken ct)
        {
            var url = $"https://{BaseUrl}/voices/list?trustedclienttoken={TrustedClientToken}" +
                      $"&Sec-MS-GEC={SecMsGec()}&Sec-MS-GEC-Version={SecMsGecVersion}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Cookie", $"muid={NewId()};");

            using var response = await Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            var voices = new List<TtsVoice>();
            using var doc = JsonDocument.Parse(json);
            foreach (var v in doc.RootElement.EnumerateArray())
            {
                var shortName = v.TryGetProperty("ShortName", out var sn) ? sn.GetString() : null;
                var locale = v.TryGetProperty("Locale", out var lo) ? lo.GetString() : null;
                if (string.IsNullOrEmpty(shortName) || string.IsNullOrEmpty(locale)) continue;
                voices.Add(new TtsVoice
                {
                    Id = shortName,
                    Locale = locale,
                    Gender = v.TryGetProperty("Gender", out var g) ? g.GetString() ?? "" : "",
                    Engine = TtsEngine.Neural
                });
            }
            return voices;
        }

        /// <summary>Popular voices that work without fetching the list first.</summary>
        public static List<TtsVoice> BuiltInVoices()
        {
            var list = new List<TtsVoice>();
            void Add(string id, string gender)
            {
                var parts = id.Split('-');
                list.Add(new TtsVoice { Id = id, Locale = $"{parts[0]}-{parts[1]}", Gender = gender, Engine = TtsEngine.Neural });
            }

            Add("hi-IN-SwaraNeural", "Female");
            Add("hi-IN-MadhurNeural", "Male");
            Add("en-IN-NeerjaNeural", "Female");
            Add("en-IN-PrabhatNeural", "Male");
            Add("en-US-AndrewNeural", "Male");
            Add("en-US-AriaNeural", "Female");
            Add("en-US-GuyNeural", "Male");
            Add("en-US-JennyNeural", "Female");
            Add("en-US-ChristopherNeural", "Male");
            Add("en-US-EmmaMultilingualNeural", "Female");
            Add("en-GB-RyanNeural", "Male");
            Add("en-GB-SoniaNeural", "Female");
            Add("bn-IN-TanishaaNeural", "Female");
            Add("bn-IN-BashkarNeural", "Male");
            Add("mr-IN-AarohiNeural", "Female");
            Add("mr-IN-ManoharNeural", "Male");
            Add("ta-IN-PallaviNeural", "Female");
            Add("ta-IN-ValluvarNeural", "Male");
            Add("te-IN-ShrutiNeural", "Female");
            Add("te-IN-MohanNeural", "Male");
            Add("gu-IN-DhwaniNeural", "Female");
            Add("gu-IN-NiranjanNeural", "Male");
            Add("ur-PK-UzmaNeural", "Female");
            Add("ur-PK-AsadNeural", "Male");
            Add("ja-JP-NanamiNeural", "Female");
            Add("ja-JP-KeitaNeural", "Male");
            Add("es-ES-ElviraNeural", "Female");
            Add("fr-FR-HenriNeural", "Male");
            Add("de-DE-ConradNeural", "Male");
            return list;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Time-based token Microsoft requires (SHA256 of 5-minute Windows file time + client token).</summary>
        private static string SecMsGec()
        {
            var unix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + _clockSkewSeconds;
            long seconds = (long)Math.Floor(unix) + WinEpochSeconds;
            seconds -= seconds % 300;
            long ticks = seconds * 10_000_000L;
            var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(ticks.ToString(CultureInfo.InvariantCulture) + TrustedClientToken));
            return Convert.ToHexString(bytes); // uppercase
        }

        private static bool TryFixClock(IReadOnlyDictionary<string, IEnumerable<string>>? headers)
        {
            if (headers == null || !headers.TryGetValue("Date", out var values)) return false;
            foreach (var value in values)
            {
                if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var server))
                {
                    var skew = (server - DateTimeOffset.UtcNow).TotalSeconds;
                    if (Math.Abs(skew) < 60) return false; // clock is fine, something else is wrong
                    _clockSkewSeconds = skew;
                    return true;
                }
            }
            return false;
        }

        private static async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        private static string JsDate() =>
            DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture) +
            " GMT+0000 (Coordinated Universal Time)";

        private static string NewId() => Guid.NewGuid().ToString("N").ToUpperInvariant();
        private static string NewRequestId() => Guid.NewGuid().ToString("N");

        private static string Signed(int value) => value >= 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);

        /// <summary>The service rejects some control characters.</summary>
        private static string CleanText(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                int code = c;
                sb.Append((code <= 8) || (code >= 11 && code <= 12) || (code >= 14 && code <= 31) ? ' ' : c);
            }
            return sb.ToString();
        }

        private sealed class ClockSkewException : Exception { }
    }
}
