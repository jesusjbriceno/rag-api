using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Rag.Infrastructure.Migrations;

[DbContext(typeof(IngestionDbContext))]
[Migration("20260823193000_InitialIngestion")]
public sealed class InitialIngestion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "collections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_collections", collection => collection.Id));

        migrationBuilder.CreateTable(
            name: "documents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_documents", document => document.Id);
                table.ForeignKey(
                    name: "FK_documents_collections_CollectionId",
                    column: document => document.CollectionId,
                    principalTable: "collections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "document_versions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                Number = table.Column<int>(type: "integer", nullable: false),
                FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                ContentHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                ContentReference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_document_versions", version => version.Id);
                table.ForeignKey(
                    name: "FK_document_versions_documents_DocumentId",
                    column: version => version.DocumentId,
                    principalTable: "documents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "operations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FailureStage = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operations", operation => operation.Id);
                table.ForeignKey(
                    name: "FK_operations_document_versions_DocumentVersionId",
                    column: operation => operation.DocumentVersionId,
                    principalTable: "document_versions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_document_versions_DocumentId_Number",
            table: "document_versions",
            columns: new[] { "DocumentId", "Number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_documents_CollectionId_ExternalReference",
            table: "documents",
            columns: new[] { "CollectionId", "ExternalReference" },
            unique: true,
            filter: "\"ExternalReference\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_operations_DocumentVersionId",
            table: "operations",
            column: "DocumentVersionId",
            unique: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_document_versions_Number_positive",
            table: "document_versions",
            sql: "\"Number\" > 0");
        migrationBuilder.AddCheckConstraint(
            name: "CK_document_versions_ContentHash_normalized",
            table: "document_versions",
            sql: "\"ContentHash\" ~ '^[0-9a-f]{64}$'");
        migrationBuilder.AddCheckConstraint(
            name: "CK_operations_Status_valid",
            table: "operations",
            sql: "\"Status\" IN ('Pending', 'Running', 'Succeeded', 'Failed')");

        migrationBuilder.Sql(
            """
            CREATE FUNCTION enforce_document_current_version() RETURNS trigger AS $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM document_versions
                    WHERE "Id" = NEW."CurrentVersionId"
                      AND "DocumentId" = NEW."Id") THEN
                    RAISE EXCEPTION 'CurrentVersionId must reference a version of the same document'
                        USING ERRCODE = '23514', CONSTRAINT = 'CK_documents_CurrentVersion_valid';
                END IF;

                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE FUNCTION prevent_current_document_version_removal_or_reparenting() RETURNS trigger AS $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM documents
                    WHERE "Id" = OLD."DocumentId"
                      AND "CurrentVersionId" = OLD."Id") THEN
                    RAISE EXCEPTION 'A document current version cannot be deleted'
                        USING ERRCODE = '23514', CONSTRAINT = 'CK_documents_CurrentVersion_valid';
                END IF;

                RETURN OLD;
            END;
            $$ LANGUAGE plpgsql;

            CREATE CONSTRAINT TRIGGER "CK_documents_CurrentVersion_valid"
            AFTER INSERT OR UPDATE OF "CurrentVersionId" ON documents
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION enforce_document_current_version();

            CREATE CONSTRAINT TRIGGER "CK_document_versions_CurrentVersion_not_deleted"
            AFTER DELETE ON document_versions
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION prevent_current_document_version_removal_or_reparenting();

            CREATE CONSTRAINT TRIGGER "CK_document_versions_CurrentVersion_not_reparented"
            AFTER UPDATE OF "DocumentId" ON document_versions
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION prevent_current_document_version_removal_or_reparenting();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "operations");
        migrationBuilder.DropTable(name: "document_versions");
        migrationBuilder.DropTable(name: "documents");
        migrationBuilder.DropTable(name: "collections");
        migrationBuilder.Sql("DROP FUNCTION prevent_current_document_version_removal_or_reparenting();");
        migrationBuilder.Sql("DROP FUNCTION enforce_document_current_version();");
    }
}
