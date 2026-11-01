using Modules.Core.Contracts.Settings;

namespace Modules.Settings.Catalog;

/// <summary>Всё, что объявили модули: имя → объявление.</summary>
internal sealed class SettingsCatalog
{
    private readonly Dictionary<string, ISettingDeclaration> _byName;

    public SettingsCatalog(IEnumerable<ISettingDeclarationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _byName = new Dictionary<string, ISettingDeclaration>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (var declaration in providers.SelectMany(provider => provider.Declarations))
        {
            if (_byName.TryGetValue(declaration.Name, out var existing))
            {
                // ⚠ Повтор ОДИНАКОВОГО объявления законен и не считается спором.
                if (!Совпадают(existing, declaration))
                {
                    duplicates.Add(declaration.Name);
                }

                continue;
            }

            _byName.Add(declaration.Name, declaration);
        }

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Два модуля объявили настройку с одним именем и разным содержимым: "
                + string.Join(", ", duplicates.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                + ". Имя настройки лежит в базе и в запросах клиента; два объявления одного "
                + "имени означают, что экран показывает одни границы, а проверка берёт другие.");
        }

        All =
        [
            .. _byName.Values
                .OrderBy(declaration => declaration.Category, StringComparer.Ordinal)
                .ThenBy(declaration => declaration.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>Все объявления, по разделам и именам.</summary>
    public IReadOnlyList<ISettingDeclaration> All { get; }

    /// <summary>Объявление по имени или <c>null</c>, если такого ключа никто не объявлял.</summary>
    public ISettingDeclaration? Find(string name) =>
        _byName.GetValueOrDefault(name);

    /// <summary>Два объявления одного имени описывают одно и то же.</summary>
    private static bool Совпадают(ISettingDeclaration a, ISettingDeclaration b) =>
        a.ValueType == b.ValueType
        && Equals(a.DefaultValue, b.DefaultValue)
        && a.Minimum == b.Minimum
        && a.Maximum == b.Maximum
        && a.Scope == b.Scope
        && a.IsNullable == b.IsNullable
        && a.IsSecurity == b.IsSecurity
        && a.Choices.SequenceEqual(b.Choices, StringComparer.Ordinal);
}
