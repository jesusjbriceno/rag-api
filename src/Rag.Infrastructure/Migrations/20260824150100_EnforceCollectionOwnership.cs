using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Rag.Infrastructure.Migrations;

[DbContext(typeof(IngestionDbContext))]
[Migration("20260824150100_EnforceCollectionOwnership")]
public sealed class EnforceCollectionOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM collections WHERE "ServiceClientId" IS NULL) THEN
                    RAISE EXCEPTION 'Collection ownership enforcement is blocked because at least one collection is unowned. Run Rag.Operator collections list-unowned, assign every row with Rag.Operator collections assign-owner <collection-id> <service-client-id>, then rerun the migration.';
                END IF;
            END $$;
            """);
        migrationBuilder.AlterColumn<Guid>(
            name: "ServiceClientId",
            table: "collections",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);
        migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_collections_ServiceClientId_NormalizedName\" ON collections (\"ServiceClientId\", lower(btrim(\"Name\")));");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX \"IX_collections_ServiceClientId_NormalizedName\";");
        migrationBuilder.AlterColumn<Guid>(
            name: "ServiceClientId",
            table: "collections",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");
    }
}
