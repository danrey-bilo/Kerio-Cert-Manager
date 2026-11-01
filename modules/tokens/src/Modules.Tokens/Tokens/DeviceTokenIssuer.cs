using Modules.Tokens.Contracts;
using Modules.Core.Contracts.Tokens;

namespace Modules.Tokens.Tokens;

/// <summary>Пара машине — тем же путём, что всякая выдача: срок, ротация, журнал у службы токенов.</summary>
[Modules.Core.Features.Feature("tokens.device_tokens")]
internal sealed class DeviceTokenIssuer(TokenService tokens) : IDeviceTokenIssuer
{
    /// <inheritdoc />
    public Task<TokenPair> IssueAsync(
        Guid userId, string deviceName, string deviceRef, CancellationToken cancellationToken = default) =>
        tokens.IssueAsync(
            new TokenIssueRequest(userId, TokenKinds.Device, deviceName, DeviceRef: deviceRef),
            cancellationToken);
}
