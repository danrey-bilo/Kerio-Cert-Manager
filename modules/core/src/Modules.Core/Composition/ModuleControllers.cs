using System.Reflection;
using Modules.Core.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Modules.Core.Composition;

/// <summary>Находит контроллеры модуля — объявленные <c>internal</c>, только свои и только включённые.</summary>
public sealed class ModuleControllers(Assembly own, ModuleFeatures features) : ControllerFeatureProvider
{
    /// <summary>Сборка, чьи контроллеры находит провайдер.</summary>
    public Assembly Assembly { get; } = own;

    /// <inheritdoc />
    protected override bool IsController(TypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (!typeInfo.Assembly.Equals(Assembly))
        {
            return false;
        }

        var контроллер = typeInfo is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
            && !typeInfo.IsDefined(typeof(NonControllerAttribute))
            && (typeInfo.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
                || typeInfo.IsDefined(typeof(ControllerAttribute)));

        return контроллер && features.Allows(typeInfo);
    }
}
