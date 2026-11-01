using Modules.Core.Errors;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Modules.Hosting;

/// <summary>Вносит конверт ошибки в контракт: он общий на всю систему и обязан быть описан.</summary>
public sealed class ErrorEnvelopeDocument : IOpenApiDocumentTransformer
{
    /// <summary>Имя схемы.</summary>
    private const string Схема = nameof(ErrorResponse);

    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);

        // ⚠ Схема берётся у генератора, а не пишется руками: руками написанная разошлась бы с типом при первой же его правке, и разошлась бы молча.
        var схема = await context.GetOrCreateSchemaAsync(typeof(ErrorResponse), cancellationToken: cancellationToken);

        document.Components.Schemas[Схема] = схема;

        foreach (var путь in document.Paths ?? [])
        {
            foreach (var (_, операция) in путь.Value.Operations ?? [])
            {
                Дописать(операция);
            }
        }
    }

    /// <summary>Дописать операции ответ «всё остальное».</summary>
    private static void Дописать(OpenApiOperation операция)
    {
        операция.Responses ??= new OpenApiResponses();

        if (операция.Responses.ContainsKey("default"))
        {
            return;
        }

        операция.Responses["default"] = new OpenApiResponse
        {
            Description = "Отказ. Единый конверт: машинный код, сообщение, подробности, номер обращения.",
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                ["application/json"] = new()
                {
                    Schema = new OpenApiSchemaReference(Схема),
                },
            },
        };
    }
}
