using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Rag.Infrastructure.Migrations;

[DbContext(typeof(IngestionDbContext))]
[Migration("20260827000000_AddAdminPlane")]
public sealed class AddAdminPlane : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Description",
            table: "service_clients",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Description",
            table: "client_credentials",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastRotatedAt",
            table: "client_credentials",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "admin_audit_events",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ActorSubject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                AppId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Outcome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                TargetType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                TargetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                Operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                AllowlistedJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_admin_audit_events", auditEvent => auditEvent.Id));

        migrationBuilder.CreateTable(
            name: "admin_operations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AppId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Fingerprint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                State = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                SafeResult = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_admin_operations", operation => operation.Id));

        migrationBuilder.CreateTable(
            name: "admin_assertion_replays",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Issuer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Jti = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                AppId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_admin_assertion_replays", replay => replay.Id));

        migrationBuilder.CreateIndex(
            name: "IX_admin_audit_events_OccurredAt_Id",
            table: "admin_audit_events",
            columns: new[] { "OccurredAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_admin_audit_events_TargetType_TargetId_OccurredAt",
            table: "admin_audit_events",
            columns: new[] { "TargetType", "TargetId", "OccurredAt" });

        migrationBuilder.CreateIndex(
            name: "IX_admin_operations_AppId_IdempotencyKey",
            table: "admin_operations",
            columns: new[] { "AppId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_admin_operations_CreatedAt",
            table: "admin_operations",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_admin_assertion_replays_Issuer_Jti",
            table: "admin_assertion_replays",
            columns: new[] { "Issuer", "Jti" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_admin_assertion_replays_ExpiresAt",
            table: "admin_assertion_replays",
            column: "ExpiresAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "admin_assertion_replays");
        migrationBuilder.DropTable(name: "admin_operations");
        migrationBuilder.DropTable(name: "admin_audit_events");
        migrationBuilder.DropColumn(name: "Description", table: "client_credentials");
        migrationBuilder.DropColumn(name: "LastRotatedAt", table: "client_credentials");
        migrationBuilder.DropColumn(name: "Description", table: "service_clients");
    }
}
