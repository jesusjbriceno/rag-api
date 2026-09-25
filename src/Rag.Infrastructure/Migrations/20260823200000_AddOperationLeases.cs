using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Rag.Infrastructure.Migrations;

[DbContext(typeof(IngestionDbContext))]
[Migration("20260823200000_AddOperationLeases")]
public sealed class AddOperationLeases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LeaseOwner",
            table: "operations",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LeaseExpiresAt",
            table: "operations",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_operations_Status_LeaseExpiresAt_CreatedAt",
            table: "operations",
            columns: new[] { "Status", "LeaseExpiresAt", "CreatedAt" });
        migrationBuilder.Sql(
            "UPDATE operations SET \"Status\" = 'Pending', \"StartedAt\" = NULL WHERE \"Status\" = 'Running';");
        migrationBuilder.AddCheckConstraint(
            name: "CK_operations_Lease_valid",
            table: "operations",
            sql: "(\"Status\" = 'Running' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL) OR (\"Status\" IN ('Pending', 'Succeeded', 'Failed') AND \"LeaseOwner\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "CK_operations_Lease_valid", table: "operations");
        migrationBuilder.DropIndex(name: "IX_operations_Status_LeaseExpiresAt_CreatedAt", table: "operations");
        migrationBuilder.DropColumn(name: "LeaseOwner", table: "operations");
        migrationBuilder.DropColumn(name: "LeaseExpiresAt", table: "operations");
    }
}
