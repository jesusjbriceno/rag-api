using Rag.HistoricalLoader.Engine.Security;

namespace Rag.HistoricalLoader.UnitTests.Security;

public sealed class WindowsCredentialManagerTests
{
    private sealed class FakeStore : IWindowsCredentialStore
    {
        private readonly Dictionary<string, StoredCredential> _entries = new(StringComparer.Ordinal);

        public void Write(string target, string userName, string secret)
            => _entries[target] = new StoredCredential(userName, secret);

        public StoredCredential? Read(string target)
            => _entries.TryGetValue(target, out var value) ? value : null;

        public void Delete(string target) => _entries.Remove(target);
    }

    [Fact]
    public void Rag_and_cloudflare_credentials_use_separate_entries()
    {
        var store = new FakeStore();
        var manager = new WindowsCredentialManager(store, "rag-entry", "cloudflare-entry");

        manager.SaveRagCredential(new RagServiceCredential("key-id", "rag-secret"));
        manager.SaveCloudflareToken(new CloudflareServiceToken("cf-id", "cf-secret"));

        var rag = manager.ReadRagCredential();
        var cf = manager.ReadCloudflareToken();

        Assert.Equal("key-id", rag!.KeyId);
        Assert.Equal("rag-secret", rag.Secret);
        Assert.Equal("cf-id", cf!.ClientId);
        Assert.Equal("cf-secret", cf.ClientSecret);
        Assert.NotEqual("rag-entry", "cloudflare-entry");
    }

    [Fact]
    public void Credential_rotation_overwrites_old_rag_entry_and_keeps_cloudflare_isolated()
    {
        var store = new FakeStore();
        var manager = new WindowsCredentialManager(store, "rag-entry", "cloudflare-entry");

        manager.SaveRagCredential(new RagServiceCredential("old-key", "old-secret"));
        manager.SaveCloudflareToken(new CloudflareServiceToken("cf-id", "cf-secret"));

        manager.SaveRagCredential(new RagServiceCredential("new-key", "new-secret"));

        var rag = manager.ReadRagCredential();
        Assert.Equal("new-key", rag!.KeyId);
        Assert.Equal("new-secret", rag.Secret);
        Assert.Equal("cf-id", manager.ReadCloudflareToken()!.ClientId);
    }

    [Fact]
    public void Deleting_rag_entry_does_not_remove_cloudflare_entry()
    {
        var store = new FakeStore();
        var manager = new WindowsCredentialManager(store, "rag-entry", "cloudflare-entry");

        manager.SaveRagCredential(new RagServiceCredential("key-id", "rag-secret"));
        manager.SaveCloudflareToken(new CloudflareServiceToken("cf-id", "cf-secret"));

        manager.DeleteRagCredential();

        Assert.Null(manager.ReadRagCredential());
        Assert.NotNull(manager.ReadCloudflareToken());
    }

    [Fact]
    public void Real_store_fails_closed_on_non_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var manager = new WindowsCredentialManager();

        Assert.Throws<PlatformNotSupportedException>(() => manager.Write("target", "user", "secret"));
        Assert.Throws<PlatformNotSupportedException>(() => manager.Read("target"));
        Assert.Throws<PlatformNotSupportedException>(() => manager.Delete("target"));
    }
}
