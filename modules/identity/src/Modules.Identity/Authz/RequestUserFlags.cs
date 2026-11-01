using Modules.Core.Contracts.Accounts;

namespace Modules.Identity.Authz;

/// <summary>Флаги людей, уже прочитанные в ЭТОМ запросе, — память области, а не кэш.</summary>
internal sealed class RequestUserFlags
{
    private readonly Dictionary<Guid, AccountFlags?> известно = [];

    /// <summary>Спрашивали ли уже про этого человека — и что узнали.</summary>
    public bool TryGet(Guid userId, out AccountFlags? flags) => известно.TryGetValue(userId, out flags);

    /// <summary>Запомнить прочитанное.</summary>
    public void Remember(Guid userId, AccountFlags? flags) => известно[userId] = flags;
}
