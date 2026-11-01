using Modules.Core.Contracts.Audit;

namespace Modules.Audit.Detection;

/// <summary>Наблюдатель показов, который ничего не считает: детектор массового показа выключен.</summary>
internal sealed class NoSecretAccessMonitor : ISecretAccessMonitor
{
    /// <inheritdoc />
    public Task<bool> NoteAsync(Guid actorId, int events = 1, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
