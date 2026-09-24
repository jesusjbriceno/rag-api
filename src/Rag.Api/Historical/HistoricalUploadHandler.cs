using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.Api.Historical;

public sealed record ReserveHistoricalUploadCommand(
    Guid ServiceClientId,
    Guid CollectionId,
    string SourceDocumentKey,
    string IdempotencyKey,
    string NormalizedTextSha256,
    long DeclaredBytes,
    Guid? CandidateId,
    Guid? ManifestId,
    Guid? RunId,
    string? DisplayName,
    string? Format,
    string? SourceRootAlias);

public sealed record ReserveHistoricalUploadResult(
    Guid UploadId,
    HistoricalUploadState State,
    string CorrelationId,
    bool Created,
    int MaxNormalizedTextBytes,
    int PerClientPendingQuota,
    long TotalStorageWatermarkBytes,
    TimeSpan AbandonedUploadExpiry);

public sealed record PublishedHistoricalUploadResult(
    Guid UploadId,
    HistoricalUploadState State,
    long DeclaredBytes,
    long ObservedBytes,
    string NormalizedTextSha256);

public sealed record CommitHistoricalUploadResult(
    Guid UploadId,
    Guid DocumentId,
    Guid DocumentVersionId,
    Guid OperationId,
    HistoricalUploadState State);

public sealed record GetHistoricalUploadResult(
    Guid UploadId,
    HistoricalUploadState State,
    string SourceDocumentKey,
    string NormalizedTextSha256,
    long DeclaredBytes,
    Guid? DocumentId,
    Guid? DocumentVersionId,
    Guid? OperationId,
    string CorrelationId);

public sealed class HistoricalUploadNotFoundException : Exception;

public sealed class HistoricalUploadConflictException : Exception;

public sealed class HistoricalQuotaExceededException : Exception;

public sealed class HistoricalWatermarkExceededException : Exception;

public sealed class HistoricalContentContractException(string message) : Exception(message);

public sealed class HistoricalContentTooLargeException(int maximumBytes) : Exception
{
    public int MaximumBytes { get; } = maximumBytes;
}

