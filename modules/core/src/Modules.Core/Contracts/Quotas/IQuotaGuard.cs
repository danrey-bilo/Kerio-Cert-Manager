namespace Modules.Core.Contracts.Quotas;

/// <summary>Проверить предел и занять место — до записи и в транзакции самого действия.</summary>
public interface IQuotaGuard
{
    /// <summary>Занять место, если предел позволяет.</summary>
    Task ReserveAsync(
        QuotaPayer payer, string metric, long delta, CancellationToken cancellationToken = default);

    /// <summary>Освободить занятое — при удалении объекта.</summary>
    Task ReleaseAsync(
        QuotaPayer payer, string metric, long delta, CancellationToken cancellationToken = default);

    /// <summary>Перенести занятое от одного плательщика к другому.</summary>
    Task TransferAsync(
        QuotaPayer source,
        QuotaPayer target,
        string metric,
        long delta,
        CancellationToken cancellationToken = default);

    /// <summary>Занять сразу по нескольким метрикам.</summary>
    Task CheckAsync(
        QuotaPayer payer,
        IReadOnlyDictionary<string, long> deltas,
        CancellationToken cancellationToken = default);

    /// <summary>Спросить, поместится ли, ничего не занимая.</summary>
    Task<bool> WouldExceedAsync(
        QuotaPayer payer,
        IReadOnlyDictionary<string, long> deltas,
        CancellationToken cancellationToken = default);

    /// <summary>Узнать предел на единицу.</summary>
    Task<long?> LimitAsync(
        QuotaPayer payer, string metric, CancellationToken cancellationToken = default);
}
