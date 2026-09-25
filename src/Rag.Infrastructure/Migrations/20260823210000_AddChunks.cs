using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Rag.Infrastructure.Migrations;

[DbContext(typeof(IngestionDbContext))]
[Migration("20260823210000_AddChunks")]
public sealed class AddChunks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "chunks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                Ordinal = table.Column<int>(type: "integer", nullable: false),
                Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_chunks", chunk => chunk.Id);
                table.ForeignKey(
                    name: "FK_chunks_document_versions_DocumentVersionId",
                    column: chunk => chunk.DocumentVersionId,
                    principalTable: "document_versions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.CheckConstraint("CK_chunks_Ordinal_positive", "\"Ordinal\" > 0");
                table.CheckConstraint(
                    "CK_chunks_Text_normalized",
                    "char_length(\"Text\") BETWEEN 1 AND 2000 AND \"Text\" = btrim(\"Text\", U&'\\0009\\000A\\000B\\000C\\000D\\0020\\0085\\00A0\\1680\\2000\\2001\\2002\\2003\\2004\\2005\\2006\\2007\\2008\\2009\\200A\\2028\\2029\\202F\\205F\\3000') AND position(E'\\r' in \"Text\") = 0");
            });

        migrationBuilder.CreateIndex(
            name: "IX_chunks_DocumentVersionId_Ordinal",
            table: "chunks",
            columns: new[] { "DocumentVersionId", "Ordinal" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "chunks");
}
