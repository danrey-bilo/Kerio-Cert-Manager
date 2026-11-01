using Modules.Core.Contracts.Authz;

namespace Modules.Core.Contracts.Quotas;

/// <summary>Кому принадлежит предел и чьё потребление считается.</summary>
public readonly record struct QuotaSubject(GrantSubject Type, Guid Id)
{
    /// <summary>Человек.</summary>
    public static QuotaSubject User(Guid id) => new(GrantSubject.User, id);

    /// <summary>Команда.</summary>
    public static QuotaSubject Team(Guid id) => new(GrantSubject.Team, id);
}
