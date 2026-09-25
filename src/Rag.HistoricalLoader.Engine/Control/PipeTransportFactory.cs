namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// The production transport factory of the control boundary. It is the only platform gate of the engine:
/// <list type="bullet">
/// <item><see cref="IsPlatformSupported"/> is a fact of the boundary, not of one invocation, so <c>serve</c>
/// can fail closed with the contract's stable <c>platform_not_supported</c> code before it takes the
/// installation lock, opens the store, reads a credential, or starts any ingestion;</item>
/// <item><see cref="Limits"/> is the single recorded set of IPC bounds — the frame/depth/page values stay the
/// contract's own limits, so the pipe cannot invent a looser deadline, a bigger page, or an unbounded
/// connection count of its own;</item>
/// <item>the portable surface carries no TCP, no pipe type, no security namespace, and no public/anonymous/
/// insecure switch: the Windows implementation and its ACL/peer-identity helpers are internal, and there is no
/// fallback transport for a platform the boundary cannot serve.</item>
/// </list>
/// </summary>
public static class ControlPipeTransportFactory
{
    /// <summary>
    /// Whether this platform can serve a boundary this engine can vouch for. <see langword="false"/> on every
    /// platform whose pipe could not be restricted to the current user.
    /// </summary>
    public static bool IsPlatformSupported => WindowsNamedPipeBoundary.IsSupported;

    /// <summary>The recorded IPC limits the boundary applies. The pipe never loosens them.</summary>
    public static ControlTransportLimits Limits => ControlTransportLimits.Default;

    /// <summary>
    /// Creates the production transport and the endpoint it owns, or reports the stable code of the platform it
    /// cannot serve. Nothing is opened by this call: the pipe instance is created by the returned transport's
    /// explicit <c>Start</c>, which only the supervised <c>serve</c> composition performs.
    /// </summary>
    public static bool TryCreate(
        out IControlTransport? transport,
        out ControlPipeEndpoint? endpoint,
        out string errorCode)
        => WindowsNamedPipeBoundary.TryCreate(out transport, out endpoint, out errorCode);
}
