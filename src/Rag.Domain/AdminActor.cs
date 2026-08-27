namespace Rag.Domain;

public readonly record struct AdminActor
{
    public AdminActor(string actorSubject, string appId)
    {
        if (string.IsNullOrWhiteSpace(actorSubject))
        {
            throw new ArgumentException("An actor subject is required.", nameof(actorSubject));
        }

        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("An app id is required.", nameof(appId));
        }

        ActorSubject = actorSubject.Trim();
        AppId = appId.Trim();
    }

    public string ActorSubject { get; }

    public string AppId { get; }

    public override string ToString() => $"{ActorSubject}@{AppId}";
}
