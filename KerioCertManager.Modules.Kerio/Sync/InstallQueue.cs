using KerioCertManager.Modules.Kerio.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Core.Data;
using Modules.Core.Errors;

namespace KerioCertManager.Modules.Kerio.Sync;

/// <summary>Слот установки: во всём сервисе сертификат ставится один за раз, сколько бы ни было серверов и процессов.</summary>
internal sealed class InstallGate(AppDbContext db, TimeProvider clock)
{
    /// <summary>Как у аренды сервера; умерший процесс отпускает слот сам.</summary>
    internal static readonly TimeSpan Срок = ServerLeases.Срок;

    /// <summary>Как часто спрашивать, свободен ли слот.</summary>
    internal static readonly TimeSpan Опрос = TimeSpan.FromSeconds(2);

    /// <summary>Сколько сервер ждёт слот в этом проходе; дольше — сертификат ждёт следующего, проверки остальных важнее.</summary>
    internal static readonly TimeSpan Ожидание = TimeSpan.FromSeconds(90);

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

/// <summary>Ставит ли сервис сертификат сам — правило одно для планировщиков и панели.</summary>
internal static class InstallPolicy
{
    /// <summary>Общая пауза держит всё; выключатель сервера — только продления: первая установка выбранного сертификата идёт и при нём.</summary>
    public static bool Automatic(bool globalAutoDeploy, bool serverAutoDeploy, string? installedSerial) =>
        globalAutoDeploy && (serverAutoDeploy || installedSerial is null);
}

/// <summary>
/// Продлевает аренду сервера и слот, пока идёт проход: долгая установка держит их сколько нужно, а умерший процесс
/// отпускает за <see cref="ServerLeases.Срок"/>. Своя область — контекст прохода занят установкой.
/// </summary>
internal sealed class LeaseHeartbeat : IAsyncDisposable
{
    /// <summary>Как часто продлевать: несколько раз за срок аренды, сбой одного раза не страшен.</summary>
    internal static readonly TimeSpan Шаг = TimeSpan.FromMinutes(1);

    private readonly CancellationTokenSource _стоп = new();
    private readonly IServiceScopeFactory _области;
    private readonly TimeProvider _часы;
    private readonly ILogger _лог;
    private readonly Guid _сервер;
    private readonly Guid _владелец;
    private readonly Task _цикл;

    public LeaseHeartbeat(IServiceScopeFactory scopes, TimeProvider clock, ILogger log, Guid serverId, Guid owner)
    {
        _области = scopes;
        _часы = clock;
        _лог = log;
        _сервер = serverId;
        _владелец = owner;
        _цикл = ЦиклAsync();
    }

    /// <summary>Слот установки, который сейчас держит проход; <c>null</c> — не держит.</summary>
    public Guid? Slot { get; set; }

    private async Task ЦиклAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(Шаг, _часы, _стоп.Token);

                try
                {
                    await using var область = _области.CreateAsyncScope();
                    await область.ServiceProvider.GetRequiredService<ServerLeases>().ExtendAsync(_сервер, _владелец);

                    if (Slot is { } слот)
                    {
                        await область.ServiceProvider.GetRequiredService<InstallGate>().ExtendAsync(слот);
                    }
                }
                catch (Exception ошибка) when (ошибка is not OperationCanceledException)
                {
                    _лог.LogWarning("kerio_lease_extend_failed {Server} {Error}", _сервер, SafeError.Describe(ошибка));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _стоп.CancelAsync();
        await _цикл;
        _стоп.Dispose();
    }
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
