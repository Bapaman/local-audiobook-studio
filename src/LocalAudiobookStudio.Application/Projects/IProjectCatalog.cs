using LocalAudiobookStudio.Application.Models;

namespace LocalAudiobookStudio.Application.Projects;

public interface IProjectCatalog
{
    Task<IReadOnlyList<CatalogProject>> ListAsync(CancellationToken cancellationToken = default);
    Task<CatalogProject?> FindAsync(string projectId, CancellationToken cancellationToken = default);
    Task InsertAsync(CatalogProject project, CancellationToken cancellationToken = default);
    Task<IdempotencyReceipt?> FindReceiptAsync(string idempotencyKey, CancellationToken cancellationToken = default);
    Task StoreReceiptAsync(IdempotencyReceipt receipt, CancellationToken cancellationToken = default);
    Task TouchOpenedAsync(string projectId, DateTimeOffset openedUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogCandidate>> ReconcileAsync(CancellationToken cancellationToken = default);
    Task FinalizeAsync(CatalogProject project, IdempotencyReceipt? receipt, CancellationToken cancellationToken = default);
    Task QuarantineAsync(string projectId, CancellationToken cancellationToken = default);
}

public interface IStudioProjectService
{
    Task<IReadOnlyList<ProjectListItem>> ListAsync(CancellationToken cancellationToken = default);
    Task<CreateProjectResult> CreateAsync(CreateProjectCommand command, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> OpenAsync(string projectId, CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> GetAsync(string projectId, CancellationToken cancellationToken = default);
}
