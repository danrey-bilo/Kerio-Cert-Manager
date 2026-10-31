using Modules.Core.Contracts.Audit;

namespace KerioCertManager.Modules.KeyManager.Contracts;

/// <summary>События журнала модуля.</summary>
public static class KeyManagerActions
{
    public static readonly AuditAction ApiKeySet = new("keymanager.api_key_set", "Задан ключ API Key Manager Plus");

    public static IReadOnlyList<AuditAction> All { get; } = [ApiKeySet];
}

internal sealed class KeyManagerAuditActions : IAuditActionProvider
{
    /// <inheritdoc />
    public IReadOnlyList<AuditActionGroup> Groups { get; } =
        [new("keymanager", KeyManagerActions.All, "Key Manager Plus")];
}
