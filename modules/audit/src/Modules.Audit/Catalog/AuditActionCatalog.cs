using Modules.Core.Contracts.Audit;

namespace Modules.Audit.Catalog;

/// <summary>Справочник событий, собранный со всех подключённых модулей.</summary>
internal sealed class AuditActionCatalog
{
    public AuditActionCatalog(IEnumerable<IAuditActionProvider> providers)
    {
        Groups =
        [
            .. providers
                .SelectMany(provider => provider.Groups)
                .GroupBy(group => group.Key, StringComparer.Ordinal)
                .Select(byKey => new AuditActionGroup(
                    byKey.Key,
                    [.. byKey.SelectMany(group => group.Actions).Distinct()],
                    byKey.Select(group => group.Title).FirstOrDefault(title => title is not null)))
                .OrderBy(group => group.Key, StringComparer.Ordinal),
        ];

        // ⚠ Пустая заявленная группа — ошибка объявления, а не пустой раздел.
        var empty = Groups.Where(group => group.Actions.Count == 0).Select(group => group.Key).ToList();

        if (empty.Count > 0)
        {
            throw new InvalidOperationException(
                "Группа событий объявлена без единого события: " + string.Join(", ", empty)
                + ". Либо в ней должны быть события, либо её не должно быть.");
        }

        var names = Groups.SelectMany(group => group.Actions).Select(action => action.Name).ToList();
        _known = new HashSet<string>(names, StringComparer.Ordinal);

        var duplicates = names.GroupBy(name => name, StringComparer.Ordinal)
            .Where(byName => byName.Count() > 1)
            .Select(byName => byName.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Событие объявлено дважды: " + string.Join(", ", duplicates));
        }

        // ⚠ Строго ПОСЛЕ проверки дублей.
        _actionsByGroup = Groups.ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<string>)[.. group.Actions.Select(action => action.Name)],
            StringComparer.Ordinal);

        _groupByAction = Groups
            .SelectMany(group => group.Actions.Select(action => (action.Name, group.Key)))
            .ToDictionary(pair => pair.Name, pair => pair.Key, StringComparer.Ordinal);
    }

    private readonly HashSet<string> _known;

    /// <summary>Группы событий, по ключу группы.</summary>
    public IReadOnlyList<AuditActionGroup> Groups { get; }

    /// <summary>Объявлено ли такое событие хоть кем-то.</summary>
    public bool Knows(AuditAction action) => _known.Contains(action.Name);

    /// <summary>Какому модулю принадлежит событие.</summary>
    public string? GroupOf(string action) =>
        _groupByAction.TryGetValue(action, out var group) ? group : null;

    /// <summary>Все события модуля.</summary>
    public IReadOnlyList<string>? ActionsOf(string group) =>
        _actionsByGroup.TryGetValue(group, out var actions) ? actions : null;

    private readonly Dictionary<string, string> _groupByAction;
    private readonly Dictionary<string, IReadOnlyList<string>> _actionsByGroup;
}
