using Rag.Domain;

namespace Rag.Application;

public enum OperationProcessingDisposition
{
    Deferred,
}

public interface IOperationProcessor
{
    Task<OperationProcessingDisposition> ProcessAsync(Operation operation, CancellationToken cancellationToken);
}
