namespace Rag.HistoricalLoader.Contracts;

/// <summary>
/// The only transport-neutral seam between a replaceable shell and the engine control host. Shell clients
/// depend on this interface; the engine implements it. No engine, storage, credential, or transport type
/// may appear here, and no method accepts a caller-selected pipe, database, or filesystem path.
/// </summary>
public interface ILoaderControlClient
{
    Task<ControlResponse<HelloResult>> HelloAsync(CancellationToken cancellationToken = default);

    Task<ControlResponse<CommandReceipt>> StartAsync(StartRequest request, CancellationToken cancellationToken = default);

    Task<ControlResponse<CommandReceipt>> PauseAsync(PauseRequest request, CancellationToken cancellationToken = default);

    Task<ControlResponse<CommandReceipt>> ResumeAsync(ResumeRequest request, CancellationToken cancellationToken = default);

    Task<ControlResponse<StateSnapshot>> GetStateAsync(GetStateRequest? request = null, CancellationToken cancellationToken = default);

    Task<ControlResponse<DocumentPage>> GetDocumentsAsync(GetDocumentsRequest request, CancellationToken cancellationToken = default);

    Task<ControlResponse<EventPage>> GetEventsAsync(GetEventsRequest request, CancellationToken cancellationToken = default);
}
