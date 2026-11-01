namespace Modules.Core.Features;

/// <summary>Пометка подвозможности модуля: на переключателе — его имя и описание, на контроллере, службе или реализации — чему они принадлежат.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = false)]
public sealed class FeatureAttribute(string name) : Attribute
{
    /// <summary>Имя подвозможности: <c>&lt;модуль&gt;.&lt;подвозможность&gt;</c>, например <c>vaults.export</c>.</summary>
    public string Name { get; } = name;

    /// <summary>Что даёт подвозможность — одной фразой; обязательно на переключателе.</summary>
    public string? Description { get; init; }
}
