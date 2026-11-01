namespace Modules.Core.Contracts.Authz;

/// <summary>Сколько живёт подтверждение второго фактора для действий под <see cref="RequireRecentSecondFactorAttribute"/>.</summary>
public interface IStepUpWindow
{
    /// <summary>Окно по умолчанию — пять минут, как было до решения 0120.</summary>
    const int DefaultMinutes = 5;

    /// <summary>Окно в минутах — не меньше одной.</summary>
    Task<int> MinutesAsync(CancellationToken cancellationToken = default);
}
