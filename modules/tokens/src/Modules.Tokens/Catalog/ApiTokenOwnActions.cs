using Modules.Tokens.Contracts;
using Modules.Core.Contracts.Audit;

namespace Modules.Tokens.Catalog;

/// <summary>События модуля для общего справочника.</summary>
internal sealed class ApiTokenOwnActions : IAuditActionProvider
{
    public IReadOnlyList<AuditActionGroup> Groups { get; } =
    [
        new(
            "auth",
            [
                ApiTokenActions.Revoked,
                ApiTokenActions.Reuse,
                ApiTokenActions.Cleaned,
            ],
            Title: null),

        // ⚠ Отдельная группа, а не продолжение `auth`, и это не косметика.
        new(
            "apitokens",
            [
                ApiTokenActions.Created,
                ApiTokenActions.KeyRevoked,
                ApiTokenActions.ExpiredPresented,
            ],
            Title: "Ключи интеграций"),
    ];
}
