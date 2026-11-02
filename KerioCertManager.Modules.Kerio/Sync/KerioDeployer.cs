using System.Text;
using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.KeyManager.Contracts;
using Microsoft.Extensions.Logging;
using Modules.Core.Errors;

namespace KerioCertManager.Modules.Kerio.Sync;

/// <summary>Шаги установки — по ним видно в панели, где остановилось.</summary>
internal static class DeploySteps
{
    public const string Fetch = "fetch";
    public const string Validate = "validate";
    public const string Login = "login";
    public const string Inventory = "inventory";
    public const string DryRun = "dry_run";
    public const string UploadKey = "upload_key";
    public const string ImportKey = "import_key";
    public const string UploadCertificate = "upload_certificate";
    public const string ImportCertificate = "import_certificate";
    public const string Locate = "locate";
    public const string Activate = "activate";
    public const string Verify = "verify";
    public const string Rollback = "rollback";
    public const string RestoreDefault = "restore_default";
    public const string Cleanup = "cleanup";
    public const string Done = "done";
}

/// <summary>Чем кончилась установка.</summary>
internal sealed record DeployResult(
    string Outcome,
    string? Sha256,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    string? KerioId,
    string? PreviousKerioId,
    string? PreviousSha256,
    string? FailedStep,
    string? Error,
    bool RollbackConfirmed = false);

/// <summary>Параметры одной установки.</summary>
internal sealed record DeployRequest(
    KerioServer Server,
    SourceCertificate Source,
    bool Manual,
    bool DryRun,
    TimeSpan VerifyTimeout,
    bool RequireRollbackTarget = false);

/// <summary>Отказ шага установки.</summary>
internal sealed class DeployStepException(string step, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Step { get; } = step;
}

