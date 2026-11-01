namespace Modules.Core.Contracts.Quotas;

/// <summary>Модуль объявляет метрики, которые он же и потребляет.</summary>
public interface IQuotaMetricProvider
{
    /// <summary>Метрики этого модуля.</summary>
    IReadOnlyList<QuotaMetricDescriptor> Metrics { get; }
}
