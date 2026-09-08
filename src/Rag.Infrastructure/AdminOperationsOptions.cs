namespace Rag.Infrastructure;

public sealed class AdminOperationsOptions
{
    public const string SectionName = "AdminOperations";

    public int RetentionHours { get; set; }

    public void Validate()
    {
        if (RetentionHours <= 0)
        {
            throw new InvalidOperationException("Admin operations retention hours must be positive.");
        }
    }
}
