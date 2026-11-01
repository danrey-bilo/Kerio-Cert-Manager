using Modules.Core.Contracts.Tokens;

namespace Modules.Tokens.Contracts;

/// <summary>Выдача пары машине — открытый контракт токенов (решения 0033, 0034, 0113).</summary>
public interface IDeviceTokenIssuer
{
    /// <summary>Выдать пару машине <paramref name="deviceRef"/> человека <paramref name="userId"/>.</summary>
    Task<TokenPair> IssueAsync(
        Guid userId, string deviceName, string deviceRef, CancellationToken cancellationToken = default);
}
