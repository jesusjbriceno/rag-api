using System.Text;
using Microsoft.Extensions.Logging;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class TxtOperationProcessor(
    IOperationCompletionRepository operations,
    IImmutableContentStore contentStore,
    TxtChunker chunker,
    ILogger<TxtOperationProcessor> logger) : IOperationProcessor
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<OperationProcessingDisposition> ProcessAsync(Operation operation, CancellationToken cancellationToken)
    {
        try
        {
            var version = await operations.GetDocumentVersionAsync(operation.DocumentVersionId, cancellationToken)
                ?? throw new ProcessingException("load", "The document version no longer exists.");
            var content = await ReadContentAsync(version, cancellationToken);
            var text = DecodeContent(content);
            var chunks = ChunkContent(version.Id, text);

            return await operations.TryCompleteSuccessAsync(operation, chunks, cancellationToken)
                ? OperationProcessingDisposition.Succeeded
                : OperationProcessingDisposition.LeaseLost;
        }
        catch (ProcessingException exception)
        {
            logger.LogWarning(
                "Operation {OperationId} failed during {Stage}: {Message}",
                operation.Id,
                exception.Stage,
                exception.Message);
            return await operations.TryCompleteFailureAsync(
                operation,
                exception.Stage,
                Truncate(exception.Message),
                cancellationToken)
                ? OperationProcessingDisposition.Failed
                : OperationProcessingDisposition.LeaseLost;
        }
    }

    private async Task<byte[]> ReadContentAsync(DocumentVersion version, CancellationToken cancellationToken)
    {
        try
        {
            return await contentStore.ReadAsync(version.ContentReference, version.ContentHash, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new ProcessingException("load", exception.Message, exception);
        }
    }

    private static string DecodeContent(byte[] content)
    {
        try
        {
            var text = StrictUtf8.GetString(content);
            if (text.Contains('\0'))
            {
                throw new InvalidDataException("The immutable content contains a NUL character.");
            }

            return text;
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProcessingException("parse", "The immutable content is not valid UTF-8 TXT.", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new ProcessingException("parse", exception.Message, exception);
        }
    }

    private IReadOnlyList<Chunk> ChunkContent(Guid documentVersionId, string content)
    {
        try
        {
            return chunker.Chunk(documentVersionId, content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content);
        }
        catch (InvalidDataException exception)
        {
            throw new ProcessingException("parse", exception.Message, exception);
        }
        catch (ArgumentException exception)
        {
            throw new ProcessingException("parse", exception.Message, exception);
        }
    }

    private static string Truncate(string message) => message.Length <= 2_000 ? message : message[..2_000];

    private sealed class ProcessingException(string stage, string message, Exception? innerException = null)
        : Exception(message, innerException)
    {
        public string Stage { get; } = stage;
    }
}
