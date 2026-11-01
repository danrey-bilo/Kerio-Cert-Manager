using KerioCertManager.Modules.Kerio.Domain;
using Microsoft.EntityFrameworkCore;
using Modules.Core.Data;

namespace KerioCertManager.Modules.Kerio.Sync;

/// <summary>Слот установки: во всём сервисе сертификат ставится один за раз, сколько бы ни было серверов и процессов.</summary>
internal sealed class InstallGate(AppDbContext db, TimeProvider clock)
{
    /// <summary>Как у аренды сервера: дольше любой установки; умерший процесс отпускает слот сам.</summary>
    internal static readonly TimeSpan Срок = ServerLeases.Срок;

    /// <summary>Как часто спрашивать, свободен ли слот.</summary>
    internal static TimeSpan Опрос { get; set; } = TimeSpan.FromSeconds(2);

    public async Task<bool> TryAcquireAsync(Guid owner, CancellationToken cancellationToken = default)
    {
        var сейчас = clock.GetUtcNow();
        var до = сейчас + Срок;

        return await db.Set<InstallSlot>()
            .Where(с => с.Id == InstallSlot.Only && (с.LeaseUntil == null || с.LeaseUntil < сейчас))
            .ExecuteUpdateAsync(set => set.SetProperty(с => с.LeaseUntil, до).SetProperty(с => с.LeaseOwner, owner), cancellationToken) == 1;
    }

    public Task ExtendAsync(Guid owner)
    {
        var до = clock.GetUtcNow() + Срок;

        return db.Set<InstallSlot>()
            .Where(с => с.Id == InstallSlot.Only && с.LeaseOwner == owner)
            .ExecuteUpdateAsync(set => set.SetProperty(с => с.LeaseUntil, до), CancellationToken.None);
    }

    public Task ReleaseAsync(Guid owner) =>
        db.Set<InstallSlot>()
            .Where(с => с.Id == InstallSlot.Only && с.LeaseOwner == owner)
            .ExecuteUpdateAsync(
                set => set.SetProperty(с => с.LeaseUntil, (DateTimeOffset?)null).SetProperty(с => с.LeaseOwner, (Guid?)null),
                CancellationToken.None);
}

/// <summary>Сертификат, который пора ставить: по умолчанию или своего имени.</summary>
internal sealed record InstallCandidate(string CommonName, bool IsDefault, DateTimeOffset? RequestedAt, DateTimeOffset? CurrentNotAfter);

/// <summary>Порядок очереди установки.</summary>
internal static class InstallOrder
{
    /// <summary>
    /// Сначала нажатые администратором — по времени нажатия; затем по риску: истёкший раньше, недостающий,
    /// истекающий раньше; при равенстве — сертификат по умолчанию, затем по имени.
    /// </summary>
    public static IReadOnlyList<InstallCandidate> Sort(IEnumerable<InstallCandidate> candidates, DateTimeOffset now) =>
        [.. candidates
            .OrderBy(к => к.RequestedAt is null)
            .ThenBy(к => к.RequestedAt)
            .ThenBy(к => к.CurrentNotAfter ?? now)
            .ThenBy(к => !к.IsDefault)
            .ThenBy(к => к.CommonName, StringComparer.OrdinalIgnoreCase)];
}
