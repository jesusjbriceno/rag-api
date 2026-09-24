namespace Rag.Application.Auth;

public static class HistoricalScopes
{
    public const string UploadsWrite = "historical:uploads.write";

    public const string OperationsRead = "historical:operations.read";

    public static readonly string[] Known = [UploadsWrite, OperationsRead];

    public static bool IsKnown(string scope) => Array.IndexOf(Known, scope) >= 0;

    public static IReadOnlyList<string> Parse(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return [];
        }

        return scope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    public static string Normalize(string? scope) => string.Join(' ', Parse(scope));

    public static bool Satisfies(IReadOnlyCollection<string>? grantedScopes, string requiredScope) =>
        grantedScopes is not null && grantedScopes.Contains(requiredScope, StringComparer.Ordinal);
}
