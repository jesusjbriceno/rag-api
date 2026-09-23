namespace Rag.AdminApp.Host;

/// <summary>
/// Ordinal, case-sensitive membership check over the configured administrator
/// subjects. An empty set authorises nobody.
/// </summary>
public sealed class AdminSubjectAllowlist(IReadOnlySet<string> subjects)
{
    public bool Contains(string subject) => subjects.Contains(subject);
}
