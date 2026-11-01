using Modules.Mail.Contracts;
using Modules.Core.Contracts.Audit;

namespace Modules.Mail.Catalog;

/// <summary>События почты — модуль объявляет их так же, как любой другой.</summary>
internal sealed class MailOwnActions : IAuditActionProvider
{
    public IReadOnlyList<AuditActionGroup> Groups { get; } =
    [
        new("mail", [MailActions.PasswordSet, MailActions.TestSent, MailActions.TestFailed], "Почта"),
    ];
}
