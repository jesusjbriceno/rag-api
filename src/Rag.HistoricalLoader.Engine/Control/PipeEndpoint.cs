using System.Security.Cryptography;
using System.Text;

namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// The kernel-object name of the local control pipe. An endpoint is a fixed prefix plus an opaque identity
/// derived from the installation and the user, so:
/// <list type="bullet">
/// <item>the name is never chosen by a caller — there is no public constructor, no <c>Parse</c>/<c>TryParse</c>,
/// and no member that accepts a pipe name or a path;</item>
/// <item>the name is a single kernel-object name, never a path, a drive path, or a relative path, so a caller
/// cannot address a different object, a filesystem path, or an alternate data stream;</item>
/// <item>the name is deterministic per (installation, user), so a second instance of the same installation and
/// user derives the same name instead of inventing one, and a different installation or user can never address
/// this endpoint.</item>
/// </list>
/// This type stays portable on purpose: the Windows named-pipe wrapper and its ACL/peer-identity helpers are
/// internal, so the public control surface carries no pipe, security, or interop type (the 11.prev-c guard).
/// </summary>
public sealed class ControlPipeEndpoint
{
    /// <summary>The fixed product prefix of every endpoint name. It carries no path fragment.</summary>
    public const string FixedPrefix = "rag-historical-loader-v1";

    /// <summary>The length of the opaque identity: 128 bits of SHA-256 rendered as lowercase hexadecimal.</summary>
    public const int OpaqueIdentityLength = 32;

    /// <summary>The largest accepted installation or user identity token, so no unbounded input is hashed.</summary>
    public const int MaxIdentityLength = 256;

    private ControlPipeEndpoint(string opaqueIdentity, string pipeName)
    {
        OpaqueIdentity = opaqueIdentity;
        PipeName = pipeName;
    }

    /// <summary>The opaque lowercase-hexadecimal identity that names this endpoint.</summary>
    public string OpaqueIdentity { get; }

    /// <summary>The complete kernel-object name: <see cref="FixedPrefix"/> plus <see cref="OpaqueIdentity"/>.</summary>
    public string PipeName { get; }

    /// <summary>
    /// Derives the endpoint of one installation and one user. Both inputs are opaque identity tokens — a
    /// machine installation identity and a user identity — never a path, a pipe name, or a caller-selected
    /// object name. A rejected token fails closed with <see cref="ArgumentException"/> instead of silently
    /// sanitising an attacker-shaped name into a valid one.
    /// </summary>
    /// <exception cref="ArgumentNullException">An identity is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is empty, oversized, a control string, or not an opaque token.</exception>
    public static ControlPipeEndpoint Derive(string installationIdentity, string userIdentity)
    {
        ArgumentNullException.ThrowIfNull(installationIdentity);
        ArgumentNullException.ThrowIfNull(userIdentity);

        EnsureOpaqueIdentity(installationIdentity, nameof(installationIdentity));
        EnsureOpaqueIdentity(userIdentity, nameof(userIdentity));

        // A fixed-separator digest binds the name to both identities: substituting either one yields a
        // different kernel object, and the inputs never appear in the name themselves.
        var material = Encoding.UTF8.GetBytes(string.Concat(installationIdentity, "\n", userIdentity));
        var digest = SHA256.HashData(material);
        var opaqueIdentity = Convert.ToHexStringLower(digest.AsSpan(0, OpaqueIdentityLength / 2));
        return new ControlPipeEndpoint(opaqueIdentity, string.Concat(FixedPrefix, "-", opaqueIdentity));
    }

    /// <summary>
    /// An identity is an opaque token: ASCII letters, digits, hyphens, and underscores only. Everything a
    /// caller could use to steer the kernel object — a path separator, a drive or stream colon, a dot or
    /// parent reference, a wildcard, a fragment, a control byte, or a blank string — is refused.
    /// </summary>
    private static void EnsureOpaqueIdentity(string identity, string parameterName)
    {
        if (identity.Length == 0 || identity.Length > MaxIdentityLength)
        {
            throw new ArgumentException(
                $"The identity must be between 1 and {MaxIdentityLength} characters.",
                parameterName);
        }

        foreach (var character in identity)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                throw new ArgumentException(
                    "The identity must be an opaque token of ASCII letters, digits, hyphens, and underscores.",
                    parameterName);
            }
        }
    }
}
