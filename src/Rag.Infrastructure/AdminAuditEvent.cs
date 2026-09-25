using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class AdminAuditEvent
{
    private AdminAuditEvent()
    {
    }

    public AdminAuditEvent(
        Guid id,
        AdminActor actor,
        string action,
        string outcome,
        DateTimeOffset occurredAt,
        string? targetType = null,
        string? targetId = null,
        string? operation = null,
        string? allowlistedJson = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An audit event id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("An audit action is required.", nameof(action));
        }

        if (string.IsNullOrWhiteSpace(outcome))
        {
            throw new ArgumentException("An audit outcome is required.", nameof(outcome));
        }

        Id = id;
        ActorSubject = actor.ActorSubject;
        AppId = actor.AppId;
        Action = action.Trim();
        Outcome = outcome.Trim();
        OccurredAt = occurredAt;
        TargetType = Normalize(targetType);
        TargetId = Normalize(targetId);
        Operation = Normalize(operation);
        AllowlistedJson = allowlistedJson;
    }

    public Guid Id { get; private set; }

    public string ActorSubject { get; private set; } = null!;

    public string AppId { get; private set; } = null!;

    public string Action { get; private set; } = null!;

    public string Outcome { get; private set; } = null!;

    public DateTimeOffset OccurredAt { get; private set; }

    public string? TargetType { get; private set; }

    public string? TargetId { get; private set; }

    public string? Operation { get; private set; }

    public string? AllowlistedJson { get; private set; }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
