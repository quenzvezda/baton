using Baton.ControlPlane.Api.Audit;
using Baton.Domain.Notifications;
using Baton.Persistence;
using Baton.Persistence.Entities;
using Baton.Persistence.Security;
using Microsoft.EntityFrameworkCore;

namespace Baton.ControlPlane.Api.Automations;

/// <summary>A notification channel in a create or update body; an <see cref="Id"/> keeps that existing channel (OBS-05).</summary>
/// <param name="On"><c>failed</c>, <c>timed_out</c> or both.</param>
/// <param name="Recipients">Email only.</param>
/// <param name="Url">Webhook only. Write-only: leave it out on update to keep the stored URL.</param>
public sealed record NotificationRequest(Guid? Id, string? Kind, string[]? On, string[]? Recipients = null, string? Url = null);

/// <param name="Url">Webhook only: <c>scheme://host</c>, never the path, query or credentials.</param>
/// <param name="SigningSecret">Only in the response that issued it (channel created, or its secret rotated).</param>
public sealed record NotificationResponse(
    Guid Id, string Kind, string[] On, string[]? Recipients, string? Url, [property: Secret] string? SigningSecret);

public sealed record SigningSecretRotated(Guid ChannelId, [property: Secret] string SigningSecret);

/// <summary>
/// The automation's notification channels (OBS-05, add-operability-and-insight design.md decision 16): validation
/// beyond <see cref="NotificationRules"/>, applying a request to the stored channels, and their responses.
/// </summary>
internal static class NotificationChannels
{
    public static List<NotificationSettings?>? Settings(NotificationRequest[]? requested) =>
        requested?.Select(c => c is null ? null : new NotificationSettings(c.Kind, c.On, c.Recipients, c.Url, c.Id is not null)).ToList();

    /// <summary>Ids must name this automation's channels, and a channel's kind cannot change.</summary>
    public static void ValidateIds(NotificationRequest[] requested, List<NotificationChannelEntity> existing, Dictionary<string, string[]> errors)
    {
        for (var i = 0; i < requested.Length; i++)
        {
            if (requested[i]?.Id is not { } channelId)
                continue;

            var current = existing.SingleOrDefault(c => c.Id == channelId);
            if (current is null)
                errors[$"notifications[{i}].id"] = ["This automation has no channel with this id; leave it out to add a channel."];
            else if (current.Kind != requested[i].Kind)
                errors[$"notifications[{i}].kind"] = ["A channel's kind cannot change; remove it and add a new one."];
        }
    }

    /// <summary>
    /// Adds, changes and removes the tracked channels to match <paramref name="requested"/>. New webhook channels get
    /// a signing secret, returned once through <paramref name="issued"/>.
    /// </summary>
    /// <returns>The ids of existing webhook channels whose URL changed, for the audit entry.</returns>
    public static List<Guid> Apply(
        BatonDbContext db, Guid automationId, NotificationRequest[] requested, List<NotificationChannelEntity> existing,
        NotificationSecrets secrets, Dictionary<Guid, string> issued)
    {
        var changedUrls = new List<Guid>();
        var kept = new HashSet<Guid>();
        foreach (var channel in requested)
        {
            var current = channel.Id is { } id ? existing.Single(c => c.Id == id) : null;
            if (current is null)
            {
                current = new NotificationChannelEntity { Id = Guid.CreateVersion7(), AutomationId = automationId, Kind = channel.Kind! };
                if (current.Kind == NotificationRules.WebhookKind)
                {
                    var (secret, stored) = secrets.IssueSecret();
                    current.WebhookSecret = stored;
                    issued[current.Id] = secret;
                }

                db.NotificationChannels.Add(current);
            }
            else
            {
                kept.Add(current.Id);
            }

            current.Events = [.. channel.On!.Distinct()];
            if (current.Kind == NotificationRules.EmailKind)
            {
                current.EmailTo = [.. channel.Recipients!];
            }
            else if (channel.Url is { } url)
            {
                if (current.WebhookUrl is { } stored && kept.Contains(current.Id) && secrets.RevealUrl(stored) != url)
                    changedUrls.Add(current.Id);
                current.WebhookUrl = secrets.ProtectUrl(url);
                current.WebhookUrlDisplay = NotificationRules.DisplayUrl(new Uri(url));
            }
        }

        db.NotificationChannels.RemoveRange(existing.Where(c => !kept.Contains(c.Id)));
        return changedUrls;
    }

    /// <summary>Email channels only: a clone must not share a webhook's signing secret or URL token.</summary>
    public static int CopyForClone(BatonDbContext db, Guid cloneId, IEnumerable<NotificationChannelEntity> source)
    {
        var dropped = 0;
        foreach (var channel in source)
        {
            if (channel.Kind != NotificationRules.EmailKind)
            {
                dropped++;
                continue;
            }

            db.NotificationChannels.Add(new NotificationChannelEntity
            {
                Id = Guid.CreateVersion7(),
                AutomationId = cloneId,
                Kind = channel.Kind,
                Events = channel.Events,
                EmailTo = channel.EmailTo,
            });
        }

        return dropped;
    }

    public static Task<List<NotificationChannelEntity>> LoadAsync(BatonDbContext db, Guid automationId, CancellationToken ct) =>
        db.NotificationChannels.AsNoTracking().Where(c => c.AutomationId == automationId).ToListAsync(ct);

    public static NotificationResponse[] ToResponse(IEnumerable<NotificationChannelEntity> channels, IReadOnlyDictionary<Guid, string>? issued) =>
    [
        .. channels.OrderBy(c => c.Id).Select(c => new NotificationResponse(
            c.Id, c.Kind, c.Events, c.EmailTo, c.WebhookUrlDisplay, issued?.GetValueOrDefault(c.Id))),
    ];
}
