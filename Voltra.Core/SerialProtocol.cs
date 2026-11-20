using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Voltra;

internal static partial class SerialProtocol
{
    internal const int ChannelCount = 5;
    internal const int FrameSize = 1024;
    internal const int ChunkSize = 256;

    internal static bool IsIdentity(string line, string nonce) => line == $"PURPLEMIXER,1,{nonce}";

    internal static bool TryLevels(string line, Span<int> levels)
    {
        if (levels.Length != ChannelCount || !line.StartsWith("MIX,", StringComparison.Ordinal)) return false;
        ReadOnlySpan<char> data = line.AsSpan(4);
        for (int i = 0; i < ChannelCount; i++)
        {
            int comma = data.IndexOf(',');
            if ((comma < 0) != (i == ChannelCount - 1)) return false;
            ReadOnlySpan<char> part = comma < 0 ? data : data[..comma];
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out levels[i]) ||
                levels[i] is < 0 or > 100) return false;
            if (comma >= 0) data = data[(comma + 1)..];
        }
        return true;
    }

    internal static string? FirmwareVersion(string line, string nonce)
    {
        if (!line.StartsWith("VOLTRA,INFO,", StringComparison.Ordinal)) return null;
        var fields = line.Split(',');
        return fields.Length == 10 && fields[2] == nonce && fields[3] == "VOLTRA-S3-ZERO" &&
            StableVersion().IsMatch(fields[4]) && fields[5] == "1" && MacAddress().IsMatch(fields[6]) &&
            fields[7] == "4194304" && fields[8] == "65536" && fields[9] == "65536" ? fields[4] : null;
    }

    internal static string FrameChunk(byte[] frame, int index) =>
        $"@C{index},{Convert.ToBase64String(frame, index * ChunkSize, ChunkSize)}";

    internal static string PageCommand(int page, int flags, DateTimeOffset local, uint media) =>
        FormattableString.Invariant($"@P,{page},{flags},{local.ToUnixTimeSeconds() + (long)local.Offset.TotalSeconds},{media}");

    internal static string Ascii(string text, int length)
    {
        var normalized = new StringBuilder(length);
        foreach (char ch in text.Replace('ł', 'l').Replace('Ł', 'L').Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            normalized.Append(ch is >= ' ' and <= '~' && ch != '@' ? ch : '?');
            if (normalized.Length == length) break;
        }
        return normalized.ToString();
    }

    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")]
    internal static partial Regex StableVersion();
    [GeneratedRegex(@"\A[0-9A-F]{12}\z")]
    private static partial Regex MacAddress();
}
