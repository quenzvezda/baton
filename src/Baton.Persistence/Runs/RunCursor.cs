using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Baton.Persistence.Runs;

/// <summary>
/// Position in the newest-first runs list (OBS-01): the last <c>(created_at, id)</c> a page returned. Opaque to
/// clients — base64url of <c>{ticks}:{id}</c>.
/// </summary>
public sealed record RunCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes(
            $"{CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{Id:N}"));

    public static bool TryDecode(string? value, out RunCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(value) || value.Length > 100)
            return false;

        string text;
        try
        {
            text = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(value));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = text.Split(':');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(parts[1], "N", out var id))
        {
            return false;
        }

        cursor = new RunCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }
}
