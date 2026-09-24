using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Rag.Infrastructure.Migrations;

[DbContext(typeof(IngestionDbContext))]
[Migration("20260828000000_AddServiceClientGrants")]
public sealed class AddServiceClientGrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "service_client_grants",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ServiceClientId = table.Column<Guid>(type: "uuid", nullable: false),
                Scopes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                Version = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_service_client_grants", grant => grant.Id);
                table.ForeignKey(
                    name: "FK_service_client_grants_service_clients_ServiceClientId",
                    column: grant => grant.ServiceClientId,
                    principalTable: "service_clients",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_service_client_grants_collections_CollectionId",
                    column: grant => grant.CollectionId,
                    principalTable: "collections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.CheckConstraint("CK_service_client_grants_version_positive", "\"Version\" > 0");
            });

        migrationBuilder.CreateIndex(
            name: "IX_service_client_grants_ServiceClientId",
            table: "service_client_grants",
            column: "ServiceClientId",
            unique: true);

        migrationBuilder.Sql(
            """
            INSERT INTO service_client_grants ("Id", "ServiceClientId", "Scopes", "CollectionId", "Version", "CreatedAt")
            SELECT gen_random_uuid(), "Id", '', NULL, 1, now()
            FROM service_clients;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "service_client_grants");
    }
}
