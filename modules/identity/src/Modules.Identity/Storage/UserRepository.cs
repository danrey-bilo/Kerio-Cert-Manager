using Modules.Identity.Domain;
using Modules.Core.Api;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Storage;

/// <summary>Единственная точка доступа к таблицам модуля.</summary>
internal sealed class UserRepository(AppDbContext db)
{
    /// <summary>Активные суперпользователи — одним запросом, который их же и блокирует.</summary>
    private const string LockActiveSuperusers =
        "SELECT * FROM users WHERE deleted_at IS NULL AND is_active AND is_superuser FOR UPDATE";

    /// <summary>Живые пользователи.</summary>
    public IQueryable<User> Live => db.Set<User>().Where(user => user.DeletedAt == null);

    /// <summary>Найти живого по идентификатору.</summary>
    public Task<User?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Live.FirstOrDefaultAsync(user => user.Id == id, cancellationToken);

    /// <summary>Прочитать живого по идентификатору — без трекинга и без хеша пароля.</summary>
    public Task<UserRow?> ReadAsync(Guid id, CancellationToken cancellationToken = default) =>
        Live
            .Where(user => user.Id == id)
            .Select(user => new UserRow(
                user.Id,
                user.Email,
                user.DisplayName,
                user.IsActive,
                user.IsSuperuser,
                user.MustChangePassword,
                user.ActivatedAt,
                user.LockedUntil,
                user.CreatedAt,
                user.UpdatedAt,
                user.Department))
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Найти живого по адресу.</summary>
    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        Live.FirstOrDefaultAsync(user => user.Email == email, cancellationToken);

    /// <summary>Есть ли живой с таким адресом.</summary>
    public Task<bool> EmailTakenAsync(string email, CancellationToken cancellationToken = default) =>
        Live.AnyAsync(user => user.Email == email, cancellationToken);

    /// <summary>Страница списка людей: поиск, потолок, курсор.</summary>
    private const int DefaultLimit = 50;

    /// <summary>Больше этого не отдаём: страница — не выгрузка.</summary>
    private const int MaxLimit = 200;

    /// <summary>Читающая проекция: та же, что у страницы, но без выполнения.</summary>
    internal IQueryable<UserRow> PageQuery(string? query, string? after)
    {
        var rows = Live;

        if (!string.IsNullOrWhiteSpace(query))
        {
            // ⚠ Не ILike: он есть только у Npgsql, а модуль провайдера не знает и знать не должен.
            var needle = query.Trim();
            var lowered = needle.ToLowerInvariant();

            rows = rows.Where(user =>
                user.Email.Contains(needle)
                || (user.DisplayName != null && user.DisplayName.ToLower().Contains(lowered)));
        }

        if (after is { } cursor)
        {
            rows = rows.Where(user => string.Compare(user.Email, cursor) > 0);
        }

        // ⚠ ⚠ Проекция, а не сущность: `password_hash` из базы не выходит (вскрытие 27.7).
        return rows
            .OrderBy(user => user.Email)
            .Select(user => new UserRow(
                user.Id,
                user.Email,
                user.DisplayName,
                user.IsActive,
                user.IsSuperuser,
                user.MustChangePassword,
                user.ActivatedAt,
                user.LockedUntil,
                user.CreatedAt,
                user.UpdatedAt,
                user.Department))
            .AsNoTracking();
    }

    public async Task<(List<UserRow> Items, string? NextCursor)> PageAsync(
        string? query, string? after, int limit, CancellationToken cancellationToken = default)
    {
        // ⚠ Строгая проверка, а не подрезание: одно правило на всю систему, разбор — в `PageLimit`.
        var page = PageLimit.Require(limit, DefaultLimit, MaxLimit);

        var items = await PageQuery(query, after)
            .Take(page + 1)
            .ToListAsync(cancellationToken);

        if (items.Count <= page)
        {
            return (items, null);
        }

        items.RemoveAt(page);

        return (items, UserCursor.Encode(items[^1].Email));
    }

    /// <summary>Добавить в текущую единицу работы.</summary>
    public void Add(User user) => db.Add(user);

    /// <summary>Идентификаторы активных суперпользователей, <b>с блокировкой их строк</b>.</summary>
    public async Task<HashSet<Guid>> LockActiveSuperusersAsync(
        bool onlySignInCapable = false, CancellationToken cancellationToken = default)
    {
        var locked = await db.Set<User>()
            .FromSqlRaw(LockActiveSuperusers)
            .ToListAsync(cancellationToken);

        return [.. locked.Where(user => !onlySignInCapable || CanSignIn(user)).Select(user => user.Id)];
    }

    /// <summary>Может ли учётная запись войти: активирована и пароль не заглушка.</summary>
    internal static bool CanSignIn(User user) =>
        user.ActivatedAt is not null && user.PasswordHash != Users.UserService.Unusable;
}
