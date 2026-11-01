using System.Net;
using System.Text.Json;
using Modules.Settings.Catalog;
using Modules.Settings.Contracts;
using Modules.Settings.Permissions;
using Modules.Settings.Reading;
using Modules.Settings.Storage;
using Modules.Settings.Values;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;

namespace Modules.Settings.Api;

/// <summary>Правка системных настроек: проверка, запись и строка журнала.</summary>
[Modules.Core.Features.Feature("settings.admin_api")]
internal sealed class SettingsService(
    AppDbContext db,
    SettingsRepository repository,
    SettingsCatalog catalog,
    SettingsReader reader,
    IPermissionChecker permissions,
    ICurrentUser current,
    IAuditWriter audit,
    IAuthorizationAuditor auditor,
    SettingsModuleOptions? options = null)
{
    /// <summary>Весь справочник вместе с действующими значениями.</summary>
    public async Task<IReadOnlyList<SettingOut>> ListAsync(CancellationToken cancellationToken = default)
    {
        var всё = await reader.AllAsync(cancellationToken);

        return [.. всё.Select(пара => Наружу(пара.Declaration, пара.Value))];
    }

    /// <summary>Применить правки и вернуть обновлённый справочник.</summary>
    public async Task<IReadOnlyList<SettingOut>> UpdateAsync(
        IReadOnlyDictionary<string, JsonElement>? values,
        IReadOnlyList<string>? reset,
        CancellationToken cancellationToken = default)
    {
        var правки = values ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var сбросы = reset ?? [];

        PreferencesService.ОграничитьЧисло(правки.Count + сбросы.Count, catalog.All.Count);

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

            // ⚠ Закреплённое проектом не правится (решение 0112).
            if (options?.PinnedValues.ContainsKey(имя) == true)
            {
                throw new AppException(
                    SettingsErrorCodes.SettingLevelForbidden,
                    "Значение закреплено проектом установки: на экране настроек оно не меняется.",
                    HttpStatusCode.UnprocessableContent,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = имя, ["reason"] = "pinned" });
            }

            // ⚠ Личное предпочтение системным адресом не правится.
            if (объявление.Scope != SettingScope.System)
            {
                throw new AppException(
                    SettingsErrorCodes.SettingLevelForbidden,
                    "Это личное предпочтение: оно правится под /api/me, а не настройками установки.",
                    HttpStatusCode.UnprocessableContent,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = имя });
            }

            объявления[имя] = объявление;
        }

        if (объявления.Values.Any(объявление => объявление.IsSecurity)
            && !await ВторымВопросомAsync(current.Id!.Value, cancellationToken))
        {
            throw new AppException(
                ErrorCodes.PermissionDenied,
                "Настройки защиты правятся отдельным правом.",
                HttpStatusCode.Forbidden,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["permission"] = SettingsPermissions.SecurityManage,
                });
        }

        var упаковано = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (имя, значение) in правки)
        {
            упаковано[имя] = SettingValues.Pack(объявления[имя], значение);
        }

        var прежние = await Прежние(объявления.Values, cancellationToken);

        await SettingsRepository.ПовторПервойЗаписиAsync(db, () => db.InTransactionAsync(
            async token =>
            {
                var изменения = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var (имя, json) in упаковано)
                {
                    // ⚠ ⚠ Строка пишется ВСЕГДА, даже если значение совпало с действующим, и это не лишняя работа.
                    await repository.SetSystemAsync(имя, json, current.Id, token);

                    var стало = SettingValues.Read(объявления[имя], json);

                    // ⚠ А вот СТРОКА ЖУРНАЛА — только при настоящем изменении: журнал не должен наполняться нажатиями «Сохранить».
                    if (Equals(прежние[имя].Value, стало) && прежние[имя].Source == SettingsSource.System)
                    {
                        continue;
                    }

                    изменения[имя] = прежние[имя].Source == SettingsSource.System
                        ? new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["from"] = прежние[имя].Value,
                            ["to"] = стало,
                        }
                        : new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["from"] = прежние[имя].Value,
                            ["to"] = стало,
                            ["source"] = Источник(прежние[имя].Source),
                        };
                }

                foreach (var имя in сбросы.Distinct(StringComparer.Ordinal))
                {
                    if (!await repository.ResetSystemAsync(имя, token))
                    {
                        continue;
                    }

                    изменения[имя] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["from"] = прежние[имя].Value,
                        ["to"] = null,
                        ["reset"] = true,
                    };
                }

                // ⚠ Ничего не изменилось — строки нет.
                if (изменения.Count > 0)
                {
                    audit.Write(new AuditEntry(
                        SettingsActions.SystemUpdate,
                        Changes: изменения));
                }

                await db.SaveChangesAsync(token);

                return изменения.Count;
            },
            cancellationToken));

        // ⚠ Снимок области устарел в момент записи — читаем заново, иначе экран показал бы прежние значения сразу после «Сохранить».
        reader.Забыть();

        return await ListAsync(cancellationToken);
    }

    private async Task<Dictionary<string, SettingValue>> Прежние(
        IEnumerable<ISettingDeclaration> объявления, CancellationToken cancellationToken)
    {
        var итог = new Dictionary<string, SettingValue>(StringComparer.Ordinal);

        foreach (var объявление in объявления)
        {
            итог[объявление.Name] = await reader.ResolveAsync(объявление, cancellationToken);
        }

        return итог;
    }

    /// <summary>Откуда значение действовало до правки — для строки журнала о переводе в базу.</summary>
    private static string Источник(SettingsSource источник) => источник switch
    {
        SettingsSource.Configuration => "configuration",
        SettingsSource.PinnedByProject => "pinned",
        _ => "default",
    };

    /// <summary>Право на настройки защиты — вторым вопросом и с записью решения.</summary>
    private async Task<bool> ВторымВопросомAsync(Guid кто, CancellationToken cancellationToken)
    {
        var можно = await permissions.CanAsync(кто, SettingsPermissions.SecurityManage, cancellationToken: cancellationToken);

        await auditor.RecordAsync(
            кто,
            new AuditDecision(можно, SettingsPermissions.SecurityManage, можно ? "право выдано" : "права нет"),
            null,
            null,
            cancellationToken);

        return можно;
    }

    private static SettingOut Наружу(ISettingDeclaration объявление, SettingValue значение) =>
        new(
            объявление.Name,
            объявление.Title,
            объявление.Description,
            объявление.Category,
            SettingValues.Имя(объявление),
            объявление.Scope == SettingScope.User ? "user" : "system",
            объявление.IsSecurity,
            объявление.IsNullable,
            объявление.RequiresRestart,
            объявление.Minimum,
            объявление.Maximum,
            объявление.Choices.Count > 0 ? объявление.Choices : null,
            значение.Value,
            объявление.DefaultValue,
            значение.Source switch
            {
                SettingsSource.PinnedByProject => "pinned",
                SettingsSource.System => "system",
                SettingsSource.Configuration => "configuration",
                _ => "default",
            });
}
