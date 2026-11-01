namespace Modules.Tokens.Contracts;

/// <summary>Абсолютный горизонт токена машины — открытый контракт токенов (решения 0034, 0113).</summary>
public interface IDeviceCredentialHorizon
{
    /// <summary>Имя настройки, которой задан горизонт, — уезжает в лог того, кого он ограничил.</summary>
    string Setting { get; }

    /// <summary>Горизонт; <c>null</c> — испорченное значение, не ограничиваю.</summary>
    Task<TimeSpan?> HorizonAsync(CancellationToken cancellationToken = default);
}
