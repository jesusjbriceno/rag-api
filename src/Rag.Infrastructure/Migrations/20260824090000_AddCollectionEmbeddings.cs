using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

namespace Rag.Infrastructure.Migrations;

[DbContext(typeof(IngestionDbContext))]
[Migration("20260824090000_AddCollectionEmbeddings")]
public sealed class AddCollectionEmbeddings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");
        migrationBuilder.AddColumn<string>(name: "EmbeddingProvider", table: "collections", type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>(name: "EmbeddingModel", table: "collections", type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "EmbeddingVersion", table: "collections", type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<int>(name: "EmbeddingDimensions", table: "collections", type: "integer", nullable: true);
        migrationBuilder.Sql("UPDATE collections SET \"EmbeddingProvider\" = 'ollama', \"EmbeddingModel\" = 'qwen3-embedding:0.6b', \"EmbeddingVersion\" = '0.6b', \"EmbeddingDimensions\" = 1024;");
        migrationBuilder.AlterColumn<string>(name: "EmbeddingProvider", table: "collections", type: "character varying(100)", maxLength: 100, nullable: false, oldClrType: typeof(string), oldType: "character varying(100)", oldMaxLength: 100, oldNullable: true);
        migrationBuilder.AlterColumn<string>(name: "EmbeddingModel", table: "collections", type: "character varying(200)", maxLength: 200, nullable: false, oldClrType: typeof(string), oldType: "character varying(200)", oldMaxLength: 200, oldNullable: true);
        migrationBuilder.AlterColumn<string>(name: "EmbeddingVersion", table: "collections", type: "character varying(100)", maxLength: 100, nullable: false, oldClrType: typeof(string), oldType: "character varying(100)", oldMaxLength: 100, oldNullable: true);
        migrationBuilder.AlterColumn<int>(name: "EmbeddingDimensions", table: "collections", type: "integer", nullable: false, oldClrType: typeof(int), oldType: "integer", oldNullable: true);
        migrationBuilder.AddCheckConstraint(name: "CK_collections_EmbeddingDimensions_positive", table: "collections", sql: "\"EmbeddingDimensions\" > 0");
        migrationBuilder.CreateTable(
            name: "chunk_embeddings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                ChunkId = table.Column<Guid>(type: "uuid", nullable: false),
                Values = table.Column<Vector>(type: "vector", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_chunk_embeddings", embedding => embedding.Id);
                table.ForeignKey(name: "FK_chunk_embeddings_collections_CollectionId", column: embedding => embedding.CollectionId, principalTable: "collections", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey(name: "FK_chunk_embeddings_chunks_ChunkId", column: embedding => embedding.ChunkId, principalTable: "chunks", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "IX_chunk_embeddings_ChunkId", table: "chunk_embeddings", column: "ChunkId", unique: true);
        migrationBuilder.Sql(
            """
            CREATE FUNCTION enforce_chunk_embedding_profile() RETURNS trigger AS $$
            DECLARE
                expected_dimensions integer;
            BEGIN
                SELECT "EmbeddingDimensions" INTO expected_dimensions
                FROM collections
                WHERE "Id" = NEW."CollectionId";

                IF expected_dimensions IS NULL OR vector_dims(NEW."Values") <> expected_dimensions THEN
                    RAISE EXCEPTION 'Embedding dimensions must match the collection profile'
                        USING ERRCODE = '23514', CONSTRAINT = 'CK_chunk_embeddings_Dimensions_match_collection';
                END IF;

                PERFORM 1
                    FROM chunks
                    JOIN document_versions ON document_versions."Id" = chunks."DocumentVersionId"
                    JOIN documents ON documents."Id" = document_versions."DocumentId"
                    WHERE chunks."Id" = NEW."ChunkId"
                      AND documents."CollectionId" = NEW."CollectionId"
                    FOR UPDATE OF documents;

                IF NOT FOUND THEN
                    RAISE EXCEPTION 'Embedding collection must own its chunk'
                        USING ERRCODE = '23514', CONSTRAINT = 'CK_chunk_embeddings_Collection_matches_chunk';
                END IF;

                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER "CK_chunk_embeddings_Profile_valid"
            BEFORE INSERT OR UPDATE OF "CollectionId", "ChunkId", "Values" ON chunk_embeddings
            FOR EACH ROW EXECUTE FUNCTION enforce_chunk_embedding_profile();

            CREATE FUNCTION prevent_collection_embedding_profile_change() RETURNS trigger AS $$
            BEGIN
                IF NEW."EmbeddingProvider" IS DISTINCT FROM OLD."EmbeddingProvider"
                   OR NEW."EmbeddingModel" IS DISTINCT FROM OLD."EmbeddingModel"
                   OR NEW."EmbeddingVersion" IS DISTINCT FROM OLD."EmbeddingVersion"
                   OR NEW."EmbeddingDimensions" IS DISTINCT FROM OLD."EmbeddingDimensions" THEN
                    RAISE EXCEPTION 'Collection embedding profiles are immutable'
                        USING ERRCODE = '23514', CONSTRAINT = 'CK_collections_EmbeddingProfile_immutable';
                END IF;

                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER "CK_collections_EmbeddingProfile_immutable"
            BEFORE UPDATE OF "EmbeddingProvider", "EmbeddingModel", "EmbeddingVersion", "EmbeddingDimensions" ON collections
            FOR EACH ROW EXECUTE FUNCTION prevent_collection_embedding_profile_change();

            CREATE FUNCTION prevent_embedded_document_reparenting() RETURNS trigger AS $$
            BEGIN
                IF NEW."CollectionId" IS DISTINCT FROM OLD."CollectionId" AND EXISTS (
                    SELECT 1
                    FROM document_versions
                    JOIN chunks ON chunks."DocumentVersionId" = document_versions."Id"
                    JOIN chunk_embeddings ON chunk_embeddings."ChunkId" = chunks."Id"
                    WHERE document_versions."DocumentId" = OLD."Id") THEN
                    RAISE EXCEPTION 'A document with embedded chunks cannot change collections'
                        USING ERRCODE = '23514', CONSTRAINT = 'CK_documents_Embedded_chunks_collection_immutable';
                END IF;

                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER "CK_documents_Embedded_chunks_collection_immutable"
            BEFORE UPDATE OF "CollectionId" ON documents
            FOR EACH ROW EXECUTE FUNCTION prevent_embedded_document_reparenting();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER \"CK_documents_Embedded_chunks_collection_immutable\" ON documents;");
        migrationBuilder.Sql("DROP FUNCTION prevent_embedded_document_reparenting();");
        migrationBuilder.Sql("DROP TRIGGER \"CK_collections_EmbeddingProfile_immutable\" ON collections;");
        migrationBuilder.Sql("DROP FUNCTION prevent_collection_embedding_profile_change();");
        migrationBuilder.Sql("DROP TRIGGER \"CK_chunk_embeddings_Profile_valid\" ON chunk_embeddings;");
        migrationBuilder.Sql("DROP FUNCTION enforce_chunk_embedding_profile();");
        migrationBuilder.DropTable(name: "chunk_embeddings");
        migrationBuilder.DropCheckConstraint(name: "CK_collections_EmbeddingDimensions_positive", table: "collections");
        migrationBuilder.DropColumn(name: "EmbeddingDimensions", table: "collections");
        migrationBuilder.DropColumn(name: "EmbeddingVersion", table: "collections");
        migrationBuilder.DropColumn(name: "EmbeddingModel", table: "collections");
        migrationBuilder.DropColumn(name: "EmbeddingProvider", table: "collections");
        migrationBuilder.Sql("DROP EXTENSION IF EXISTS vector;");
    }
}
