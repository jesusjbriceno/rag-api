using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Rag.Infrastructure.Migrations;

[DbContext(typeof(IngestionDbContext))]
[Migration("20260829000000_AddHistoricalIngestion")]
public sealed class AddHistoricalIngestion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "WorkloadClass",
            table: "operations",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "RealTime");

        migrationBuilder.AddCheckConstraint(
            name: "CK_operations_WorkloadClass_valid",
            table: "operations",
            sql: "\"WorkloadClass\" IN ('RealTime', 'Historical')");

        migrationBuilder.CreateTable(
            name: "historical_uploads",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ServiceClientId = table.Column<Guid>(type: "uuid", nullable: false),
                CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceDocumentKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                NormalizedTextSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                DeclaredBytes = table.Column<long>(type: "bigint", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CandidateId = table.Column<Guid>(type: "uuid", nullable: true),
                ManifestId = table.Column<Guid>(type: "uuid", nullable: true),
                RunId = table.Column<Guid>(type: "uuid", nullable: true),
                DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                SourceRootAlias = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                ContentReference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                OperationId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CommittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AbandonedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_historical_uploads", upload => upload.Id);
                table.ForeignKey(
                    name: "FK_historical_uploads_service_clients_ServiceClientId",
                    column: upload => upload.ServiceClientId,
                    principalTable: "service_clients",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_historical_uploads_collections_CollectionId",
                    column: upload => upload.CollectionId,
                    principalTable: "collections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.CheckConstraint("CK_historical_uploads_DeclaredBytes_positive", "\"DeclaredBytes\" > 0");
                table.CheckConstraint("CK_historical_uploads_NormalizedTextSha256_hex", "\"NormalizedTextSha256\" ~ '^[0-9a-f]{64}$'");
                table.CheckConstraint("CK_historical_uploads_State_valid", "\"State\" IN ('Reserved', 'Published', 'Committed', 'Abandoned')");
            });

        migrationBuilder.CreateIndex(
            name: "IX_historical_uploads_ServiceClientId_IdempotencyKey",
            table: "historical_uploads",
            columns: new[] { "ServiceClientId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_historical_uploads_State_CreatedAt",
            table: "historical_uploads",
            columns: new[] { "State", "CreatedAt" });

        migrationBuilder.CreateTable(
            name: "historical_provenance",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceKind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                SourceDocumentKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ManifestId = table.Column<Guid>(type: "uuid", nullable: true),
                CandidateId = table.Column<Guid>(type: "uuid", nullable: true),
                RunId = table.Column<Guid>(type: "uuid", nullable: true),
                SourceRootAlias = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                DisplayFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                DeclaredBytes = table.Column<long>(type: "bigint", nullable: false),
                NormalizedTextSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                IngestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RemoteOperationId = table.Column<Guid>(type: "uuid", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_historical_provenance", provenance => provenance.Id);
                table.ForeignKey(
                    name: "FK_historical_provenance_document_versions_DocumentVersionId",
                    column: provenance => provenance.DocumentVersionId,
                    principalTable: "document_versions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.CheckConstraint("CK_historical_provenance_DeclaredBytes_positive", "\"DeclaredBytes\" > 0");
                table.CheckConstraint("CK_historical_provenance_NormalizedTextSha256_hex", "\"NormalizedTextSha256\" ~ '^[0-9a-f]{64}$'");
            });

        migrationBuilder.CreateIndex(
            name: "IX_historical_provenance_DocumentVersionId",
            table: "historical_provenance",
            column: "DocumentVersionId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "historical_provenance");
        migrationBuilder.DropTable(name: "historical_uploads");
        migrationBuilder.DropCheckConstraint(name: "CK_operations_WorkloadClass_valid", table: "operations");
        migrationBuilder.DropColumn(name: "WorkloadClass", table: "operations");
    }
}
