using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Modules.Hosting;

/// <summary>Описывает тело заливки файла: сам ASP.NET его не находит.</summary>
public sealed class MultipartUploadDocument : IOpenApiDocumentTransformer
{
    /// <summary>Пути, тело которых описывается здесь.</summary>
    private static readonly string[] Заливки =
    [
        "/api/credentials/{credentialId}/attachments",
    ];

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var путь in Заливки)
        {
            if (document.Paths?.TryGetValue(путь, out var описание) != true
                || описание?.Operations is null
                || !описание.Operations.TryGetValue(HttpMethod.Post, out var операция))
            {
                continue;
            }

            операция.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Description = "Форма с единственной частью `file` — содержимым файла.",
                Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
                {
                    ["multipart/form-data"] = new()
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object,
                            Required = new HashSet<string>(StringComparer.Ordinal) { "file" },
                            Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                            {
                                ["file"] = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.String,
                                    Format = "binary",
                                    Description =
                                        "Содержимое файла. Имя берётся из `filename` части и "
                                        + "очищается при приёме: только листовое имя, без "
                                        + "разделителей пути и управляющих символов.",
                                },
                            },
                        },
                    },
                },
            };
        }

        return Task.CompletedTask;
    }
}
