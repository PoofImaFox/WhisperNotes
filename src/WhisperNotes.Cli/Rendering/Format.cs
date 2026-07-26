using System.Globalization;

namespace WhisperNotes.Cli.Rendering;

/// <summary>Shared number/time formatting so every command prints the same shapes.</summary>
internal static class Format
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary><c>hh:mm:ss</c> — the form used for transcript timestamps and durations.</summary>
    public static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}",
            (int)value.TotalHours,
            value.Minutes,
            value.Seconds);
    }

    /// <summary>"1 hr 2 min 12 sec" — the form a human reads in the finalize line.</summary>
    public static string Human(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var parts = new List<string>(3);
        var hours = (int)value.TotalHours;

        if (hours > 0)
        {
            parts.Add($"{hours.ToString(CultureInfo.InvariantCulture)} hr");
        }

        if (value.Minutes > 0 || hours > 0)
        {
            parts.Add($"{value.Minutes.ToString(CultureInfo.InvariantCulture)} min");
        }

        parts.Add($"{value.Seconds.ToString(CultureInfo.InvariantCulture)} sec");
        return string.Join(' ', parts);
    }

    public static string Bytes(long value)
    {
        if (value < 0)
        {
            return "-";
        }

        double scaled = value;
        var unit = 0;
        while (scaled >= 1024 && unit < ByteUnits.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            unit == 0 ? "{0:0} {1}" : "{0:0.0} {1}",
            scaled,
            ByteUnits[unit]);
    }

    public static string Count(int value, string singular, string plural) =>
        $"{value.ToString(CultureInfo.InvariantCulture)} {(value == 1 ? singular : plural)}";

    public static string Percent(double fraction) =>
        ((int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%";
}
