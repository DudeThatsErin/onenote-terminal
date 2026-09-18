using System.Globalization;
using System.Text.RegularExpressions;

namespace OneNoteSystem.Cli;

/// <summary>
/// Due dates are validated locally so a typo costs a round trip to nobody, and so the
/// shorthand a person actually types in a shell works.
/// </summary>
public static partial class DueDates
{
    private static readonly Dictionary<string, int> DayWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["today"] = 0,
        ["tomorrow"] = 1,
        ["yesterday"] = -1,
    };

    [GeneratedRegex(@"^\+(\d+)\s*(d|days?|w|weeks?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Relative();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex BareDate();

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2}))?$")]
    private static partial Regex DateTimeValue();

    [GeneratedRegex(@"Z$|[+-]\d{2}:\d{2}$")]
    private static partial Regex HasOffset();

    /// <summary>
    /// Returns either a plain YYYY-MM-DD or a local ISO date-time without a zone.
    /// Microsoft To Do pairs the value with an explicit time zone, so a bare date must stay a
    /// bare date -- turning it into UTC here would shift the task a day for anyone west of London.
    /// </summary>
    public static string Parse(string? input, string flag, DateTime? now = null)
    {
        var value = (input ?? string.Empty).Trim();
        if (value.Length == 0) throw new CliException($"{flag} is required", ExitCode.Usage);

        var today = (now ?? DateTime.Now).Date;

        var relative = Relative().Match(value);
        if (relative.Success)
        {
            var count = int.Parse(relative.Groups[1].Value, CultureInfo.InvariantCulture);
            var days = count * (relative.Groups[2].Value.StartsWith('w') || relative.Groups[2].Value.StartsWith('W') ? 7 : 1);
            return LocalDate(today.AddDays(days));
        }

        if (DayWords.TryGetValue(value, out var offset)) return LocalDate(today.AddDays(offset));

        if (BareDate().IsMatch(value))
        {
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new CliException($"{flag} \"{value}\" is not a real date", ExitCode.Usage);
            return value;
        }

        var dateTime = DateTimeValue().Match(value);
        if (dateTime.Success)
        {
            var seconds = dateTime.Groups[6].Success ? dateTime.Groups[6].Value : "00";
            var normalized =
                $"{dateTime.Groups[1].Value}-{dateTime.Groups[2].Value}-{dateTime.Groups[3].Value}" +
                $"T{dateTime.Groups[4].Value}:{dateTime.Groups[5].Value}:{seconds}";
            if (!DateTime.TryParseExact(normalized, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new CliException($"{flag} \"{value}\" is not a real date", ExitCode.Usage);
            return normalized;
        }

        throw new CliException(
            $"{flag} \"{value}\" is not a date. Try 2026-09-15, \"tomorrow\", or \"+3d\".", ExitCode.Usage);
    }

    /// <summary>The caller's IANA zone, so the deployment can anchor a bare date correctly.</summary>
    public static string LocalTimeZone()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "UTC";
    }

    /// <summary>Renders a due value for humans. A bare date has no time to show; anything else is a real moment.</summary>
    public static string Format(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (BareDate().IsMatch(value)) return value;

        var text = HasOffset().IsMatch(value) ? value : $"{value}Z";
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
            ? moment.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : value;
    }

    private static string LocalDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
