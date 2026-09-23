using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Rag.AdminApp;
using Rag.Infrastructure;

namespace Rag.AdminApp.Host.Configuration;

public sealed class CloudflareAccessKeyHostOptions
{
    public string? KeyId { get; set; }

    public string? PublicKeyPem { get; set; }
}

public sealed class CloudflareAccessHostOptions : IValidateOptions<CloudflareAccessHostOptions>
{
    public const string SectionName = "CloudflareAccess";

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public List<CloudflareAccessKeyHostOptions> Keys { get; set; } = [];

    public ValidateOptionsResult Validate(string? name, CloudflareAccessHostOptions options) =>
        ValidateCore(options);

    public CloudflareAccessOptions ToOptions() => new(
        Issuer!,
        Audience!,
        Keys.Select(key => new CloudflareAccessKey(key.KeyId!, key.PublicKeyPem!)).ToArray());

    private static ValidateOptionsResult ValidateCore(CloudflareAccessHostOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            return ValidateOptionsResult.Fail("CloudflareAccess:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            return ValidateOptionsResult.Fail("CloudflareAccess:Audience is required.");
        }

        if (options.Keys.Count == 0)
        {
            return ValidateOptionsResult.Fail("At least one CloudflareAccess validation key is required.");
        }

        if (options.Keys.Any(key => string.IsNullOrWhiteSpace(key.KeyId)))
        {
            return ValidateOptionsResult.Fail("CloudflareAccess key ids are required.");
        }

        if (options.Keys.Any(key => !IsValidPublicKeyPem(key.PublicKeyPem)))
        {
            return ValidateOptionsResult.Fail("CloudflareAccess public keys must be valid RSA PEM.");
        }

        if (options.Keys.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != options.Keys.Count)
        {
            return ValidateOptionsResult.Fail("CloudflareAccess key ids must be unique.");
        }

        return ValidateOptionsResult.Success;
    }

    private static bool IsValidPublicKeyPem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}

public sealed class AdminAssertionHostOptions : IValidateOptions<AdminAssertionHostOptions>
{
    public const string SectionName = "AdminAssertion";

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public string? AppId { get; set; }

    public string? KeyId { get; set; }

    public string? PrivateKeyPem { get; set; }

    public ValidateOptionsResult Validate(string? name, AdminAssertionHostOptions options) =>
        ValidateCore(options);

    public AdminAssertionIssuerOptions ToOptions() => new(Issuer!, Audience!, AppId!, KeyId!, PrivateKeyPem!);

    private static ValidateOptionsResult ValidateCore(AdminAssertionHostOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            return ValidateOptionsResult.Fail("AdminAssertion:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            return ValidateOptionsResult.Fail("AdminAssertion:Audience is required.");
        }

        if (string.IsNullOrWhiteSpace(options.AppId))
        {
            return ValidateOptionsResult.Fail("AdminAssertion:AppId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.KeyId))
        {
            return ValidateOptionsResult.Fail("AdminAssertion:KeyId is required.");
        }

        if (!IsValidPrivateKeyPem(options.PrivateKeyPem))
        {
            return ValidateOptionsResult.Fail("AdminAssertion:PrivateKeyPem must be a valid RSA private key.");
        }

        return ValidateOptionsResult.Success;
    }

    private static bool IsValidPrivateKeyPem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}

public sealed class AdminApiHostOptions : IValidateOptions<AdminApiHostOptions>
{
    public const string SectionName = "AdminApi";

    public string? BaseUrl { get; set; }

    public ValidateOptionsResult Validate(string? name, AdminApiHostOptions options) =>
        IsValidBaseUrl(options.BaseUrl)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("AdminApi:BaseUrl must be an absolute HTTP(S) origin with a root path and no query or fragment.");

    internal static bool IsValidBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
        {
            return false;
        }

        return true;
    }
}

public sealed class AdminAppAuthHostValidator(IOptions<AdminAssertionHostOptions> assertion) : IValidateOptions<AdminAppAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminAppAuthOptions options)
    {
        try
        {
            options.Validate();
        }
        catch (InvalidOperationException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }

        var expectedAppId = assertion.Value.AppId;
        if (options.Apps.Count(app => string.Equals(app.AppId, expectedAppId, StringComparison.Ordinal)) != 1)
        {
            return ValidateOptionsResult.Fail("Exactly one AdminAppAuth app must match AdminAssertion:AppId.");
        }

        return ValidateOptionsResult.Success;
    }
}
