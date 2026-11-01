namespace Modules.Core.Contracts.Quotas;

/// <summary>В чём меряется метрика.</summary>
public enum QuotaUnit
{
    /// <summary>Штуками: хранилища, записи, устройства.</summary>
    Count,

    /// <summary>Байтами: занятое место, размер вложения.</summary>
    Bytes,
}

/// <summary>Что именно ограничивает предел.</summary>
public enum QuotaLimitKind
{
    /// <summary>Накопительный: предел на сумму по субъекту.</summary>
    Cumulative,

    /// <summary>На единицу: предел одного объекта, без счётчика.</summary>
    PerItem,
}

/// <summary>Объявление метрики её владельцем.</summary>
public sealed record QuotaMetricDescriptor(
    string Name,
    string Title,
    QuotaUnit Unit,
    QuotaLimitKind Kind,
    long? DefaultLimit,
    string Category);
