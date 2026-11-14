namespace Baton.ControlPlane.Api.Audit;

/// <summary>
/// Marks a response property that carries a secret (an issued webhook secret, agent token or signing secret).
/// <see cref="AuditWriter"/> never writes such a property to the audit log (AUT-11 "No secrets in the audit log").
/// On a positional record, apply it with the <c>property:</c> target.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SecretAttribute : Attribute;
