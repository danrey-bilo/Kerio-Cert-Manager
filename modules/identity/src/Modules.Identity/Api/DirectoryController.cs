using Modules.Identity.Contracts;
using System.Text.Json.Serialization;
using Modules.Identity.Domain;
using Modules.Core.Contracts.Accounts;
using Modules.Core.Contracts.Authz;
using Modules.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Api;

/// <summary>Субъект справочника: тот, кому можно что-то выдать или написать.</summary>
internal sealed record SubjectOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("email")] string? Email = null,
    [property: JsonPropertyName("department")] string? Department = null);

/// <summary>Кто есть в системе: люди и команды.</summary>
[ApiController]
[Route("api/directory")]
[Modules.Core.Features.Feature("identity.directory")]
internal sealed class DirectoryController(
    AppDbContext db, IEnumerable<IDirectorySource> sources, IdentityModuleOptions options) : ControllerBase
{
    /// <summary>Люди и команды одним списком.</summary>
    [HttpGet]
    // ⚠ ⚠ Своё право, а не «любой вошедший» (решение владельца 2026-10-21, решение 0110).
    [RequirePermission(Permissions.IdentityPermissions.Directory)]
    public async Task<IReadOnlyList<SubjectOut>> List(CancellationToken cancellationToken)
    {
        var строки = await db.Set<User>()
            .Where(user => user.DeletedAt == null)
            .Select(user => new { user.Id, user.DisplayName, user.Email, user.IsActive, user.Department })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // ⚠ Имя может быть не задано — колонка необязательная.
        var адрес = options.DirectoryFields.HasFlag(DirectoryFields.Email);
        var отдел = options.DirectoryFields.HasFlag(DirectoryFields.Department);

        var люди = строки.Select(row => new SubjectOut(
            row.Id,
            Naming.IdentityResources.User,
            string.IsNullOrWhiteSpace(row.DisplayName) ? row.Email.Split('@')[0] : row.DisplayName,
            row.IsActive,
            адрес ? row.Email : null,
            отдел ? row.Department : null));

        // ⚠ Команды и прочих субъектов приносят их владельцы (решение 0108): людей модуль знает сам, команды — модуль доступа.
        var прочие = new List<SubjectOut>();

        foreach (var источник in sources)
        {
            прочие.AddRange((await источник.SubjectsAsync(cancellationToken))
                .Select(subject => new SubjectOut(subject.Id, subject.Type, subject.Name, subject.IsActive)));
        }

        return [.. люди.Concat(прочие).OrderBy(subject => subject.Name, StringComparer.CurrentCulture)];
    }
}
