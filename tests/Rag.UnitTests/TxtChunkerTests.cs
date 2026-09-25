using Rag.Application;

namespace Rag.UnitTests;

public sealed class TxtChunkerTests
{
    [Fact]
    public void Normalizes_utf8_text_structure_before_chunking()
    {
        var documentVersionId = Guid.NewGuid();
        var chunks = new TxtChunker().Chunk(documentVersionId, "  cafe\u0301  line\r\ncontinued\ttext\r\n\r\n second paragraph  ");

        var chunk = Assert.Single(chunks);
        Assert.Equal("café line continued text\n\nsecond paragraph", chunk.Text);
        Assert.Equal(documentVersionId, chunk.DocumentVersionId);
        Assert.Equal(1, chunk.Ordinal);
    }

    [Fact]
    public void Prefers_paragraph_boundaries_and_preserves_deterministic_overlap_and_provenance()
    {
        var documentVersionId = Guid.NewGuid();
        var firstParagraph = new string('a', 1_800);
        var secondParagraph = new string('b', 500);

        var chunks = new TxtChunker().Chunk(documentVersionId, $"{firstParagraph}\n\n{secondParagraph}");

        Assert.Equal(2, chunks.Count);
        Assert.Equal(firstParagraph, chunks[0].Text);
        Assert.Equal($"{new string('a', TxtChunker.OverlapCharacters)}\n\n{secondParagraph}", chunks[1].Text);
        Assert.Collection(
            chunks,
            chunk =>
            {
                Assert.Equal(documentVersionId, chunk.DocumentVersionId);
                Assert.Equal(1, chunk.Ordinal);
            },
            chunk =>
            {
                Assert.Equal(documentVersionId, chunk.DocumentVersionId);
                Assert.Equal(2, chunk.Ordinal);
            });
        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.EnumerateRunes().Count(), 1, TxtChunker.MaximumChunkCharacters));
    }
}
