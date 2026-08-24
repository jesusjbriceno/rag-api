using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Rag.Infrastructure.Migrations;

[DbContext(typeof(IngestionDbContext))]
[Migration("20260824140000_AddServiceClientCredentials")]
public sealed class AddServiceClientCredentials : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "service_clients",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_service_clients", client => client.Id));

        migrationBuilder.CreateTable(
            name: "client_credentials",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ServiceClientId = table.Column<Guid>(type: "uuid", nullable: false),
                KeyId = table.Column<string>(type: "character varying(27)", maxLength: 27, nullable: false),
                SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                SecretSalt = table.Column<byte[]>(type: "bytea", nullable: false),
                HashVersion = table.Column<int>(type: "integer", nullable: false),
                Version = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_client_credentials", credential => credential.Id);
                table.ForeignKey(
                    name: "FK_client_credentials_service_clients_ServiceClientId",
                    column: credential => credential.ServiceClientId,
                    principalTable: "service_clients",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.CheckConstraint("CK_client_credentials_KeyId_format", "\"KeyId\" ~ '^[A-Za-z0-9_-]{27}$'");
                table.CheckConstraint("CK_client_credentials_secret_material", "octet_length(\"SecretHash\") = 32 AND octet_length(\"SecretSalt\") = 16 AND \"HashVersion\" > 0");
                table.CheckConstraint("CK_client_credentials_version_positive", "\"Version\" > 0");
                table.CheckConstraint("CK_client_credentials_status_valid", "\"Status\" IN ('Active', 'Revoked')");
            });

        migrationBuilder.CreateIndex(name: "IX_service_clients_Name", table: "service_clients", column: "Name", unique: true);
        migrationBuilder.CreateIndex(name: "IX_client_credentials_KeyId", table: "client_credentials", column: "KeyId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_client_credentials_ServiceClientId_Status", table: "client_credentials", columns: new[] { "ServiceClientId", "Status" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "client_credentials");
        migrationBuilder.DropTable(name: "service_clients");
    }
}
