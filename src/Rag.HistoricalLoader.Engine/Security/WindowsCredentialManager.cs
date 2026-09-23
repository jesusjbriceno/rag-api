using System.Runtime.InteropServices;

namespace Rag.HistoricalLoader.Engine.Security;

/// <summary>Reusable RAG service-client credential (opaque key id + one-time secret).</summary>
public sealed record RagServiceCredential(string KeyId, string Secret)
{
    public override string ToString() => $"{nameof(RagServiceCredential)}(KeyId: {KeyId}, Secret: <redacted>)";
}

/// <summary>Cloudflare Access service-token credential (client id + client secret).</summary>
public sealed record CloudflareServiceToken(string ClientId, string ClientSecret)
{
    public override string ToString() => $"{nameof(CloudflareServiceToken)}(ClientId: {ClientId}, ClientSecret: <redacted>)";
}

/// <summary>A single generic credential entry (username + secret) read back from the OS store.</summary>
public sealed record StoredCredential(string UserName, string Secret)
{
    public override string ToString() => $"{nameof(StoredCredential)}(UserName: {UserName}, Secret: <redacted>)";
}

/// <summary>
/// Narrow Windows Credential Manager abstraction. The real implementation is a Windows-only P/Invoke
/// adapter; tests substitute a fake so no test ever touches the real OS credential store.
/// </summary>
public interface IWindowsCredentialStore
{
    void Write(string target, string userName, string secret);

    StoredCredential? Read(string target);

    void Delete(string target);
}

/// <summary>
/// Windows Credential Manager adapter. RAG and Cloudflare credentials are stored in separate generic
/// credential entries so the two secrets rotate and revoke independently. The real store fails closed
/// with <see cref="PlatformNotSupportedException"/> on non-Windows platforms.
/// </summary>
public sealed class WindowsCredentialManager : IWindowsCredentialStore
{
    public const string DefaultRagEntryName = "rag-api/historical-loader/rag-service-client";

    public const string DefaultCloudflareEntryName = "rag-api/historical-loader/cloudflare-service-token";

    private readonly IWindowsCredentialStore _store;
    private readonly string _ragEntryName;
    private readonly string _cloudflareEntryName;

    public WindowsCredentialManager()
        : this(new WindowsNativeCredentialStore(), DefaultRagEntryName, DefaultCloudflareEntryName)
    {
    }

    public WindowsCredentialManager(IWindowsCredentialStore store, string ragEntryName, string cloudflareEntryName)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ragEntryName = ragEntryName;
        _cloudflareEntryName = cloudflareEntryName;
    }

    public void Write(string target, string userName, string secret) => _store.Write(target, userName, secret);

    public StoredCredential? Read(string target) => _store.Read(target);

    public void Delete(string target) => _store.Delete(target);

    public void SaveRagCredential(RagServiceCredential credential) => Write(_ragEntryName, credential.KeyId, credential.Secret);

    public RagServiceCredential? ReadRagCredential()
        => Read(_ragEntryName) is { } stored ? new RagServiceCredential(stored.UserName, stored.Secret) : null;

    public void DeleteRagCredential() => Delete(_ragEntryName);

    public void SaveCloudflareToken(CloudflareServiceToken token) => Write(_cloudflareEntryName, token.ClientId, token.ClientSecret);

    public CloudflareServiceToken? ReadCloudflareToken()
        => Read(_cloudflareEntryName) is { } stored ? new CloudflareServiceToken(stored.UserName, stored.Secret) : null;

    public void DeleteCloudflareToken() => Delete(_cloudflareEntryName);
}

/// <summary>Windows-only P/Invoke implementation of <see cref="IWindowsCredentialStore"/>.</summary>
internal sealed class WindowsNativeCredentialStore : IWindowsCredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public void Write(string target, string userName, string secret)
    {
        EnsureWindows();
        var credential = new CREDENTIAL
        {
            Type = CredTypeGeneric,
            TargetName = target,
            CredentialBlob = Marshal.StringToCoTaskMemUni(secret),
            CredentialBlobSize = (uint)(secret.Length * sizeof(char)),
            Persist = CredPersistLocalMachine,
            UserName = userName,
        };

        try
        {
            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"CredWrite failed with error {Marshal.GetLastWin32Error()}.");
            }
        }
        finally
        {
            if (credential.CredentialBlob != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(credential.CredentialBlob);
            }
        }
    }

    public StoredCredential? Read(string target)
    {
        EnsureWindows();
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            var secret = credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)(credential.CredentialBlobSize / sizeof(char)));
            return new StoredCredential(credential.UserName ?? string.Empty, secret ?? string.Empty);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Delete(string target)
    {
        EnsureWindows();
        CredDelete(target, CredTypeGeneric, 0);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
