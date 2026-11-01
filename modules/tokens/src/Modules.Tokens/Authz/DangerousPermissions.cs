using Modules.Core.Contracts.Authz;

namespace Modules.Tokens.Authz;

/// <summary>Права, которые нельзя дать ключу интеграции.</summary>
internal sealed class DangerousPermissions
{
    private readonly HashSet<string> _keys;

    public DangerousPermissions(IEnumerable<IPermissionProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _keys = providers
            .SelectMany(provider => provider.Permissions)
            .Where(permission => permission.IsDangerous)
            .Select(permission => permission.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Сколько прав объявлено опасными.</summary>
    public int Count => _keys.Count;

    /// <summary>Объявлено ли это право опасным.</summary>
    public bool Contains(string permission) => _keys.Contains(permission);
}
