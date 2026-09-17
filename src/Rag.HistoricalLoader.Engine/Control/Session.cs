using Rag.HistoricalLoader.Contracts;

namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// One connection's protocol session. <c>hello</c> must succeed on this session before any other operation is
/// delegated: a connection that opens with anything else is refused with a stable rejection and reaches no
/// engine work. The gate is session-scoped on purpose, so a fresh connection can never inherit a previous
/// peer's handshake.
/// </summary>
public sealed class ControlSession : IControlCommandHandler
{
    private readonly IControlCommandHandler _handler;

    public ControlSession(IControlCommandHandler handler)
        => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <summary>Whether this session completed its handshake.</summary>
    public bool HelloSucceeded { get; private set; }

    /// <summary>
    /// Handles one request of this session: the handshake gate runs first, and only a successful
    /// <c>hello</c> opens the session.
    /// </summary>
    public async ValueTask<ControlDispatchOutcome> HandleAsync(
        ControlRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!HelloSucceeded && request.Operation != ControlOperations.Hello)
        {
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.MalformedRequest);
        }

        var outcome = await _handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Operation == ControlOperations.Hello && outcome.Status == ControlStatuses.Ok)
        {
            HelloSucceeded = true;
        }

        return outcome;
    }
}
