using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Rag.Infrastructure.Migrations;

[DbContext(typeof(IngestionDbContext))]
[Migration("20260824150000_AddCollectionOwnership")]
public sealed class AddCollectionOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ServiceClientId",
            table: "collections",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddForeignKey(
            name: "FK_collections_service_clients_ServiceClientId",
            table: "collections",
            column: "ServiceClientId",
            principalTable: "service_clients",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_collections_service_clients_ServiceClientId", table: "collections");
        migrationBuilder.DropColumn(name: "ServiceClientId", table: "collections");
    }
}
