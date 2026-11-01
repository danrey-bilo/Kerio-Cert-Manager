using Modules.Settings.Domain;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Settings.Storage;

/// <summary>Единственная точка доступа к обеим таблицам настроек.</summary>
internal sealed class SettingsRepository(AppDbContext db, TimeProvider clock)
{
    /// <summary>Все заданные системные значения: имя → JSON.</summary>
    public async Task<IReadOnlyDictionary<string, string>> SystemAsync(
        CancellationToken cancellationToken = default) =>
        await db.Set<SystemSetting>()
            .AsNoTracking()
            .ToDictionaryAsync(строка => строка.Key, строка => строка.ValueJson, StringComparer.Ordinal, cancellationToken);

    /// <summary>Все предпочтения одного человека: имя → JSON.</summary>
    public async Task<IReadOnlyDictionary<string, string>> PreferencesAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        await db.Set<UserPreference>()
            .AsNoTracking()
            .Where(строка => строка.UserId == userId)
            .ToDictionaryAsync(строка => строка.Key, строка => строка.ValueJson, StringComparer.Ordinal, cancellationToken);

    /// <summary>Выполнить правку; первая запись ключа разом с соседней — повтор один раз.</summary>
    internal static async Task ПовторПервойЗаписиAsync(AppDbContext db, Func<Task> правка)
    {
        try
        {
            await правка();
        }
        catch (DbUpdateException ошибка) when (ПервичныйКлюч(ошибка))
        {
            db.ChangeTracker.Clear();

            await правка();
        }
    }

    private static bool ПервичныйКлюч(DbUpdateException ошибка) =>
        ошибка.InnerException?.Message is { } текст
        && (текст.Contains($"pk_{SettingsEntities.UserTable}", StringComparison.Ordinal)
            || текст.Contains($"pk_{SettingsEntities.SystemTable}", StringComparison.Ordinal));

    /// <summary>Задать системное значение.</summary>
    public async Task SetSystemAsync(
        string key, string valueJson, Guid? updatedBy, CancellationToken cancellationToken = default)
    {
        var строка = await db.Set<SystemSetting>()
            .FirstOrDefaultAsync(запись => запись.Key == key, cancellationToken);

        if (строка is null)
        {
            db.Set<SystemSetting>().Add(new SystemSetting
            {
                Key = key,
                ValueJson = valueJson,
                UpdatedBy = updatedBy,
                UpdatedAt = clock.GetUtcNow(),
            });

            return;
        }

        строка.ValueJson = valueJson;
        строка.UpdatedBy = updatedBy;
        строка.UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>Убрать заданное системное значение — настройка возвращается к умолчанию.</summary>
    public async Task<bool> ResetSystemAsync(string key, CancellationToken cancellationToken = default)
    {
        var строка = await db.Set<SystemSetting>()
            .FirstOrDefaultAsync(запись => запись.Key == key, cancellationToken);

        if (строка is null)
        {
            return false;
        }

        db.Set<SystemSetting>().Remove(строка);
        return true;
    }

    /// <summary>Задать личное предпочтение.</summary>
    public async Task SetPreferenceAsync(
        Guid userId, string key, string valueJson, CancellationToken cancellationToken = default)
    {
        var строка = await db.Set<UserPreference>()
            .FirstOrDefaultAsync(
                запись => запись.UserId == userId && запись.Key == key, cancellationToken);

        if (строка is null)
        {
            db.Set<UserPreference>().Add(new UserPreference
            {
                UserId = userId,
                Key = key,
                ValueJson = valueJson,
                UpdatedAt = clock.GetUtcNow(),
            });

            return;
        }

        строка.ValueJson = valueJson;
        строка.UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>Убрать личное предпочтение — действует слой ниже.</summary>
    public async Task<bool> ResetPreferenceAsync(
        Guid userId, string key, CancellationToken cancellationToken = default)
    {
        var строка = await db.Set<UserPreference>()
            .FirstOrDefaultAsync(
                запись => запись.UserId == userId && запись.Key == key, cancellationToken);

        if (строка is null)
        {
            return false;
        }

        db.Set<UserPreference>().Remove(строка);
        return true;
    }
}
