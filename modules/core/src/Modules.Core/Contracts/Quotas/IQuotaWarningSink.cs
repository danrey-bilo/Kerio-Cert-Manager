namespace Modules.Core.Contracts.Quotas;

/// <summary>Кому сказать, что предел близко.</summary>
public interface IQuotaWarningSink
{
    /// <summary>Предупредить: предел близко.</summary>
    Task WarnAsync(
        QuotaSubject subject,
        string metric,
        long used,
        long limit,
        int thresholdPercent,
        CancellationToken cancellationToken = default);
}
