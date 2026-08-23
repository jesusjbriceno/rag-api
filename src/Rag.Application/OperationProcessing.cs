using Rag.Domain;

namespace Rag.Application;

public enum OperationProcessingDisposition
{
    Succeeded,
    Failed,
    LeaseLost,
}

public interface IOperationProcessor
{
    Task<OperationProcessingDisposition> ProcessAsync(Operation operation, CancellationToken cancellationToken);
}
