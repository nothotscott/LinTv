using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace LinTv.Core.Domain
{
    /// Times of day, in the server's local time zone, parsed from strings like "11am",
    /// "11:30 pm" or "23:00".
    public sealed class DailySchedule
    {
        private static readonly string[] Formats =
            ["htt", "hhtt", "h:mmtt", "hh:mmtt", "H:mm", "HH:mm"];

        public IReadOnlyList<TimeOnly> Times { get; }

        public bool IsEmpty => Times.Count == 0;

        private DailySchedule(IReadOnlyList<TimeOnly> times)
        {
            Times = times;
        }

        public static bool TryParse(IEnumerable<string>? entries, [NotNullWhen(true)] out DailySchedule? schedule,
            [NotNullWhen(false)] out string? error)
        {
            schedule = null;
            error = null;
            var times = new List<TimeOnly>();

            foreach (var entry in entries ?? [])
            {
                // "11 PM", "11pm" and "11 pm" all normalise to "11PM".
                var text = entry.Replace(" ", "").ToUpperInvariant();
                if (!TimeOnly.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                {
                    error = $"'{entry}' is not a time of day (examples: \"11am\", \"11:30pm\", \"23:00\")";
                    return false;
                }
                times.Add(time);
            }

            schedule = new DailySchedule(times.Distinct().Order().ToList());
            return true;
        }

        /// The first scheduled time strictly after <paramref name="now"/>, in local time.
        public DateTimeOffset NextAfter(DateTimeOffset now)
        {
            if (IsEmpty) throw new InvalidOperationException("The schedule is empty");

            var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
            for (int day = 0; day <= 1; day++)
            {
                var date = DateOnly.FromDateTime(local.DateTime).AddDays(day);
                foreach (var time in Times)
                {
                    // A time skipped by a DST jump resolves to the equivalent instant after it.
                    var candidate = new DateTimeOffset(date.ToDateTime(time, DateTimeKind.Local).ToUniversalTime());
                    if (candidate > now) return candidate.ToLocalTime();
                }
            }

            // Unreachable: every time recurs within 24h (+1h for DST).
            throw new InvalidOperationException("No next run found");
        }

        public override string ToString() =>
            string.Join(", ", Times.Select(t => t.ToString("h:mmtt", CultureInfo.InvariantCulture).ToLowerInvariant()));
    }
}
