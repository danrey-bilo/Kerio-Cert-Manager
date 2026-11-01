using System.Net;
using System.Text.Json;
using Modules.Settings.Catalog;
using Modules.Settings.Domain;
using Modules.Settings.Reading;
using Modules.Settings.Storage;
using Modules.Settings.Values;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Modules.Settings.Contracts;

namespace Modules.Settings.Api;

/// <summary>Личные предпочтения: чтение поверх системного слоя и правка.</summary>
internal sealed class PreferencesService(
    AppDbContext db,
    SettingsRepository repository,
    SettingsCatalog catalog,
    SettingsReader reader)
{
    /// <summary>Предпочтения человека вместе с действующими значениями.</summary>
    public async Task<PreferencesOut> ForAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var свои = await repository.PreferencesAsync(userId, cancellationToken);
        var личные = ЛичныеКлючи();

        var заданное = new Dictionary<string, object?>(StringComparer.Ordinal);
        var действует = new Dictionary<string, object?>(StringComparer.Ordinal);
        var справочник = new List<SettingOut>(личные.Count);

        foreach (var объявление in личные)
        {
            var ниже = await reader.ResolveAsync(объявление, cancellationToken);

            // ⚠ Годность — отдельным вопросом (замечание ревью к PR #32): негодное своё не выдаёт себя за заданный null и не перекрывает общее значение.
            if (свои.TryGetValue(объявление.Name, out var json)
                && SettingValues.TryRead(объявление, json, out var значение))
            {
                заданное[объявление.Name] = значение;
                действует[объявление.Name] = значение;
                справочник.Add(Наружу(объявление, значение, "user"));
                continue;
            }

            действует[объявление.Name] = ниже.Value;
            справочник.Add(Наружу(объявление, ниже.Value, Источник(ниже.Source)));
        }

        return new PreferencesOut(userId, заданное, действует, справочник, await КогдаAsync(userId, cancellationToken));
    }

    /// <summary>Задать своё и вернуть заданное к общему.</summary>
    public async Task<PreferencesOut> UpdateAsync(
        Guid userId,
        IReadOnlyDictionary<string, JsonElement>? values,
        IReadOnlyList<string>? reset,
        CancellationToken cancellationToken = default)
    {
        var правки = values ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var сбросы = reset ?? [];

        // ⚠ Законному запросу элементов не больше, чем объявлено ключей (ревизия 2026-10-26, решение 0118).
        ОграничитьЧисло(правки.Count + сбросы.Count, catalog.All.Count);

        var объявления = new Dictionary<string, ISettingDeclaration>(StringComparer.Ordinal);

        foreach (var имя in правки.Keys.Concat(сбросы))
        {
            if (объявления.ContainsKey(имя))
            {
                continue;
            }

            var объявление = catalog.Find(имя)
                ?? throw new AppException(
                    SettingsErrorCodes.UnknownSetting,
                    "Такой настройки не существует.",
                    HttpStatusCode.UnprocessableContent,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = имя });

            // ⚠ 403, а не 422: человек просит изменить настройку УСТАНОВКИ через свой адрес.
            if (объявление.Scope != SettingScope.User)
            {
                throw new AppException(
                    SettingsErrorCodes.SettingLevelForbidden,
                    "Это настройка установки: её правит администратор, а не владелец учётной записи.",
                    HttpStatusCode.Forbidden,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = имя });
            }

            объявления[имя] = объявление;
        }

        var упаковано = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (имя, значение) in правки)
        {
            упаковано[имя] = SettingValues.Pack(объявления[имя], значение);
        }

        await SettingsRepository.ПовторПервойЗаписиAsync(db, () => db.InTransactionAsync(
            async token =>
            {
                foreach (var (имя, json) in упаковано)
                {
                    await repository.SetPreferenceAsync(userId, имя, json, token);
                }

                // ⚠ По различным именам (решение 0118): повтор в `reset` — запрос в базу на каждый.
                foreach (var имя in сбросы.Distinct(StringComparer.Ordinal))
                {
                    await repository.ResetPreferenceAsync(userId, имя, token);
                }

                await db.SaveChangesAsync(token);
            },
            cancellationToken));

        reader.Забыть();

        return await ForAsync(userId, cancellationToken);
    }

    /// <summary>Больше элементов, чем объявлено ключей, законному запросу не нужно.</summary>
    internal static void ОграничитьЧисло(int элементов, int ключей)
    {
        if (элементов > ключей)
        {
            throw new AppException(
                ErrorCodes.Validation,
                "В правке больше элементов, чем настроек.",
                HttpStatusCode.UnprocessableContent,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["maximum"] = ключей });
        }
    }

    /// <summary>Объявления, которые человек правит себе сам.</summary>
    private List<ISettingDeclaration> ЛичныеКлючи() =>
        [.. catalog.All.Where(объявление => объявление.Scope == SettingScope.User)];

    /// <summary>Когда человек правил свои предпочтения в последний раз.</summary>
    private async Task<DateTimeOffset?> КогдаAsync(Guid userId, CancellationToken cancellationToken)
    {
        var времена = await db.Set<UserPreference>()
            .AsNoTracking()
            .Where(строка => строка.UserId == userId)
            .Select(строка => (DateTimeOffset?)строка.UpdatedAt)
            .ToListAsync(cancellationToken);

        return времена.Count == 0 ? null : времена.Max();
    }

    private static string Источник(SettingsSource источник) => источник switch
    {
        SettingsSource.PinnedByProject => "pinned",
        SettingsSource.System => "system",
        SettingsSource.Configuration => "configuration",
        _ => "default",
    };

    private static SettingOut Наружу(ISettingDeclaration объявление, object? значение, string источник) =>
        new(
            объявление.Name,
            объявление.Title,
            объявление.Description,
            объявление.Category,
            SettingValues.Имя(объявление),
            "user",
            объявление.IsSecurity,
            объявление.IsNullable,
            объявление.RequiresRestart,
            объявление.Minimum,
            объявление.Maximum,
            объявление.Choices.Count > 0 ? объявление.Choices : null,
            значение,
            объявление.DefaultValue,
            источник);
}