public sealed class HistoricalUploadHandler(
    IngestionDbContext dbContext,
    IImmutableContentStore contentStore,
    HistoricalIngestionOptions options)
{
    public async Task<ReserveHistoricalUploadResult> ReserveAsync(
        ReserveHistoricalUploadCommand command,
        CancellationToken cancellationToken)
    {
        ValidateReserve(command);
        var now = DateTimeOffset.UtcNow;
        await ExpireAbandonedUploadsAsync(command.ServiceClientId, now, cancellationToken);

        var collection = await dbContext.Collections.SingleOrDefaultAsync(
            item => item.Id == command.CollectionId && item.ServiceClientId == command.ServiceClientId,
            cancellationToken);
        if (collection is null)
        {
            throw new HistoricalUploadNotFoundException();
        }

        var fingerprint = ComputeFingerprint(command);
        var existing = await dbContext.HistoricalUploads.SingleOrDefaultAsync(
            upload => upload.ServiceClientId == command.ServiceClientId && upload.IdempotencyKey == command.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new HistoricalUploadConflictException();
            }

            return ToReserveResult(existing, created: false);
        }

        var pending = await dbContext.HistoricalUploads.CountAsync(
            upload => upload.ServiceClientId == command.ServiceClientId &&
                (upload.State == HistoricalUploadState.Reserved || upload.State == HistoricalUploadState.Published),
            cancellationToken);
        if (pending >= options.PerClientPendingQuota)
        {
            throw new HistoricalQuotaExceededException();
        }

        var upload = new HistoricalUpload(
            Guid.NewGuid(),
            command.ServiceClientId,
            command.CollectionId,
            command.SourceDocumentKey,
            command.IdempotencyKey,
            fingerprint,
            command.NormalizedTextSha256,
            command.DeclaredBytes,
            Guid.NewGuid().ToString("N"),
            command.CandidateId,
            command.ManifestId,
            command.RunId,
            command.DisplayName,
            command.Format,
            command.SourceRootAlias,
            now);
        dbContext.HistoricalUploads.Add(upload);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToReserveResult(upload, created: true);
    }

    public async Task<PublishedHistoricalUploadResult> PublishContentAsync(
        Guid uploadId,
        Guid serviceClientId,
        Stream body,
        CancellationToken cancellationToken)
    {
        var upload = await dbContext.HistoricalUploads.SingleOrDefaultAsync(
            item => item.Id == uploadId && item.ServiceClientId == serviceClientId,
            cancellationToken) ?? throw new HistoricalUploadNotFoundException();
        if (upload.State is HistoricalUploadState.Committed or HistoricalUploadState.Abandoned)
        {
            throw new HistoricalUploadConflictException();
        }

        var declaredHash = new ContentHash(upload.NormalizedTextSha256);

        var (content, observedBytes, observedHash) = await ReadAndVerifyAsync(
            body,
            upload.DeclaredBytes,
            options.MaxNormalizedTextBytes,
            cancellationToken);
        if (observedHash != declaredHash)
        {
            throw new HistoricalContentContractException("The observed content digest does not match the declared SHA-256.");
        }

        if (upload.State != HistoricalUploadState.Published)
        {
            var reference = ContentReference.ForVersion(upload.Id);
            var usedBytes = await dbContext.HistoricalUploads
                .Where(item => item.State == HistoricalUploadState.Reserved || item.State == HistoricalUploadState.Published)
                .SumAsync(item => (long?)item.DeclaredBytes, cancellationToken) ?? 0;
            if (usedBytes > options.TotalStorageWatermarkBytes)
            {
                throw new HistoricalWatermarkExceededException();
            }

            await contentStore.StoreAsync(reference, declaredHash, content, cancellationToken);
            upload.Publish(reference.Value, DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new PublishedHistoricalUploadResult(
            upload.Id,
            upload.State,
            upload.DeclaredBytes,
            observedBytes,
            upload.NormalizedTextSha256);
    }

    public async Task<CommitHistoricalUploadResult> CommitAsync(
        Guid uploadId,
        Guid serviceClientId,
        CancellationToken cancellationToken)
    {
        var upload = await dbContext.HistoricalUploads.SingleOrDefaultAsync(
            item => item.Id == uploadId && item.ServiceClientId == serviceClientId,
            cancellationToken) ?? throw new HistoricalUploadNotFoundException();
        if (upload.State == HistoricalUploadState.Committed)
        {
            return new CommitHistoricalUploadResult(
                upload.Id,
                upload.DocumentId!.Value,
                upload.DocumentVersionId!.Value,
                upload.OperationId!.Value,
                upload.State);
        }

        if (upload.State != HistoricalUploadState.Published)
        {
            throw new HistoricalUploadConflictException();
        }

        var now = DateTimeOffset.UtcNow;
        var collection = await dbContext.Collections.SingleOrDefaultAsync(
            item => item.Id == upload.CollectionId && item.ServiceClientId == serviceClientId,
            cancellationToken) ?? throw new HistoricalUploadNotFoundException();

        var hash = new ContentHash(upload.NormalizedTextSha256);
        var reference = new ContentReference(upload.ContentReference ?? ContentReference.ForVersion(upload.Id).Value);
        var document = await dbContext.Documents.Include(item => item.Versions).SingleOrDefaultAsync(
            item => item.CollectionId == upload.CollectionId && item.ExternalReference == upload.SourceDocumentKey,
            cancellationToken);
        var existingVersion = document?.FindVersion(hash);

        if (existingVersion is not null)
        {
            var existingOperation = await dbContext.Operations.SingleOrDefaultAsync(
                item => item.DocumentVersionId == existingVersion.Id,
                cancellationToken);
            if (existingOperation is null)
            {
                existingOperation = Operation.CreatePendingHistorical(existingVersion.Id, now);
                dbContext.Operations.Add(existingOperation);
            }

            if (await dbContext.HistoricalProvenance.SingleOrDefaultAsync(
                item => item.DocumentVersionId == existingVersion.Id,
                cancellationToken) is null)
            {
                dbContext.HistoricalProvenance.Add(CreateProvenance(upload, existingVersion.Id, existingOperation.Id, now));
            }

            upload.Commit(document!.Id, existingVersion.Id, existingOperation.Id, now);
        }
        else
        {
            var isNewDocument = document is null;
            document ??= new Document(Guid.NewGuid(), upload.CollectionId, upload.SourceDocumentKey, now);
            var versionId = Guid.NewGuid();
            var version = document.AddVersion(
                versionId,
                upload.DisplayName ?? upload.SourceDocumentKey,
                hash,
                reference,
                now);
            var operation = Operation.CreatePendingHistorical(version.Id, now);
            if (isNewDocument)
            {
                dbContext.Documents.Add(document);
            }
            else
            {
                dbContext.DocumentVersions.Add(version);
            }

            dbContext.Operations.Add(operation);
            dbContext.HistoricalProvenance.Add(CreateProvenance(upload, version.Id, operation.Id, now));
            upload.Commit(document.Id, version.Id, operation.Id, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new CommitHistoricalUploadResult(
            upload.Id,
            upload.DocumentId!.Value,
            upload.DocumentVersionId!.Value,
            upload.OperationId!.Value,
            upload.State);
    }

    public async Task<GetHistoricalUploadResult> GetAsync(
        Guid uploadId,
        Guid serviceClientId,
        CancellationToken cancellationToken)
    {
        var upload = await dbContext.HistoricalUploads.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == uploadId && item.ServiceClientId == serviceClientId,
            cancellationToken) ?? throw new HistoricalUploadNotFoundException();
        return new GetHistoricalUploadResult(
            upload.Id,
            upload.State,
            upload.SourceDocumentKey,
            upload.NormalizedTextSha256,
            upload.DeclaredBytes,
            upload.DocumentId,
            upload.DocumentVersionId,
            upload.OperationId,
            upload.CorrelationId);
    }

    private static void ValidateReserve(ReserveHistoricalUploadCommand command)
    {
        if (command.ServiceClientId == Guid.Empty || command.CollectionId == Guid.Empty)
        {
            throw new ArgumentException("Service client and collection ids are required.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.SourceDocumentKey) || string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            throw new ArgumentException("Source document key and idempotency key are required.", nameof(command));
        }

        if (command.DeclaredBytes <= 0)
        {
            throw new ArgumentException("Declared bytes must be positive.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.NormalizedTextSha256) ||
            command.NormalizedTextSha256.Length != 64 ||
            !command.NormalizedTextSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The normalized text SHA-256 must be 64 hexadecimal characters.", nameof(command));
        }
    }

    private static string ComputeFingerprint(ReserveHistoricalUploadCommand command) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "\u001F",
            command.CollectionId.ToString("N"),
            command.SourceDocumentKey,
            command.NormalizedTextSha256.ToLowerInvariant(),
            command.DeclaredBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            command.CandidateId?.ToString("N") ?? string.Empty,
            command.ManifestId?.ToString("N") ?? string.Empty,
            command.RunId?.ToString("N") ?? string.Empty))));

    private HistoricalProvenance CreateProvenance(
        HistoricalUpload upload,
        Guid documentVersionId,
        Guid operationId,
        DateTimeOffset ingestedAt) => new(
            Guid.NewGuid(),
            documentVersionId,
            "historical_windows_manifest",
            upload.SourceDocumentKey,
            upload.NormalizedTextSha256,
            upload.DeclaredBytes,
            operationId,
            ingestedAt,
            upload.ManifestId,
            upload.CandidateId,
            upload.RunId,
            upload.SourceRootAlias,
            upload.DisplayName,
            upload.Format);

    private async Task ExpireAbandonedUploadsAsync(Guid serviceClientId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - options.AbandonedUploadExpiry;
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE historical_uploads
            SET "State" = 'Abandoned', "AbandonedAt" = clock_timestamp()
            WHERE "ServiceClientId" = {serviceClientId}
              AND "State" IN ('Reserved', 'Published')
              AND "CreatedAt" < {cutoff}
            """, cancellationToken);
    }

    private static async Task<(byte[] Content, long ObservedBytes, ContentHash ObservedHash)> ReadAndVerifyAsync(
        Stream body,
        long declaredBytes,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
            {
                throw new HistoricalContentTooLargeException(maximumBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        var content = buffer.ToArray();
        if (content.Length != declaredBytes)
        {
            throw new HistoricalContentContractException("The observed content length does not match the declared bytes.");
        }

        return (content, content.Length, ContentHash.FromBytes(content));
    }

    private ReserveHistoricalUploadResult ToReserveResult(HistoricalUpload upload, bool created) => new(
        upload.Id,
        upload.State,
        upload.CorrelationId,
        created,
        options.MaxNormalizedTextBytes,
        options.PerClientPendingQuota,
        options.TotalStorageWatermarkBytes,
        options.AbandonedUploadExpiry);
}
