using Modules.Mail.Domain;
using Modules.Core.Contracts.Authz;
using Modules.Crypto;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Modules.Mail.Secrets;

/// <summary>Имена секретов установки.</summary>
internal static class SystemSecretNames
{
    /// <summary>Пароль к SMTP-серверу.</summary>
    public const string SmtpPassword = "smtp_password";

    /// <summary>Все законные имена.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { SmtpPassword };
}

/// <summary>Секреты установки: запечатать, открыть, убрать.</summary>
internal sealed class SystemSecrets(
    AppDbContext db,
    ISecretEnvelope envelope,
    ICurrentUser current,
    TimeProvider clock,
    ILogger<SystemSecrets>? log = null)
{
    /// <summary>Приставка контекста AEAD.</summary>
    private const string Контекст = "eams:system-secret:";

    /// <summary>Открыть секрет или <c>null</c>, если он не задан.</summary>
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var открыто = await ОткрытьAsync(key, cancellationToken);

        return открыто is null ? null : BoundSecret.Unpack(открыто.Value.Текст)?.Secret ?? открыто.Value.Текст;
    }

    /// <summary>Открыть секрет для адреса или <c>null</c>, если он не задан (решение 0120).</summary>
    public async Task<string?> GetForAsync(string key, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (await ОткрытьAsync(key, cancellationToken) is not { } открыто)
        {
            return null;
        }

        var привязка = BoundSecret.Unpack(открыто.Текст);

        if (привязка is null)
        {
            await ПривязатьAsync(key, target, открыто, cancellationToken);

            return открыто.Текст;
        }

        // ⚠ ⚠ Другой адрес — отказ до соединения: до подменённого сервера не доходит ни соединения, ни пароля.
        if (!привязка.IsFor(target))
        {
            throw new Sending.MailConfigurationException(
                "Пароль SMTP вводили для другого сервера, порта, защиты или учётной записи: настройки почты "
                + "сменились после ввода пароля. Введите пароль SMTP заново — он уходит только туда, для чего его ввели.");
        }

        return привязка.Secret;
    }

    /// <summary>Привязать голое значение, заданное до привязки, к адресу <paramref name="target"/> (решение 0120).</summary>
    public async Task<bool> BindLegacyAsync(string key, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        return await ОткрытьAsync(key, cancellationToken) is { } открыто
            && BoundSecret.Unpack(открыто.Текст) is null
            && await ПривязатьAsync(key, target, открыто, cancellationToken);
    }

    /// <summary>Перепечатать голое значение вместе с адресом — условным обновлением, тем же ключом данных.</summary>
    private async Task<bool> ПривязатьAsync(
        string key, string target, (SystemSecret Строка, string Текст) открыто, CancellationToken cancellationToken)
    {
        var перепечатано = envelope.SealText(
            new WrappedDataKey(открыто.Строка.DataKey), new BoundSecret(target, открыто.Текст).Pack(), Контекст + key).Bytes;
        var было = открыто.Строка.Sealed;

        var перепечатан = await db.Set<SystemSecret>()
            .Where(с => с.Key == key && с.Sealed == было)
            .ExecuteUpdateAsync(set => set.SetProperty(с => с.Sealed, перепечатано), cancellationToken);

        // ⚠ Имя секрета и факт — без адреса и тем более без значения (правило 14).
        if (перепечатан == 1)
        {
            log?.LogWarning("system_secret_bound_on_first_use {Key}", key);
        }

        return перепечатан == 1;
    }

    /// <summary>Строка секрета и её открытый текст; <c>null</c> — секрет не задан.</summary>
    private async Task<(SystemSecret Строка, string Текст)?> ОткрытьAsync(string key, CancellationToken cancellationToken)
    {
        Проверить(key);

        var строка = await db.Set<SystemSecret>()
            .AsNoTracking()
            .FirstOrDefaultAsync(с => с.Key == key, cancellationToken);

        return строка is null
            ? null
            : (строка, envelope.OpenText(new WrappedDataKey(строка.DataKey), new SealedSecret(строка.Sealed), Контекст + key));
    }

    /// <summary>Задать секрет.</summary>
    public Task PutAsync(string key, string value, CancellationToken cancellationToken = default) =>
        ЗаписатьAsync(key, value, cancellationToken);

    /// <summary>Задать секрет, привязанный к адресу, для которого его вводят (решение 0120).</summary>
    public Task PutAsync(string key, string value, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(target);

        return ЗаписатьAsync(key, new BoundSecret(target, value).Pack(), cancellationToken);
    }

    private async Task ЗаписатьAsync(string key, string value, CancellationToken cancellationToken)
    {
        Проверить(key);

        var ключ = envelope.CreateDataKey();
        var запечатано = envelope.SealText(ключ, value, Контекст + key);

        var строка = await db.Set<SystemSecret>()
            .FirstOrDefaultAsync(с => с.Key == key, cancellationToken);

        if (строка is null)
        {
            db.Set<SystemSecret>().Add(new SystemSecret
            {
                Key = key,
                Sealed = запечатано.Bytes,
                DataKey = ключ.Bytes,
                UpdatedAt = clock.GetUtcNow(),
                UpdatedBy = current.Id,
            });

            return;
        }

        строка.Sealed = запечатано.Bytes;
        строка.DataKey = ключ.Bytes;

        // ⚠ Время проставляется ЗДЕСЬ, а не серверным умолчанием.
        строка.UpdatedAt = clock.GetUtcNow();
        строка.UpdatedBy = current.Id;
    }

    /// <summary>Убрать секрет.</summary>
    public async Task<bool> DropAsync(string key, CancellationToken cancellationToken = default)
    {
        Проверить(key);

        var строка = await db.Set<SystemSecret>()
            .FirstOrDefaultAsync(с => с.Key == key, cancellationToken);

        if (строка is null)
        {
            return false;
        }

        db.Set<SystemSecret>().Remove(строка);
        return true;
    }

    /// <summary>Задан ли секрет — без его открытия.</summary>
    public async Task<bool> HasAsync(string key, CancellationToken cancellationToken = default)
    {
        Проверить(key);

        return await db.Set<SystemSecret>().AnyAsync(с => с.Key == key, cancellationToken);
    }

    private static void Проверить(string key)
    {
        if (!SystemSecretNames.All.Contains(key))
        {
            throw new ArgumentOutOfRangeException(
                nameof(key),
                key,
                "Имя не из закрытого набора секретов установки. Новое имя заводится константой "
                + "рядом с набором, а не строкой в вызове: иначе таблица становится свалкой.");
        }
    }
}
