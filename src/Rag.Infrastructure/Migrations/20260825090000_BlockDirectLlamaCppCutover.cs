using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Rag.Infrastructure.Migrations;

[DbContext(typeof(IngestionDbContext))]
[Migration("20260825090000_BlockDirectLlamaCppCutover")]
public sealed class BlockDirectLlamaCppCutover : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM collections) OR EXISTS (SELECT 1 FROM chunk_embeddings) THEN
                    RAISE EXCEPTION 'Direct llama.cpp cutover is blocked because existing collection or chunk embedding data was found. A clone-and-reindex release is required for existing Ollama data.';
                END IF;
            END
            $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
