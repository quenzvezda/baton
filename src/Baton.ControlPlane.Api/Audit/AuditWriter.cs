using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Automations;
using Baton.Domain.Hosts;
using Baton.Persistence;
using Baton.Persistence.Entities;

namespace Baton.ControlPlane.Api.Audit;

/// <summary>What an audit entry is about (<c>audit_log.entity_type</c>).</summary>
public static class AuditEntities
{
    public const string Automation = "automation";
    public const string HostGroup = "host_group";
    public const string AgentCredential = "agent_credential";
    public const string WorkflowLimit = "workflow_limit";

    public static readonly IReadOnlyList<string> All = [Automation, HostGroup, AgentCredential, WorkflowLimit];
}

/// <summary>What happened (<c>audit_log.action</c>).</summary>
public static class AuditActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Clone = "clone";
    public const string Enable = "enable";
    public const string Disable = "disable";
    public const string RotateSecret = "rotate_secret";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Issue = "issue";
    public const string Revoke = "revoke";
    public const string Set = "set";
    public const string Remove = "remove";
}

/// <summary>
/// AUT-11 (add-operability-and-insight design.md decisions 13–14): adds an audit entry to the request's
/// <see cref="BatonDbContext"/>, so it is saved by the same <c>SaveChangesAsync</c>, inside the same transaction, as
/// the change it records. A change that is rejected never reaches <c>SaveChanges</c> and leaves no entry.
/// <c>before</c> and <c>after</c> are the endpoint's own response bodies, serialized like the API serializes them,
/// minus every <see cref="SecretAttribute"/> property; a serialized value that still looks like a Baton secret
/// fails loudly instead of being stored.
/// </summary>
public static class AuditWriter
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { DropSecrets } },
    };

    private static readonly string[] SecretMarkers = [WebhookSignature.SecretPrefix, AgentToken.Prefix];

    /// <summary>The JSON stored for <paramref name="value"/>; <c>null</c> stays <c>null</c>.</summary>
    public static string? Snapshot(object? value)
    {
        if (value is null)
            return null;

        var json = JsonSerializer.Serialize(value, value.GetType(), Options);
        foreach (var marker in SecretMarkers)
        {
            if (json.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException($"An audit snapshot of {value.GetType().Name} contains a secret value.");
        }

        return json;
    }

    /// <summary>Adds an entry whose sides are already snapshots (see <see cref="Snapshot"/>).</summary>
    public static void AddSnapshots(
        BatonDbContext db, ClaimsPrincipal user, string entityType, string entityId, string action, string? before, string? after)
    {
        ArgumentNullException.ThrowIfNull(db);

        var actor = Actor.From(user);
        db.AuditLog.Add(new AuditEntryEntity
        {
            ActorSubject = actor.Subject,
            ActorName = actor.Name,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Before = before,
            After = after,
        });
    }

    /// <summary>Snapshots both sides and adds the entry.</summary>
    public static void Add(
        BatonDbContext db, ClaimsPrincipal user, string entityType, string entityId, string action, object? before, object? after) =>
        AddSnapshots(db, user, entityType, entityId, action, Snapshot(before), Snapshot(after));

    private static void DropSecrets(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.AttributeProvider?.IsDefined(typeof(SecretAttribute), inherit: true) == true)
                property.ShouldSerialize = static (_, _) => false;
        }
    }
}
