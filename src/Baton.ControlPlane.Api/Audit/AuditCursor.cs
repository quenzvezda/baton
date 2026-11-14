using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Baton.ControlPlane.Api.Audit;

/// <summary>Position in the newest-first audit list: the last <c>(at, id)</c> a page returned, base64url of <c>{ticks}:{id}</c>.</summary>
public sealed record AuditCursor(DateTimeOffset At, long Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes(
            $"{At.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{Id.ToString(CultureInfo.InvariantCulture)}"));

    public static bool TryDecode(string? value, out AuditCursor? cursor)
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
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return false;
        }

        cursor = new AuditCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }
}
