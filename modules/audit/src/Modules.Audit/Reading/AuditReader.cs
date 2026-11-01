using System.Net;
using Modules.Audit.Domain;
using Modules.Audit.Writing;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Audit.Reading;

/// <summary>Чтение журнала: фильтры и постраничный обход по ключу.</summary>
internal sealed class AuditReader(AppDbContext db)
{
    /// <summary>Сколько строк отдаёт одна страница по умолчанию.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Потолок размера страницы.</summary>
    public const int MaxLimit = 200;

    /// <summary>Одна страница журнала и признак, что есть следующая.</summary>
    public async Task<(IReadOnlyList<AuditLogEntry> Items, bool HasMore)> PageAsync(
        AuditQuery query,
        int limit,
        AuditCursor? after,
        CancellationToken cancellationToken = default)
    {
        var rows = await Filter(query, after)
            .OrderByDescending(entry => entry.Ts)
            .ThenByDescending(entry => entry.Id)
            .Take(limit + 1)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > limit;

        return (hasMore ? rows[..limit] : rows, hasMore);
    }

    /// <summary>Выгрузка: те же фильтры, но без курсора и с жёстким потолком.</summary>
    public async Task<IReadOnlyList<AuditLogEntry>> ExportAsync(
        AuditQuery query, int rowLimit, CancellationToken cancellationToken = default) =>
        await Filter(query, after: null)
            .OrderByDescending(entry => entry.Ts)
            .ThenByDescending(entry => entry.Id)
            .Take(rowLimit)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    /// <summary>Разобрать адрес фильтра.</summary>
    private static IPAddress? Address(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IPAddress.TryParse(value, out var address) ? address : null;

    private IQueryable<AuditLogEntry> Filter(AuditQuery query, AuditCursor? after)
    {
        var rows = db.Set<AuditLogEntry>().AsQueryable();

        if (query.ActorId is { } actorId)
        {
            rows = rows.Where(entry => entry.ActorId == actorId);
        }

        if (!string.IsNullOrEmpty(query.Action))
        {
            rows = rows.Where(entry => entry.Action == query.Action);
        }

        if (query.Actions is { } actions)
        {
            rows = rows.Where(entry => actions.Contains(entry.Action));
        }

        if (!string.IsNullOrEmpty(query.ResourceType))
        {
            rows = rows.Where(entry => entry.ResourceType == query.ResourceType);
        }

        if (query.ResourceId is { } resourceId)
        {
            rows = rows.Where(entry => entry.ResourceId == resourceId);
        }

        if (query.Result is { } result)
        {
            var value = AuditRow.Map(result);
            rows = rows.Where(entry => entry.Result == value);
        }

        if (query.SessionId is { } session)
        {
            rows = rows.Where(entry => entry.SessionId == session);
        }

        if (!string.IsNullOrEmpty(query.RequestId))
        {
            rows = rows.Where(entry => entry.RequestId == query.RequestId);
        }

        if (Address(query.ActorIp) is { } адрес)
        {
            // ⚠ Точный адрес, а не подсеть. Поиск по подсети (&lt;&lt;=).
            rows = rows.Where(entry => entry.ActorIp != null && entry.ActorIp.Equals(адрес));
        }

        if (query.Since is { } since)
        {
            rows = rows.Where(entry => entry.Ts >= since);
        }

        if (query.Until is { } until)
        {
            rows = rows.Where(entry => entry.Ts < until);
        }

        if (after is { } cursor)
        {
            rows = rows.Where(entry =>
                entry.Ts <= cursor.Ts && (entry.Ts < cursor.Ts || entry.Id < cursor.Id));
        }

        return rows;
    }
}
