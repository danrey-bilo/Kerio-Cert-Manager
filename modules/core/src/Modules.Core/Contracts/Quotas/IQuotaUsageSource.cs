namespace Modules.Core.Contracts.Quotas;

/// <summary>Владелец метрики отвечает, сколько на самом деле занято.</summary>
public interface IQuotaUsageSource
{
    /// <summary>Метрика, о которой этот источник умеет отвечать.</summary>
    string Metric { get; }

    /// <summary>Сколько занято сейчас — по всем плательщикам сразу.</summary>
    Task<IReadOnlyDictionary<QuotaSubject, long>> CurrentAsync(
        CancellationToken cancellationToken = default);
}
