using Microsoft.EntityFrameworkCore;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class IngestionDbContext(DbContextOptions<IngestionDbContext> options) : DbContext(options)
{
    public DbSet<Collection> Collections => Set<Collection>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();

    public DbSet<Operation> Operations => Set<Operation>();

    public DbSet<Chunk> Chunks => Set<Chunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Collection>(builder =>
        {
            builder.ToTable("collections");
            builder.HasKey(collection => collection.Id);
            builder.Property(collection => collection.Name).HasMaxLength(200).IsRequired();
            builder.Property(collection => collection.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<Document>(builder =>
        {
            builder.ToTable("documents");
            builder.HasKey(document => document.Id);
            builder.Property(document => document.ExternalReference).HasMaxLength(512);
            builder.Property(document => document.CurrentVersionId).IsRequired();
            builder.Property(document => document.CreatedAt).IsRequired();
            builder.HasIndex(document => new { document.CollectionId, document.ExternalReference })
                .IsUnique()
                .HasFilter("\"ExternalReference\" IS NOT NULL");
            builder.HasOne<Collection>()
                .WithMany()
                .HasForeignKey(document => document.CollectionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasMany(document => document.Versions)
                .WithOne()
                .HasForeignKey(version => version.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentVersion>(builder =>
        {
            builder.ToTable("document_versions");
            builder.HasKey(version => version.Id);
            builder.Property(version => version.Number).IsRequired();
            builder.Property(version => version.FileName).HasMaxLength(255).IsRequired();
            builder.Property(version => version.MimeType).HasMaxLength(100).IsRequired();
            builder.Property(version => version.ContentHash)
                .HasConversion(hash => hash.Value, value => new ContentHash(value))
                .HasMaxLength(64)
                .IsFixedLength()
                .IsRequired();
            builder.Property(version => version.ContentReference)
                .HasConversion(reference => reference.Value, value => new ContentReference(value))
                .HasMaxLength(300)
                .IsRequired();
            builder.Property(version => version.CreatedAt).IsRequired();
            builder.HasIndex(version => new { version.DocumentId, version.Number }).IsUnique();
            builder.ToTable(table => table.HasCheckConstraint(
                "CK_document_versions_Number_positive",
                "\"Number\" > 0"));
            builder.ToTable(table => table.HasCheckConstraint(
                "CK_document_versions_ContentHash_normalized",
                "\"ContentHash\" ~ '^[0-9a-f]{64}$'"));
        });

        modelBuilder.Entity<Operation>(builder =>
        {
            builder.ToTable("operations");
            builder.HasKey(operation => operation.Id);
            builder.Property(operation => operation.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            builder.Property(operation => operation.FailureStage).HasMaxLength(100);
            builder.Property(operation => operation.FailureMessage).HasMaxLength(2_000);
            builder.Property(operation => operation.LeaseOwner).HasMaxLength(200);
            builder.Property(operation => operation.LeaseExpiresAt);
            builder.Property(operation => operation.CreatedAt).IsRequired();
            builder.HasIndex(operation => operation.DocumentVersionId).IsUnique();
            builder.HasIndex(operation => new { operation.Status, operation.LeaseExpiresAt, operation.CreatedAt });
            builder.ToTable(table => table.HasCheckConstraint(
                "CK_operations_Status_valid",
                "\"Status\" IN ('Pending', 'Running', 'Succeeded', 'Failed')"));
            builder.ToTable(table => table.HasCheckConstraint(
                "CK_operations_Lease_valid",
                "(\"Status\" = 'Running' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL) OR (\"Status\" IN ('Pending', 'Succeeded', 'Failed') AND \"LeaseOwner\" IS NULL AND \"LeaseExpiresAt\" IS NULL)"));
            builder.HasOne<DocumentVersion>()
                .WithMany()
                .HasForeignKey(operation => operation.DocumentVersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Chunk>(builder =>
        {
            builder.ToTable("chunks");
            builder.HasKey(chunk => chunk.Id);
            builder.Property(chunk => chunk.Ordinal).IsRequired();
            builder.Property(chunk => chunk.Text).HasMaxLength(2_000).IsRequired();
            builder.HasIndex(chunk => new { chunk.DocumentVersionId, chunk.Ordinal }).IsUnique();
            builder.ToTable(table => table.HasCheckConstraint(
                "CK_chunks_Ordinal_positive",
                "\"Ordinal\" > 0"));
            builder.ToTable(table => table.HasCheckConstraint(
                "CK_chunks_Text_normalized",
                "char_length(\"Text\") BETWEEN 1 AND 2000 AND \"Text\" = btrim(\"Text\", U&'\\0009\\000A\\000B\\000C\\000D\\0020\\0085\\00A0\\1680\\2000\\2001\\2002\\2003\\2004\\2005\\2006\\2007\\2008\\2009\\200A\\2028\\2029\\202F\\205F\\3000') AND position(E'\\r' in \"Text\") = 0"));
            builder.HasOne<DocumentVersion>()
                .WithMany()
                .HasForeignKey(chunk => chunk.DocumentVersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