/// <summary>Ставит сертификат из Key Manager Plus на Kerio Connect и убеждается, что он встал.</summary>
internal sealed partial class KerioDeployer(
    ICertificateSource source,
    IKerioConnector connector,
    ITlsInspector tls,
    TimeProvider clock,
    ILogger<KerioDeployer> log)
{
    private static readonly TimeSpan ШагПроверки = TimeSpan.FromSeconds(3);

    /// <summary>Сколько ждать прежнего при откате из-за остановки сервиса: хост ждёт не дольше двух минут.</summary>
    internal static readonly TimeSpan ОткатПриОстановке = TimeSpan.FromSeconds(40);

    public async Task<DeployResult> DeployAsync(
        DeployRequest request, Func<DeployStep, Task> journal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(journal);

        var server = request.Server;
        var шаг = DeploySteps.Fetch;

        CertificateMaterial? выгрузка = null;
        CertificateBundle? пара = null;
        IKerioSession? сессия = null;

        string? прежний = null;
        string? прежнийSha = server.ServedSha256;
        string? новый = null;
        var активирован = false;
        var проверен = false;
        var импортированЗдесь = false;

        // ⚠ Журнал — не установка: сбой записи (база мигнула, строку сервера удалили) не должен оборвать откат.
        async Task Отметить(string step, string message, bool ok = true)
        {
            шаг = step;

            try
            {
                await journal(new DeployStep(clock.GetUtcNow(), step, message, ok));
            }
            catch (Exception ошибка)
            {
                log.LogWarning("kerio_deploy_journal_failed {Server} {Step} {Error}", server.Name, step, SafeError.Describe(ошибка));
            }
        }

        try
        {
            await Отметить(DeploySteps.Fetch, $"Выгрузка {request.Source.CommonName} (серийный {request.Source.SerialNumber}) из Key Manager Plus.");

            try
            {
                выгрузка = await source.FetchAsync(request.Source.CommonName, request.Source.SerialNumber, cancellationToken);
            }
            catch (CertificateSourceException ошибка)
            {
                throw new DeployStepException(DeploySteps.Fetch, ошибка.Message, ошибка);
            }

            await Отметить(DeploySteps.Validate, "Проверка ключа, имени, срока и цепочки.");

            try
            {
                пара = CertificateBundle.Build(выгрузка, server.CommonName ?? request.Source.CommonName, clock.GetUtcNow());
            }
            catch (BundleException ошибка)
            {
                throw new DeployStepException(DeploySteps.Validate, ошибка.Message, ошибка);
            }
            finally
            {
                выгрузка.Dispose();
            }

            if (server.InstalledNotBefore is { } было && пара.NotBefore < было.AddMinutes(-1))
            {
                if (!request.Manual)
                {
                    throw new DeployStepException(
                        DeploySteps.Validate,
                        $"Выпуск из Key Manager Plus ({пара.NotBefore:yyyy-MM-dd}) старше установленного ({было:yyyy-MM-dd}): "
                        + "сам сервис откат не делает. Установите вручную, если это намеренно.");
                }

                await Отметить(DeploySteps.Validate, "Выпуск старше установленного — ставится по решению администратора.");
            }

            await Отметить(
                DeploySteps.Validate,
                $"Сертификат {пара.Subject} от {пара.Issuer}, до {пара.NotAfter:yyyy-MM-dd}, SHA-256 {Fingerprints.Pretty(пара.Sha256)}."
                + (пара.Intermediates.Count > 0
                    ? $" Промежуточных в цепочке: {пара.Intermediates.Count}."
                    : " Промежуточных в цепочке нет.")
                + (пара.ChainComplete ? string.Empty : " ⚠ Цепочка до корня не собрана — клиенты могут не доверять серверу."),
                ok: true);

            await Отметить(DeploySteps.Login, $"Вход в Kerio {server.BaseUrl} учётной записью {server.Username}.");

            try
            {
                сессия = await connector.OpenAsync(server, [пара.Sha256], cancellationToken);
            }
            catch (KerioException ошибка)
            {
                throw new DeployStepException(DeploySteps.Login, ошибка.Message, ошибка);
            }

            await Отметить(DeploySteps.Inventory, "Поиск сертификата, который сервер подаёт сейчас.");

            var хранилище = await сессия.CertificatesAsync(cancellationToken);
            var отпечатки = new Dictionary<string, string>(StringComparer.Ordinal);

            var подаётся = await ПодаётсяAsync(server, cancellationToken);
            прежнийSha = подаётся ?? прежнийSha;

            if (прежнийSha is not null)
            {
                прежний = await НайтиAsync(сессия, хранилище, прежнийSha, отпечатки, cancellationToken);
            }

            var уже = await НайтиAsync(сессия, хранилище, пара.Sha256, отпечатки, cancellationToken);

            await Отметить(
                DeploySteps.Inventory,
                $"В хранилище Kerio сертификатов: {хранилище.Count}."
                + (прежний is null ? " Подаётся сертификат, которого нет в хранилище (встроенный или неизвестный)." : $" Сейчас подаётся «{прежний}».")
                + (уже is null ? string.Empty : $" Новый уже в хранилище как «{уже}»."));

            if (request.RequireRollbackTarget && прежнийSha is not null && прежний is null
                && !string.Equals(прежнийSha, пара.Sha256, StringComparison.Ordinal))
            {
                throw new DeployStepException(
                    DeploySteps.Inventory,
                    "Сертификат, который сервер подаёт сейчас, не найден в хранилище Kerio: при неудаче вернуть его было бы "
                    + "нельзя. Сам сервис так не ставит — установите кнопкой «Обновить» у сертификата во вкладке «Обзор», если это ожидаемо.");
            }

            if (request.DryRun)
            {
                await Отметить(DeploySteps.DryRun, "Пробный прогон: всё готово к установке, Kerio не изменён.");

                return new DeployResult(
                    DeployOutcomes.DryRun, пара.Sha256, пара.NotBefore, пара.NotAfter, null, прежний, прежнийSha, null, null);
            }

            if (уже is null)
            {
                await Отметить(DeploySteps.UploadKey, "Загрузка закрытого ключа.");
                var файлКлюча = await сессия.UploadAsync(пара.PrivateKeyPem, "kcm.key", cancellationToken);

                await Отметить(DeploySteps.ImportKey, "Импорт закрытого ключа.");
                var ключ = await сессия.ImportPrivateKeyAsync(файлКлюча, cancellationToken);

                if (ключ.NeedPassword)
                {
                    throw new DeployStepException(DeploySteps.ImportKey, "Kerio просит пароль к ключу, который сервис передал открытым.");
                }

                await Отметить(DeploySteps.UploadCertificate, "Загрузка сертификата с цепочкой.");
                var файлСертификата = await сессия.UploadAsync(
                    пара.CertificatePem, $"{ИмяФайла(пара.Subject)}.crt", cancellationToken);

                await Отметить(DeploySteps.ImportCertificate, "Импорт сертификата.");

                // ⚠ С этого места клиенты по имени из SNI могут уже получать новый: любой отказ дальше — через откат.
                активирован = true;
                импортированЗдесь = true;
                await сессия.ImportCertificateAsync(ключ.Id, файлСертификата, string.Empty, cancellationToken);

                await Отметить(DeploySteps.Locate, "Поиск импортированного сертификата в хранилище Kerio.");
                var после = await сессия.CertificatesAsync(cancellationToken);
                var появились = после.Where(с => хранилище.All(б => б.Id != с.Id)).ToList();

                новый = await НайтиAsync(сессия, появились, пара.Sha256, отпечатки, cancellationToken)
                    ?? await НайтиAsync(сессия, после, пара.Sha256, отпечатки, cancellationToken)
                    ?? throw new DeployStepException(
                        DeploySteps.Locate, "Kerio принял файлы, но импортированного сертификата в хранилище нет.");
            }
            else
            {
                новый = уже;
                await Отметить(DeploySteps.ImportCertificate, $"Сертификат уже в хранилище Kerio («{уже}») — повторный импорт не нужен.");
            }

            await Отметить(DeploySteps.Activate, $"Сертификат «{новый}» — активный.");
            активирован = true;
            await сессия.SetActiveAsync(новый, cancellationToken);

            var порты = Порты(server);
            await Отметить(DeploySteps.Verify, $"Проверка по TLS на портах {string.Join(", ", порты)}.");

            var проверка = await ДождатьсяAsync(server, порты, пара.Sha256, request.VerifyTimeout, cancellationToken, Sni.Default);

            if (проверка is not null)
            {
                throw new DeployStepException(DeploySteps.Verify, проверка);
            }

            проверен = true;
            await Отметить(DeploySteps.Verify, "Новый сертификат подаётся на всех проверяемых портах.");

            if (server.CleanupOld)
            {
                await УбратьСтарыеAsync(сессия, server, request.Source.CommonName, пара, новый, прежний, отпечатки, Отметить, cancellationToken);
            }

            await Отметить(DeploySteps.Done, $"Установлен {пара.Subject}, действует до {пара.NotAfter:yyyy-MM-dd}.");

            return new DeployResult(
                DeployOutcomes.Succeeded, пара.Sha256, пара.NotBefore, пара.NotAfter, новый, прежний, прежнийSha, null, null);
        }
        // ⚠ Отмена (остановка сервиса) до активации — просто выход: Kerio не тронут.
        catch (Exception ошибка) when (!(ошибка is OperationCanceledException && cancellationToken.IsCancellationRequested)
            || (активирован && !проверен))
        {
            var остановка = ошибка is OperationCanceledException && cancellationToken.IsCancellationRequested;

            var текст = ошибка switch
            {
                _ when остановка => "Установка прервана остановкой сервиса.",
                DeployStepException отказ => отказ.Message,
                KerioException kerio => kerio.Message,
                TlsException tlsОтказ => tlsОтказ.Message,
                _ => $"Сбой: {SafeError.Describe(ошибка)}.",
            };

            var где = ошибка is DeployStepException с ? с.Step : шаг;

            log.LogWarning("kerio_deploy_failed {Server} {Step} {Error}", server.Name, где, текст);
            await Отметить(где, текст, ok: false);

            if (проверен && пара is not null)
            {
                return new DeployResult(
                    DeployOutcomes.Succeeded, пара.Sha256, пара.NotBefore, пара.NotAfter, новый, прежний, прежнийSha, null, null);
            }

            var тотЖе = прежнийSha is not null && string.Equals(прежнийSha, пара?.Sha256, StringComparison.Ordinal);

            if (!активирован || сессия is null || тотЖе)
            {
                return new DeployResult(DeployOutcomes.Failed, пара?.Sha256, пара?.NotBefore, пара?.NotAfter, новый, прежний, прежнийSha, где, текст);
            }

            var незаметно = прежнийSha is not null
                && await ДождатьсяAsync(server, Порты(server), прежнийSha, TimeSpan.Zero, CancellationToken.None, Sni.Default) is null;

            var вернули = await ОткатитьAsync(
                сессия,
                server,
                новый,
                прежний,
                прежнийSha,
                остановка ? ОткатПриОстановке : request.VerifyTimeout,
                Отметить,
                импортированЗдесь);

            return new DeployResult(
                вернули && !незаметно ? DeployOutcomes.RolledBack : DeployOutcomes.Failed,
                пара?.Sha256,
                пара?.NotBefore,
                пара?.NotAfter,
                новый,
                прежний,
                прежнийSha,
                где,
                вернули
                    ? текст
                    : текст + " ⚠ Вернуть прежний сертификат не удалось — проверьте сервер вручную.",
                вернули);
        }
        finally
        {
            if (сессия is not null)
            {
                await сессия.DisposeAsync();
            }

            пара?.Dispose();
        }
    }

    /// <summary>Вернуть прежний и убедиться по TLS; <c>true</c> — сервер снова подаёт прежний.</summary>
    private async Task<bool> ОткатитьAsync(
        IKerioSession сессия,
        KerioServer server,
        string? новый,
        string? прежний,
        string? прежнийSha,
        TimeSpan срок,
        Func<string, string, bool, Task> отметить,
        bool убратьНовый = false)
    {
        using var свой = new CancellationTokenSource(срок + TimeSpan.FromSeconds(60));
        var token = свой.Token;

        if (прежний is null || прежнийSha is null)
        {
            await отметить(DeploySteps.Rollback, "Прежний сертификат не найден в хранилище Kerio — вернуть нечего.", false);
            return false;
        }

        try
        {
            await отметить(DeploySteps.Rollback, $"Возврат прежнего сертификата «{прежний}».", true);
            await сессия.SetActiveAsync(прежний, token);

            var порты = Порты(server);

            if (await ДождатьсяAsync(server, порты, прежнийSha, срок / 2, token, Sni.Default) is null)
            {
                await отметить(DeploySteps.Rollback, "Прежний сертификат снова подаётся.", true);
                await УбратьНеудачныйAsync(сессия, убратьНовый ? новый : null, отметить, token);
                return true;
            }

            // ⚠ Kerio считает прежний уже активным и setActive на него ничего не делает (проверено на 10.0.6).
            if (новый is not null)
            {
                await отметить(DeploySteps.Rollback, "Kerio не переключился — повтор через новый и обратно.", true);
                await сессия.SetActiveAsync(новый, token);
                await Task.Delay(TimeSpan.FromSeconds(2), clock, token);
                await сессия.SetActiveAsync(прежний, token);

                if (await ДождатьсяAsync(server, порты, прежнийSha, срок / 2, token, Sni.Default) is null)
                {
                    await отметить(DeploySteps.Rollback, "Прежний сертификат снова подаётся.", true);
                    await УбратьНеудачныйAsync(сессия, убратьНовый ? новый : null, отметить, token);
                    return true;
                }
            }

            await отметить(DeploySteps.Rollback, "Прежний сертификат не вернулся на все порты.", false);
            return false;
        }
        catch (Exception ошибка)
        {
            var текст = ошибка is KerioException or TlsException ? ошибка.Message : SafeError.Describe(ошибка);
            await отметить(DeploySteps.Rollback, $"Откат не удался: {текст}", false);
            return false;
        }
    }

    /// <summary>Убрать не вставший сертификат после отката; сбой — в отчёт: откат уже состоялся.</summary>
    private static async Task УбратьНеудачныйAsync(
        IKerioSession сессия, string? новый, Func<string, string, bool, Task> отметить, CancellationToken cancellationToken)
    {
        if (новый is null)
        {
            return;
        }

        try
        {
            var отказы = await сессия.RemoveAsync([новый], cancellationToken);
            await отметить(
                DeploySteps.Rollback,
                отказы.Count == 0
                    ? $"Не вставший «{новый}» убран из хранилища: клиенты по имени снова получают прежний."
                    : $"Не вставший «{новый}» не убран: {string.Join("; ", отказы)}",
                отказы.Count == 0);
        }
        catch (Exception ошибка) when (ошибка is KerioException)
        {
            await отметить(DeploySteps.Rollback, $"Не вставший «{новый}» не убран: {ошибка.Message}", false);
        }
    }

    /// <summary>Свои прежние версии того же имени — не по умолчанию, кроме только что сменённого, — убрать.</summary>
    private async Task УбратьСтарыеAsync(
        IKerioSession сессия,
        KerioServer server,
        string commonName,
        CertificateBundle пара,
        string новый,
        string? прежний,
        Dictionary<string, string> отпечатки,
        Func<string, string, bool, Task> отметить,
        CancellationToken cancellationToken)
    {
        try
        {
            var свои = await СвоиНомераAsync(commonName, [server.InstalledSerial, .. server.PreviousSerials], cancellationToken);

            // ⚠ По метке «не по умолчанию», а не «неактивный»: действующие у Kerio помечены ActiveCertificate.
            var версии = await ВерсииИмениAsync(сессия, пара.Subject, новый, прежний, cancellationToken);
            var старые = версии.Where(в => в.Serial is { } номер && свои.Contains(номер)).Select(в => в.Id).ToList();
            var чужие = версии.Select(в => в.Id).Except(старые).ToList();

            if (чужие.Count > 0)
            {
                await отметить(
                    DeploySteps.Cleanup, $"Не из Key Manager Plus и не поставлены сервисом — не трогаются: {string.Join(", ", чужие)}.", true);
            }

            if (старые.Count == 0)
            {
                return;
            }

            var отказы = await сессия.RemoveAsync(старые, cancellationToken);

            await отметить(
                DeploySteps.Cleanup,
                отказы.Count == 0
                    ? $"Убраны прежние версии: {string.Join(", ", старые)}; предыдущая «{прежний}» оставлена для отката."
                    : $"Не всё убрано: {string.Join("; ", отказы)}",
                отказы.Count == 0);

            foreach (var id in старые)
            {
                отпечатки.Remove(id);
            }
        }
        catch (Exception ошибка)
        {
            var текст = ошибка is KerioException ? ошибка.Message : SafeError.Describe(ошибка);
            await отметить(DeploySteps.Cleanup, $"Прежние версии не убраны: {текст}", false);
        }
    }

    /// <summary>Идентификатор сертификата с этим отпечатком; активные проверяются первыми.</summary>
    private static async Task<string?> НайтиAsync(
        IKerioSession сессия,
        IReadOnlyList<KerioCertificate> где,
        string sha256,
        Dictionary<string, string> отпечатки,
        CancellationToken cancellationToken)
    {
        foreach (var с in где.Where(с => с.IsServerCertificate).OrderByDescending(с => с.IsDefault))
        {
            if (!отпечатки.TryGetValue(с.Id, out var отпечаток))
            {
                using var лист = Fingerprints.FirstFromPem(await сессия.SourceAsync(с.Id, cancellationToken));
                отпечаток = лист is null ? string.Empty : Fingerprints.Sha256(лист);
                отпечатки[с.Id] = отпечаток;
            }

            if (string.Equals(отпечаток, sha256, StringComparison.Ordinal))
            {
                return с.Id;
            }
        }

        return null;
    }

    /// <summary>Что сервер подаёт на порту администрирования; не ответил — <c>null</c>.</summary>
    private async Task<string?> ПодаётсяAsync(KerioServer server, CancellationToken cancellationToken)
    {
        var адрес = KerioConnector.BaseUri(server.BaseUrl);

        try
        {
            return (await tls.InspectAsync(адрес.Host, адрес.Port, Sni.Default, cancellationToken)).Sha256;
        }
        catch (TlsException)
        {
            return null;
        }
    }

    /// <summary>Ждать, пока все порты подают <paramref name="sha256"/>; <c>null</c> — дождались, иначе причина.</summary>
    private async Task<string?> ДождатьсяAsync(
        KerioServer server, IReadOnlyList<int> порты, string sha256, TimeSpan срок, CancellationToken cancellationToken, string? serverName = null)
    {
        var хост = KerioConnector.BaseUri(server.BaseUrl).Host;
        var до = clock.GetUtcNow() + срок;
        var последнее = new Dictionary<int, string>();

        while (true)
        {
            var все = true;

            foreach (var порт in порты)
            {
                try
                {
                    var подан = serverName is null
                        ? await tls.InspectAsync(хост, порт, cancellationToken)
                        : await tls.InspectAsync(хост, порт, serverName, cancellationToken);
                    последнее[порт] = подан.Sha256 == sha256 ? $"Порт {порт} подаёт новый." : $"Порт {порт} подаёт {подан.Subject} до {подан.NotAfter:yyyy-MM-dd}.";
                    все &= подан.Sha256 == sha256;
                }
                catch (TlsException ошибка)
                {
                    последнее[порт] = $"Порт {порт}: {ошибка.Message}";
                    все = false;
                }
            }

            if (все)
            {
                return null;
            }

            if (clock.GetUtcNow() >= до)
            {
                return $"За {срок.TotalSeconds:0} с сервер не начал подавать новый сертификат. " + string.Join(' ', последнее.Values);
            }

            await Task.Delay(ШагПроверки, clock, cancellationToken);
        }
    }

    internal static IReadOnlyList<int> Порты(KerioServer server)
    {
        var главный = KerioConnector.BaseUri(server.BaseUrl).Port;

        return [главный, .. server.VerifyPorts.Where(п => п is > 0 and < 65536 && п != главный).Distinct()];
    }

    private static string ИмяФайла(string subject)
    {
        var имя = new StringBuilder();

        foreach (var с in subject.Replace("*", "wildcard", StringComparison.Ordinal))
        {
            имя.Append(char.IsAsciiLetterOrDigit(с) || с is '.' or '-' ? с : '_');
        }

        return имя.Length == 0 ? "certificate" : имя.ToString();
    }
}
