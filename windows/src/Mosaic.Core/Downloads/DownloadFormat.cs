using System.Globalization;

namespace Mosaic.Core.Downloads;

/// <summary>Text for sizes, speeds and times (port of <c>DownloadFormat</c> in DownloadSupport.swift).</summary>
public static class DownloadFormat
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    /// <summary>"512 bytes", "12.4 MB": 1024-based with Windows Explorer's unit names.</summary>
    public static string Bytes(long count)
    {
        double value = Math.Max(0, count);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        if (unit == 0) return count == 1 ? "1 byte" : $"{(long)value} bytes";
        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond) => $"{Bytes((long)bytesPerSecond)}/s";

    /// <summary>"20 s left", "3 min 5 s left", "1 h 12 min left".</summary>
    public static string Remaining(double seconds)
    {
        var total = (long)Math.Max(1, Math.Round(seconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var rest = total % 60;
        var text = hours > 0
            ? minutes > 0 ? $"{hours} h {minutes} min" : $"{hours} h"
            : minutes > 0 ? rest > 0 ? $"{minutes} min {rest} s" : $"{minutes} min" : $"{rest} s";
        return text + " left";
    }

    /// <summary>One status line: "12 MB of 40 MB · 1.5 MB/s · 20 s left", "40 MB", "Failed · Network error"…</summary>
    public static string Status(DownloadRecord record)
    {
        switch (record.State)
        {
            case DownloadState.InProgress:
                var parts = new List<string>
                {
                    record.TotalBytes > 0
                        ? $"{Bytes(record.ReceivedBytes)} of {Bytes(record.TotalBytes)}"
                        : Bytes(record.ReceivedBytes),
                };
                if (record.Speed > 0) parts.Add(Speed(record.Speed));
                if (record.SecondsRemaining is { } left) parts.Add(Remaining(left));
                return string.Join(" · ", parts);
            case DownloadState.Paused:
                var size = record.TotalBytes > 0
                    ? $"{Bytes(record.ReceivedBytes)} of {Bytes(record.TotalBytes)}"
                    : Bytes(record.ReceivedBytes);
                return $"Paused · {size}";
            case DownloadState.Completed:
                var bytes = record.TotalBytes > 0 ? record.TotalBytes : record.ReceivedBytes;
                return bytes > 0 ? Bytes(bytes) : "Completed";
            case DownloadState.Failed:
                return "Failed" + (record.ErrorMessage is { } error ? $" · {error}" : "");
            case DownloadState.Cancelled:
                return record.ErrorMessage ?? "Cancelled";
            default:
                return record.ErrorMessage ?? "Sent to Free Download Manager";
        }
    }

    /// <summary>"Today", "Yesterday", or the full date, for the Downloads page's sections.</summary>
    public static string DaySection(DateTimeOffset started, DateTimeOffset now)
    {
        var day = started.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.ToString("D", CultureInfo.CurrentCulture);
    }
}
