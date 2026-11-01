using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Modules.Core.Contracts.Authz;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Modules.Hosting;

/// <summary>Вносит в контракт описание каждой операции, её модуль и требуемое право.</summary>
public sealed class EndpointDocsDocument(EndpointDocsOptions options) : IOpenApiOperationTransformer, IOpenApiDocumentTransformer
{
    /// <summary>Модуль, которому принадлежит операция: <c>Vaults</c>, <c>Identity</c>… или <c>Api</c>.</summary>
    public string ModuleExtension => $"{options.ExtensionPrefix}-module";

    /// <summary>Как пускают: <c>public</c>, <c>authenticated</c>, <c>permission</c>, <c>resource</c> или <c>self</c>.</summary>
    public string AccessExtension => $"{options.ExtensionPrefix}-access";

    /// <summary>Требует ли операция свежего второго фактора.</summary>
    public string RecentSecondFactorExtension => $"{options.ExtensionPrefix}-recent-second-factor";

    /// <summary>Требуемые права, если объявлены: через запятую, и обязательны все.</summary>
    public string PermissionExtension => $"{options.ExtensionPrefix}-permission";

    /// <summary>Пускает ли операция токен API.</summary>
    public string ApiTokenExtension => $"{options.ExtensionPrefix}-api-token";

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Description.ActionDescriptor is not ControllerActionDescriptor действие)
        {
            return Task.CompletedTask;
        }

        var документ = XmlDocs.Для(действие.MethodInfo);

        if (string.IsNullOrEmpty(operation.Summary) && документ?.Summary is { Length: > 0 } кратко)
        {
            operation.Summary = кратко;
        }

        if (документ is not null)
        {
            foreach (var параметр in operation.Parameters ?? [])
            {
                if (параметр is OpenApiParameter свой
                    && string.IsNullOrEmpty(свой.Description)
                    && свой.Name is { } имя
                    && документ.Params.TryGetValue(Имя(действие, имя), out var текст))
                {
                    свой.Description = текст;
                }
            }
        }

        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
        operation.Extensions[ModuleExtension] = Строка(Модуль(действие.ControllerTypeInfo));

        var метаданные = действие.EndpointMetadata;
        var право = метаданные.OfType<RequirePermissionAttribute>().LastOrDefault();
        var права = метаданные.OfType<RequirePermissionAttribute>()
            .Select(требование => требование.Permission)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        operation.Extensions[AccessExtension] = Строка(Доступ(метаданные, право));

        if (права.Count > 0)
        {
            operation.Extensions[PermissionExtension] = Строка(string.Join(", ", права));
        }

        if (метаданные.OfType<AllowApiTokenAttribute>().Any())
        {
            operation.Extensions[ApiTokenExtension] = new JsonNodeExtension(JsonValue.Create(true));
        }

        if (метаданные.OfType<RequireRecentSecondFactorAttribute>().Any())
        {
            operation.Extensions[RecentSecondFactorExtension] = new JsonNodeExtension(JsonValue.Create(true));
        }

        return Task.CompletedTask;
    }

    /// <summary>Как операция пускает — по пометкам проекта, последняя объявленная решает.</summary>
    internal static string Доступ(IList<object> метаданные, RequirePermissionAttribute? право)
    {
        ArgumentNullException.ThrowIfNull(метаданные);

        if (право is not null)
        {
            return "permission";
        }

        // ⚠ Пометка действия стоит в метаданных ПОСЛЕ пометки контроллера: решает последняя.
        foreach (var пометка in метаданные.Reverse())
        {
            switch (пометка)
            {
                case PublicEndpointAttribute:
                    return "public";
                case ResourceScopedAuthorizationAttribute:
                    return "resource";
                case SelfGuardedEndpointAttribute:
                    return "self";
                case AuthenticatedEndpointAttribute:
                    return "authenticated";
            }
        }

        return "authenticated";
    }

    /// <summary>Теги — по контроллерам, с описанием контроллера и именем его модуля.</summary>
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        document.Info.Title = options.Title;
        document.Info.Description = options.Description
            ?? "Операции сгруппированы тегами по контроллерам; модуль операции — расширение "
            + $"{ModuleExtension}, как она пускает — {AccessExtension}, требуемое право — {PermissionExtension}.";

        var теги = new SortedDictionary<string, OpenApiTag>(StringComparer.Ordinal);

        foreach (var группа in context.DescriptionGroups)
        {
            foreach (var описание in группа.Items)
            {
                if (описание.ActionDescriptor is not ControllerActionDescriptor действие
                    || теги.ContainsKey(действие.ControllerName))
                {
                    continue;
                }

                var модуль = Модуль(действие.ControllerTypeInfo);
                var кратко = XmlDocs.ДляТипа(действие.ControllerTypeInfo)?.Summary;

                теги[действие.ControllerName] = new OpenApiTag
                {
                    Name = действие.ControllerName,
                    Description = кратко is { Length: > 0 } ? $"{модуль}: {кратко}" : модуль,
                };
            }
        }

        // ⚠ Порядок — модуль, затем тег: документ читается по модулям, как и устроена система.
        document.Tags = new HashSet<OpenApiTag>(теги.Values
            .OrderBy(тег => тег.Description, StringComparer.Ordinal)
            .ThenBy(тег => тег.Name, StringComparer.Ordinal));

        return Task.CompletedTask;
    }

    /// <summary>Имя модуля по сборке: <c>Modules.Vaults</c> → <c>Vaults</c>, хост — <c>Api</c>.</summary>
    internal static string Модуль(Type тип)
    {
        var сборка = тип.Assembly.GetName().Name ?? string.Empty;

        return сборка.StartsWith("Modules.", StringComparison.Ordinal)
            ? сборка["Modules.".Length..]
            : "Api";
    }

    /// <summary>Имя параметра метода, на который ссылается параметр операции.</summary>
    private static string Имя(ControllerActionDescriptor действие, string имя) =>
        действие.Parameters.FirstOrDefault(параметр =>
                string.Equals(параметр.BindingInfo?.BinderModelName ?? параметр.Name, имя, StringComparison.Ordinal))
            ?.Name ?? имя;

    private static JsonNodeExtension Строка(string значение) => new(JsonValue.Create(значение));

    /// <summary>Описание члена из XML-файла его сборки.</summary>
    internal sealed record XmlDoc(string? Summary, IReadOnlyDictionary<string, string> Params);

    /// <summary>XML-документация сборок приложения: читается один раз на процесс и только при сборке документа.</summary>
    internal static class XmlDocs
    {
        private static readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, XmlDoc>> Сборки = new();

        public static XmlDoc? Для(MethodInfo метод) =>
            Сборка(метод.DeclaringType!.Assembly).TryGetValue(DocId(метод), out var документ) ? документ : null;

        public static XmlDoc? ДляТипа(Type тип) =>
            Сборка(тип.Assembly).TryGetValue("T:" + Тип(тип), out var документ) ? документ : null;

        private static IReadOnlyDictionary<string, XmlDoc> Сборка(Assembly сборка) =>
            Сборки.GetOrAdd(сборка, Прочитать);

        /// <summary>Прочитать файл <c>&lt;сборка&gt;.xml</c> рядом со сборкой.</summary>
        private static IReadOnlyDictionary<string, XmlDoc> Прочитать(Assembly сборка)
        {
            var путь = string.IsNullOrEmpty(сборка.Location) ? null : Path.ChangeExtension(сборка.Location, ".xml");

            if (путь is null || !File.Exists(путь))
            {
                return new Dictionary<string, XmlDoc>(StringComparer.Ordinal);
            }

            var члены = new Dictionary<string, XmlDoc>(StringComparer.Ordinal);

            foreach (var член in XDocument.Load(путь).Descendants("member"))
            {
                if (член.Attribute("name")?.Value is not { } имя)
                {
                    continue;
                }

                var параметры = член.Elements("param")
                    .Where(параметр => параметр.Attribute("name") is not null)
                    .ToDictionary(параметр => параметр.Attribute("name")!.Value, Текст, StringComparer.Ordinal);

                члены[имя] = new XmlDoc(член.Element("summary") is { } кратко ? Текст(кратко) : null, параметры);
            }

            return члены;
        }

        /// <summary>Текст элемента без разметки: <c>&lt;c&gt;x&lt;/c&gt;</c> → x, ссылка — коротким именем.</summary>
        internal static string Текст(XElement элемент)
        {
            var текст = new StringBuilder();

            foreach (var узел in элемент.Nodes())
            {
                switch (узел)
                {
                    case XText кусок:
                        текст.Append(кусок.Value);
                        break;
                    case XElement вложенный when вложенный.Name.LocalName is "see" or "seealso":
                        текст.Append(вложенный.Attribute("langword")?.Value
                            ?? КороткоеИмя(вложенный.Attribute("cref")?.Value)
                            ?? Текст(вложенный));
                        break;
                    case XElement вложенный when вложенный.Name.LocalName is "paramref" or "typeparamref":
                        текст.Append(вложенный.Attribute("name")?.Value);
                        break;
                    case XElement вложенный:
                        текст.Append(Текст(вложенный));
                        break;
                }
            }

            return string.Join(' ', текст.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string? КороткоеИмя(string? ссылка)
        {
            if (string.IsNullOrEmpty(ссылка))
            {
                return null;
            }

            var безВида = ссылка.Length > 2 && ссылка[1] == ':' ? ссылка[2..] : ссылка;
            var безПараметров = безВида.Split('(')[0];

            return безПараметров[(безПараметров.LastIndexOf('.') + 1)..];
        }

        /// <summary>Идентификатор документации метода — в той же форме, что пишет компилятор.</summary>
        internal static string DocId(MethodInfo метод)
        {
            var параметры = метод.GetParameters();
            var список = параметры.Length == 0
                ? string.Empty
                : "(" + string.Join(",", параметры.Select(параметр => Параметр(параметр.ParameterType))) + ")";

            return $"M:{Тип(метод.DeclaringType!)}.{метод.Name}{список}";
        }

        private static string Тип(Type тип) => (тип.FullName ?? тип.Name).Replace('+', '.');

        private static string Параметр(Type тип)
        {
            if (тип.IsByRef)
            {
                return Параметр(тип.GetElementType()!) + "@";
            }

            if (тип.IsArray)
            {
                return Параметр(тип.GetElementType()!) + "[]";
            }

            if (тип.IsGenericType)
            {
                var определение = тип.GetGenericTypeDefinition();
                var имя = определение.FullName ?? определение.Name;

                return имя[..имя.IndexOf('`', StringComparison.Ordinal)].Replace('+', '.')
                    + "{" + string.Join(",", тип.GenericTypeArguments.Select(Параметр)) + "}";
            }

            return Тип(тип);
        }
    }
}

/// <summary>Приставка расширений операции в документе OpenAPI.</summary>
public sealed class EndpointDocsOptions
{
    /// <summary>Приставка: <c>{приставка}-module</c>, <c>{приставка}-access</c> и так далее.</summary>
    public string ExtensionPrefix { get; init; } = "x-module";

    /// <summary>Название документа.</summary>
    public string Title { get; init; } = "API";

    /// <summary>Описание документа; не задано — общее, с названиями расширений.</summary>
    public string? Description { get; init; }
}
