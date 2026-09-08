using Rag.Companion.Adapters;

namespace Rag.Companion.Tests;

public sealed class NormalizerTests
{
    [Fact]
    public void Normalize_strips_leading_byte_order_mark()
    {
        Assert.Equal("Hello\n", Normalizer.Normalize("\uFEFFHello"));
    }

    [Fact]
    public void Normalize_maps_crlf_and_lone_cr_to_lf()
    {
        Assert.Equal("a\nb\nc\n", Normalizer.Normalize("a\r\nb\rc"));
    }

    [Fact]
    public void Normalize_applies_nfc_composition()
    {
        // U+0065 + U+0301 (e + combining acute) composes to U+00E9 (é).
        Assert.Equal("\u00e9\n", Normalizer.Normalize("e\u0301"));
    }

    [Fact]
    public void Normalize_preserves_internal_and_trailing_whitespace()
    {
        Assert.Equal("  spaced  \t \n", Normalizer.Normalize("  spaced  \t "));
    }

    [Fact]
    public void Normalize_guarantees_single_terminal_lf_on_non_empty_text()
    {
        Assert.Equal("text\n", Normalizer.Normalize("text"));
        Assert.Equal("text\n", Normalizer.Normalize("text\n")); // already terminated stays single-LF
    }

    [Fact]
    public void Normalize_leaves_empty_text_empty()
    {
        Assert.Equal(string.Empty, Normalizer.Normalize(string.Empty));
    }

    [Fact]
    public void Normalize_equalizes_bom_cr_crlf_and_nfc_variants()
    {
        var withBomAndCrlf = Normalizer.Normalize("\uFEFFcaf\u00e9\r\nline2");
        var decomposedAndLf = Normalizer.Normalize("cafe\u0301\nline2");

        Assert.Equal(withBomAndCrlf, decomposedAndLf);
    }
}
