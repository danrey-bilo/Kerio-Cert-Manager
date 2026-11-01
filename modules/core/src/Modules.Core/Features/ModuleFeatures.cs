using System.Reflection;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;

namespace Modules.Core.Features;

/// <summary>Переключатели подвозможностей одного модуля — свойство <c>Features</c> его параметров подключения.</summary>
public abstract class ModuleFeatures
{
    private Dictionary<string, FeatureSwitch>? _снимок;

    /// <summary>Набор переключателей модуля <paramref name="module"/>.</summary>
    protected ModuleFeatures(string module)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        Module = module;
    }

    /// <summary>Имя модуля.</summary>
    public string Module { get; }

    /// <summary>Переключатели — после закрепления, в порядке объявления.</summary>
    public IReadOnlyList<FeatureSwitch> Switches => [.. Снимок().Values];

    /// <summary>Закреплён ли набор.</summary>
    public bool IsSealed => _снимок is not null;

    /// <summary>Включена ли подвозможность; незнакомое имя — ошибка, а не «выключено».</summary>
    public bool IsEnabled(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // ⚠ Незнакомое имя — опечатка или чужая подвозможность.
        return Снимок().TryGetValue(name, out var переключатель)
            ? переключатель.Enabled
            : throw new InvalidOperationException(
                $"Модуль {Module} не знает подвозможности «{name}». Известны: {string.Join(", ", Снимок().Keys)}.");
    }

    /// <summary>Достижим ли тип: без пометки — да, с пометкой — если подвозможность включена.</summary>
    public bool Allows(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.GetCustomAttribute<FeatureAttribute>(inherit: false) is not { } пометка
            || IsEnabled(пометка.Name);
    }

    /// <summary>Закрепить значения.</summary>
    public void Seal() => Снимок();

    /// <summary>Взять значения из конфигурации установки.</summary>
    public void Bind(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (IsSealed)
        {
            throw new InvalidOperationException($"Переключатели модуля {Module} уже закреплены подключением.");
        }

        var раздел = configuration.GetSection($"Features:{Module}");
        var свойства = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(свойство => свойство.IsDefined(typeof(FeatureAttribute)))
            .ToDictionary(свойство => свойство.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var ключ in раздел.GetChildren())
        {
            if (!свойства.TryGetValue(ключ.Key, out var свойство))
            {
                throw new InvalidOperationException(
                    $"У модуля {Module} нет переключателя «{ключ.Key}» (Features:{Module}:{ключ.Key}). Известны: "
                    + string.Join(", ", свойства.Keys) + ".");
            }

            свойство.SetValue(this, bool.TryParse(ключ.Value, out var значение)
                ? значение
                : throw new InvalidOperationException(
                    $"Features:{Module}:{ключ.Key} = «{ключ.Value}» — ожидалось true или false."));
        }
    }

    /// <summary>Права выключенных подвозможностей — модуль их не объявляет и не выдаёт.</summary>
    public IReadOnlySet<string> DisabledPermissions => Снятые(переключатель => переключатель.Permissions);

    /// <summary>Права модуля без прав выключенных подвозможностей.</summary>
    public IPermissionProvider Filter(IPermissionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var снято = Снятые(переключатель => переключатель.Permissions);

        return new ОтобранныеПрава([.. provider.Permissions.Where(право => !снято.Contains(право.Key))]);
    }

    /// <summary>События журнала модуля без событий выключенных подвозможностей.</summary>
    public IAuditActionProvider Filter(IAuditActionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var снято = Снятые(переключатель => переключатель.Actions);

        return new ОтобранныеСобытия(
        [
            .. from группа in provider.Groups
               let оставшиеся = группа.Actions.Where(событие => !снято.Contains(событие.Name)).ToList()
               where оставшиеся.Count > 0
               select группа with { Actions = оставшиеся },
        ]);
    }

    /// <summary>Настройки модуля без настроек выключенных подвозможностей.</summary>
    public ISettingDeclarationProvider Filter(ISettingDeclarationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var снято = Снятые(переключатель => переключатель.Settings);

        return new ОтобранныеНастройки([.. provider.Declarations.Where(настройка => !снято.Contains(настройка.Name))]);
    }

    /// <summary>Что принадлежит подвозможностям: права, настройки, события — поимённо.</summary>
    protected virtual IEnumerable<FeatureOwnership> Owned() => [];

    private HashSet<string> Снятые(Func<FeatureSwitch, IReadOnlyList<string>> что) =>
        [.. Снимок().Values.Where(переключатель => !переключатель.Enabled).SelectMany(что)];

    private Dictionary<string, FeatureSwitch> Снимок()
    {
        if (_снимок is { } готов)
        {
            return готов;
        }

        var владение = Owned().ToList();
        var снимок = new Dictionary<string, FeatureSwitch>(StringComparer.Ordinal);

        foreach (var свойство in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (свойство.GetCustomAttribute<FeatureAttribute>() is not { } пометка)
            {
                continue;
            }

            if (свойство.PropertyType != typeof(bool))
            {
                throw new InvalidOperationException($"Переключатель {пометка.Name} модуля {Module} — не логическое свойство.");
            }

            if (!пометка.Name.StartsWith(Module + ".", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Имя подвозможности {пометка.Name} не начинается с «{Module}.».");
            }

            if (string.IsNullOrWhiteSpace(пометка.Description))
            {
                throw new InvalidOperationException($"У переключателя {пометка.Name} модуля {Module} нет описания.");
            }

            var своё = владение.Where(запись => запись.Feature == пометка.Name).ToList();

            снимок.Add(пометка.Name, new FeatureSwitch(пометка.Name, пометка.Description, (bool)свойство.GetValue(this)!)
            {
                Permissions = [.. своё.SelectMany(запись => запись.Permissions ?? [])],
                Settings = [.. своё.SelectMany(запись => запись.Settings ?? [])],
                Actions = [.. своё.SelectMany(запись => запись.Actions ?? [])],
            });
        }

        if (владение.FirstOrDefault(запись => !снимок.ContainsKey(запись.Feature)) is { } чужое)
        {
            throw new InvalidOperationException($"Модуль {Module} называет владельцем незнакомую подвозможность {чужое.Feature}.");
        }

        return _снимок = снимок;
    }

    private sealed class ОтобранныеПрава(IReadOnlyList<PermissionDescriptor> права) : IPermissionProvider
    {
        public IReadOnlyList<PermissionDescriptor> Permissions { get; } = права;
    }

    private sealed class ОтобранныеСобытия(IReadOnlyList<AuditActionGroup> группы) : IAuditActionProvider
    {
        public IReadOnlyList<AuditActionGroup> Groups { get; } = группы;
    }

    private sealed class ОтобранныеНастройки(IReadOnlyList<ISettingDeclaration> настройки) : ISettingDeclarationProvider
    {
        public IReadOnlyList<ISettingDeclaration> Declarations { get; } = настройки;
    }
}

/// <summary>Один переключатель: имя, описание, включён ли и что ему принадлежит.</summary>
public sealed record FeatureSwitch(string Name, string Description, bool Enabled)
{
    /// <summary>Права подвозможности: без неё не объявляются.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>Настройки подвозможности.</summary>
    public IReadOnlyList<string> Settings { get; init; } = [];

    /// <summary>События журнала подвозможности.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];
}

/// <summary>Что принадлежит подвозможности — поимённо.</summary>
public sealed record FeatureOwnership(
    string Feature,
    string[]? Permissions = null,
    string[]? Settings = null,
    string[]? Actions = null);
