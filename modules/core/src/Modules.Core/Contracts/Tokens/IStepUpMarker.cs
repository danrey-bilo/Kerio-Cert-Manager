namespace Modules.Core.Contracts.Tokens;

/// <summary>Отметить предъявленный токен как переподтверждённый.</summary>
public interface IStepUpMarker
{
    /// <summary>Записать, что личность за этим токеном только что подтвердили.</summary>
    Task<bool> MarkAsync(Guid tokenId, DateTimeOffset at, CancellationToken cancellationToken = default);
}
