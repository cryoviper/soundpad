using System;
using System.Globalization;

namespace BoomBx.Models
{
    /// <summary>
    /// Turns "1:23", "83" or "1:02:03" into seconds and back.
    /// </summary>
    public static class TimeText
    {
        public static bool TryParse(string? text, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(text)) return true;

            var parts = text.Trim().Split(':');
            if (parts.Length > 3) return false;

            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
                    return false;
                total = total * 60 + value;
            }

            seconds = total;
            return true;
        }

        public static string Format(double seconds)
        {
            if (seconds <= 0) return "0:00";
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
        }
    }
}
