using System.Net.Mail;

namespace Baton.Domain.Notifications;

/// <summary>Deployment settings for notifications (section <c>Baton:Notifications</c>), read by the API and the orchestrator.</summary>
public sealed class NotificationPolicy
{
    public const string Section = "Baton:Notifications";

    /// <summary>Accept <c>http://</c> webhook URLs. Development and tests only.</summary>
    public bool AllowInsecureWebhooks { get; set; }

    /// <summary>When not empty, webhook URLs must point at one of these hosts (case-insensitive).</summary>
    public string[] AllowedWebhookHosts { get; set; } = [];

    /// <summary>Base address of the web console; notifications link to <c>{ConsoleBaseUrl}/runs/{id}</c> when set.</summary>
    public string? ConsoleBaseUrl { get; set; }
}

/// <summary>A notification channel as a create or update request describes it.</summary>
/// <param name="IsExisting">The request names an existing channel, so a webhook may leave its URL out to keep it.</param>
public sealed record NotificationSettings(string? Kind, IReadOnlyList<string?>? On, IReadOnlyList<string?>? Recipients, string? Url, bool IsExisting);

/// <summary>
/// OBS-05 "Notification channels per automation" (add-operability-and-insight design.md decision 16): up to
/// <see cref="MaxChannels"/> channels, each email (1–<see cref="MaxRecipients"/> plain addresses) or webhook (one
/// absolute HTTPS URL), each firing on <c>failed</c>, <c>timed_out</c> or both. Errors are keyed
/// <c>notifications[i].field</c>, like trigger errors.
/// </summary>
public static class NotificationRules
{
    public const string EmailKind = "email";
    public const string WebhookKind = "webhook";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";

    public const int MaxChannels = 10;
    public const int MaxRecipients = 20;
    public const int MaxUrlLength = 2_000;
    public const int MaxAddressLength = 320;

    public static readonly IReadOnlyList<string> Kinds = [EmailKind, WebhookKind];
    public static readonly IReadOnlyList<string> Events = [Failed, TimedOut];

    public static Dictionary<string, string[]> Validate(IReadOnlyList<NotificationSettings?>? channels, NotificationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var errors = new Dictionary<string, string[]>();
        if (channels is null)
            return errors;
        if (channels.Count > MaxChannels)
            errors["notifications"] = [$"At most {MaxChannels} channels."];

        for (var i = 0; i < channels.Count; i++)
        {
            var prefix = $"notifications[{i}]";
            if (channels[i] is not { } channel)
            {
                errors[prefix] = ["Required."];
                continue;
            }

            if (channel.On is not { Count: > 0 } || channel.On.Any(e => e is null || !Events.Contains(e)) || channel.On.Distinct().Count() != channel.On.Count)
                errors[$"{prefix}.on"] = [$"One or both of {string.Join(", ", Events)}, each once."];

            switch (channel.Kind)
            {
                case EmailKind:
                    ValidateRecipients(channel, prefix, errors);
                    if (channel.Url is not null)
                        errors[$"{prefix}.url"] = ["Only webhook channels have a URL."];
                    break;
                case WebhookKind:
                    ValidateUrl(channel, prefix, policy, errors);
                    if (channel.Recipients is not null)
                        errors[$"{prefix}.recipients"] = ["Only email channels have recipients."];
                    break;
                default:
                    errors[$"{prefix}.kind"] = [$"One of {string.Join(", ", Kinds)}."];
                    break;
            }
        }

        return errors;
    }

    /// <summary><c>scheme://host[:port]</c> of a URL: what reads and the audit log show (design.md decision 16).</summary>
    public static string DisplayUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.IsDefaultPort ? $"{url.Scheme}://{url.Host}" : $"{url.Scheme}://{url.Host}:{url.Port}";
    }

    private static void ValidateRecipients(NotificationSettings channel, string prefix, Dictionary<string, string[]> errors)
    {
        if (channel.Recipients is not { Count: > 0 and <= MaxRecipients } recipients)
        {
            errors[$"{prefix}.recipients"] = [$"Between 1 and {MaxRecipients} addresses."];
            return;
        }

        if (recipients.Any(r => !IsPlainAddress(r)))
            errors[$"{prefix}.recipients"] = ["Each recipient must be a plain email address, such as ops@example.com."];
    }

    private static void ValidateUrl(NotificationSettings channel, string prefix, NotificationPolicy policy, Dictionary<string, string[]> errors)
    {
        if (channel.Url is null)
        {
            if (!channel.IsExisting)
                errors[$"{prefix}.url"] = ["Required for a new webhook channel."];
            return;
        }

        if (channel.Url.Length > MaxUrlLength
            || !Uri.TryCreate(channel.Url, UriKind.Absolute, out var url)
            || !(url.Scheme == Uri.UriSchemeHttps || (policy.AllowInsecureWebhooks && url.Scheme == Uri.UriSchemeHttp)))
        {
            errors[$"{prefix}.url"] = [policy.AllowInsecureWebhooks
                ? "An absolute http or https URL."
                : "An absolute https URL."];
            return;
        }

        if (policy.AllowedWebhookHosts.Length > 0
            && !policy.AllowedWebhookHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
        {
            errors[$"{prefix}.url"] = ["This deployment does not allow webhooks to that host."];
        }
    }

    private static bool IsPlainAddress(string? value) =>
        value is { Length: > 0 and <= MaxAddressLength }
        && MailAddress.TryCreate(value, out var address)
        && address.Address == value
        && string.IsNullOrEmpty(address.DisplayName);
}
