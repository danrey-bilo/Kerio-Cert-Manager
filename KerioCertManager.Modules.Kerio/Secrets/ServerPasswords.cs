using KerioCertManager.Modules.Kerio.Domain;
using Microsoft.EntityFrameworkCore;
using Modules.Core.Data;
using Modules.Crypto;

namespace KerioCertManager.Modules.Kerio.Secrets;

/// <summary>Пароль учётной записи Kerio, запечатанный и привязанный к адресу и логину (<see cref="BoundSecret"/>).</summary>
internal sealed class ServerPasswords(ISecretEnvelope envelope)
{
    /// <summary>⚠ Контекст AEAD входит в шифротекст — не менять.</summary>
    internal const string Контекст = "kerio-cert-manager:kerio:server-password";

    /// <summary>Цель привязки: нормальный адрес и логин без учёта регистра.</summary>
    internal static string Target(string baseUrl, string username) =>
        $"{NormalizeUrl(baseUrl)}|{username.Trim().ToLowerInvariant()}";

    /// <summary>Адрес в нормальном виде: <c>https://host:port</c> строчными, без пути и «/».</summary>
    internal static string NormalizeUrl(string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        return Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            ? $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}"
            : baseUrl.Trim().ToLowerInvariant();
    }

    /// <summary>Запечатать пароль в строку сервера.</summary>
    public void Put(KerioServer server, string password)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var ключ = envelope.CreateDataKey();
        server.Password = envelope.SealText(ключ, new BoundSecret(Target(server.BaseUrl, server.Username), password).Pack(), Контекст).Bytes;
        server.DataKey = ключ.Bytes;
    }

    /// <summary>Снять пароль: сервер ждёт нового.</summary>
    public static void Clear(KerioServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        server.Password = null;
        server.DataKey = null;
    }

    /// <summary>Пароль для текущего адреса и логина сервера.</summary>
    public string Open(KerioServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (server is not { Password: { } запечатано, DataKey: { } ключ })
        {
            throw new InvalidOperationException("Пароль учётной записи Kerio не задан.");
        }

        var открыто = envelope.OpenText(new WrappedDataKey(ключ), new SealedSecret(запечатано), Контекст);
        var привязка = BoundSecret.Unpack(открыто);

        if (привязка is null || !привязка.IsFor(Target(server.BaseUrl, server.Username)))
        {
            throw new InvalidOperationException(
                "Пароль был введён для другого адреса или логина Kerio. Введите пароль заново: он уходит только туда, "
                + "для чего его ввели.");
        }

        return привязка.Secret;
    }
}

/// <summary>Перекладывает ключи данных паролей Kerio под текущий мастер-ключ; пароли не открываются.</summary>
internal sealed class ServerPasswordRewrap(AppDbContext db, ISecretEnvelope envelope) : IRewrapTarget
{
    public string Name => "kerio_servers";

    public Task<RewrapBatch> RewrapAsync(int batch, string? after = null, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async token =>
            {
                var после = after is null ? Guid.Empty : Guid.Parse(after);

                var строки = await db.Set<KerioServer>()
                    .AsNoTracking()
                    .Where(с => с.Id > после)
                    .OrderBy(с => с.Id)
                    .Take(batch)
                    .Select(с => new { с.Id, с.DataKey })
                    .ToListAsync(token);

                var переложено = 0;

                foreach (var строка in строки)
                {
                    if (строка.DataKey is not { } было || !envelope.NeedsRewrap(new WrappedDataKey(было)))
                    {
                        continue;
                    }

                    var стало = envelope.Rewrap(new WrappedDataKey(было)).Bytes;

                    // ⚠ Условно по прежней обёртке: пароль, перезаданный между чтением и записью, не ломается.
                    переложено += await db.Set<KerioServer>()
                        .Where(с => с.Id == строка.Id && с.DataKey == было)
                        .ExecuteUpdateAsync(set => set.SetProperty(с => с.DataKey, стало), token);
                }

                return new RewrapBatch(
                    строки.Count, переложено, строки.Count == batch ? строки[^1].Id.ToString() : null);
            },
            cancellationToken);
}
