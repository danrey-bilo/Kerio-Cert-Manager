using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.KeyManager.Contracts;
using Microsoft.Extensions.Logging;
using Modules.Core.Errors;

namespace KerioCertManager.Modules.Kerio.Sync;

/// <summary>Установка сертификата своего имени — того, что Kerio подаёт по SNI, а не по умолчанию.</summary>
internal sealed partial class KerioDeployer
{
    /// <summary>Положить в хранилище Kerio свежий выпуск сертификата своего имени, не меняя сертификат по умолчанию.</summary>
    public async Task<DeployResult> DeployDomainAsync(
        DeployRequest request, Func<DeployStep, Task> journal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(journal);

        var server = request.Server;
        var выпуск = request.Source;
        var шаг = DeploySteps.Fetch;

        CertificateBundle? пара = null;
        IKerioSession? сессия = null;

        string? поУмолчанию = null;
        string? поУмолчаниюSha = null;
        string? новый = null;
        string? убран = null;
        string? замечание = null;
        var импортирован = false;
        var вернули = true;

        // ⚠ Журнал — не установка: сбой записи не должен оборвать возврат сертификата по умолчанию.
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
            await Отметить(DeploySteps.Fetch, $"Выгрузка {выпуск.CommonName} (серийный {выпуск.SerialNumber}) из Key Manager Plus.");

            CertificateMaterial выгрузка;

            try
            {
                выгрузка = await source.FetchAsync(выпуск.CommonName, выпуск.SerialNumber, cancellationToken);
            }
            catch (CertificateSourceException ошибка)
            {
                throw new DeployStepException(DeploySteps.Fetch, ошибка.Message, ошибка);
            }

            await Отметить(DeploySteps.Validate, "Проверка ключа, имени, срока и цепочки.");

            try
            {
                пара = CertificateBundle.Build(выгрузка, выпуск.CommonName, clock.GetUtcNow());
            }
            catch (BundleException ошибка)
            {
                throw new DeployStepException(DeploySteps.Validate, ошибка.Message, ошибка);
            }
            finally
            {
                выгрузка.Dispose();
            }

            // ⚠ Как у сертификата по умолчанию: KMP мог заново показать старую пару — сам сервис откат не делает.
            var запись = server.Extras.FirstOrDefault(д => string.Equals(д.CommonName, выпуск.CommonName, StringComparison.OrdinalIgnoreCase));
            var было = server.Store
                .Where(з => ExtraPlanner.Того(з, выпуск.CommonName) && запись?.Installed(з.Serial) == true)
                .Max(з => з.NotBefore);

            if (было is { } своя && пара.NotBefore < своя.AddMinutes(-1))
            {
                if (!request.Manual)
                {
                    throw new DeployStepException(
                        DeploySteps.Validate,
                        $"Выпуск из Key Manager Plus ({пара.NotBefore:yyyy-MM-dd}) старше установленного ({своя:yyyy-MM-dd}): "
                        + "сам сервис откат не делает. Установите вручную, если это намеренно.");
                }

                await Отметить(DeploySteps.Validate, "Выпуск старше установленного — ставится по решению администратора.");
            }

            await Отметить(
                DeploySteps.Validate,
                $"Сертификат {пара.Subject} от {пара.Issuer}, до {пара.NotAfter:yyyy-MM-dd}, SHA-256 {Fingerprints.Pretty(пара.Sha256)}."
                + (пара.ChainComplete ? string.Empty : " ⚠ Цепочка до корня не собрана — клиенты могут не доверять серверу."));

            await Отметить(DeploySteps.Login, $"Вход в Kerio {server.BaseUrl} учётной записью {server.Username}.");

            try
            {
                сессия = await connector.OpenAsync(server, [пара.Sha256], cancellationToken);
            }
            catch (KerioException ошибка)
            {
                throw new DeployStepException(DeploySteps.Login, ошибка.Message, ошибка);
            }

            await Отметить(DeploySteps.Inventory, "Какой сертификат подаётся по умолчанию и что лежит в хранилище.");

            поУмолчаниюSha = await ПоУмолчаниюAsync(server, cancellationToken)
                ?? throw new DeployStepException(DeploySteps.Inventory, "Не удалось узнать сертификат по умолчанию: порт администрирования не ответил по TLS.");

            var хранилище = await сессия.CertificatesAsync(cancellationToken);
            var отпечатки = new Dictionary<string, string>(StringComparer.Ordinal);

            поУмолчанию = await НайтиAsync(сессия, хранилище, поУмолчаниюSha, отпечатки, cancellationToken);
            var уже = await НайтиAsync(сессия, хранилище, пара.Sha256, отпечатки, cancellationToken);

            await Отметить(
                DeploySteps.Inventory,
                $"В хранилище Kerio сертификатов: {хранилище.Count}."
                + (поУмолчанию is null ? " По умолчанию подаётся сертификат, которого нет в хранилище." : $" По умолчанию подаётся «{поУмолчанию}».")
                + (уже is null ? string.Empty : $" Новый уже в хранилище как «{уже}»."));

            if (поУмолчанию is null && !string.Equals(поУмолчаниюSha, пара.Sha256, StringComparison.Ordinal))
            {
                throw new DeployStepException(
                    DeploySteps.Inventory,
                    "Сертификат по умолчанию не найден в хранилище Kerio: если импорт сменит его, вернуть прежний будет нечем. "
                    + "Сделайте нужный сертификат активным в Kerio и повторите.");
            }

            if (request.DryRun)
            {
                await Отметить(DeploySteps.DryRun, "Пробный прогон: всё готово к загрузке, Kerio не изменён.");

                return new DeployResult(DeployOutcomes.DryRun, пара.Sha256, пара.NotBefore, пара.NotAfter, null, null, поУмолчаниюSha, null, null);
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
                var файлСертификата = await сессия.UploadAsync(пара.CertificatePem, $"{ИмяФайла(пара.Subject)}.crt", cancellationToken);

                await Отметить(DeploySteps.ImportCertificate, "Импорт сертификата.");

                // ⚠ С этого места Kerio мог сменить сертификат по умолчанию: любой исход дальше — с проверкой и возвратом.
                импортирован = true;
                вернули = false;
                await сессия.ImportCertificateAsync(ключ.Id, файлСертификата, string.Empty, cancellationToken);

                await Отметить(DeploySteps.Locate, "Поиск импортированного сертификата в хранилище Kerio.");
                var после = await сессия.CertificatesAsync(cancellationToken);
                var появились = после.Where(с => хранилище.All(б => б.Id != с.Id)).ToList();

                новый = await НайтиAsync(сессия, появились, пара.Sha256, отпечатки, cancellationToken)
                    ?? await НайтиAsync(сессия, после, пара.Sha256, отпечатки, cancellationToken)
                    ?? throw new DeployStepException(DeploySteps.Locate, "Kerio принял файлы, но импортированного сертификата в хранилище нет.");

                if (поУмолчанию is not null)
                {
                    // ПоУмолчаниюAsync пережидает перезапуск HTTPS: не ответил и после него — знать, что подаётся, неоткуда.
                    var сейчас = await ПоУмолчаниюAsync(server, cancellationToken)
                        ?? throw new DeployStepException(DeploySteps.RestoreDefault, "После импорта порт администрирования не ответил по TLS — проверьте сертификат по умолчанию вручную.");

                    if (string.Equals(сейчас, поУмолчаниюSha, StringComparison.Ordinal))
                    {
                        вернули = true;
                        await Отметить(DeploySteps.RestoreDefault, "Сертификат по умолчанию не изменился.");
                    }
                    else
                    {
                        вернули = await ВернутьПоУмолчаниюAsync(сессия, server, поУмолчанию, поУмолчаниюSha, request.VerifyTimeout, Отметить);

                        if (!вернули)
                        {
                            throw new DeployStepException(DeploySteps.RestoreDefault, "Сертификат по умолчанию не вернулся — проверьте сервер вручную.");
                        }
                    }
                }
                else
                {
                    вернули = true;
                }
            }
            else
            {
                новый = уже;
                await Отметить(DeploySteps.ImportCertificate, $"Сертификат уже в хранилище Kerio («{уже}») — повторный импорт не нужен.");
            }

            var номераKmp = await СвоиНомераAsync(
                выпуск.CommonName, [запись?.InstalledSerial, .. запись?.PreviousSerials ?? []], cancellationToken);

            var версии = await ВерсииИмениAsync(сессия, выпуск.CommonName, новый, поУмолчанию, cancellationToken);
            var имя = Sni.For(выпуск.CommonName);
            var порты = Порты(server);

            if (имя is not null)
            {
                await Отметить(DeploySteps.Verify, $"Проверка по TLS с именем {имя} на портах {string.Join(", ", порты)}.");

                var отказ = await ДождатьсяAsync(server, порты, пара.Sha256, ПерваяПроверкаИмени, cancellationToken, имя);

                if (отказ is not null)
                {
                    // Новый уже подаётся, другого нигде нет — мешающих версий быть не может.
                    if (!отказ.ТолькоНеОтветившие)
                    {
                        var мешают = версии.Where(в => в.NotAfter >= пара.NotAfter).ToList();
                        var свои = мешают.Where(в => в.Serial is { } номер && номераKmp.Contains(номер)).ToList();
                        var чужая = мешают.FirstOrDefault(в => в.Serial is not { } номер || !номераKmp.Contains(номер));

                        if (чужая is not null)
                        {
                            throw new DeployStepException(
                                DeploySteps.Verify,
                                $"Для {имя} Kerio подаёт «{чужая.Id}» ({чужая.Subject}, до {чужая.NotAfter:yyyy-MM-dd}) — он не из Key Manager "
                                + "Plus и действует дольше нового. Сервис чужие сертификаты не удаляет: уберите его в Kerio, если он больше не нужен.");
                        }

                        убран = await УбратьAsync(сессия, [.. свои.Select(в => в.Id)], выпуск.CommonName, "мешают новой версии", Отметить, cancellationToken);
                        версии = [.. версии.Where(в => свои.All(с => с.Id != в.Id))];
                    }

                    отказ = await ДождатьсяAsync(server, порты, пара.Sha256, request.VerifyTimeout, cancellationToken, имя);
                }

                if (отказ is { ТолькоНеОтветившие: true })
                {
                    // ⚠ Установка удалась: сертификат подаётся, а порту, который не отвечает, перезапуск службы не поможет.
                    замечание = $"Не отвечают по TLS порты: {string.Join(", ", отказ.НеОтветили)}. На остальных новый сертификат подаётся; проверьте эти порты или уберите их из проверяемых.";
                    await Отметить(DeploySteps.Verify, замечание, false);
                }
                else if (отказ is not null)
                {
                    throw new DeployStepException(
                        DeploySteps.Verify,
                        отказ.Текст + (отказ.ЕстьДругой
                            ? $" Новый сертификат лежит в хранилище Kerio; чтобы он подавался для {имя}, перезапустите службу Kerio Connect."
                            : " Новый сертификат лежит в хранилище Kerio; сервер не ответил ни на одном порту, следующий проход проверит снова."));
                }
                else
                {
                    await Отметить(DeploySteps.Verify, $"Для {имя} подаётся новый сертификат на всех проверяемых портах.");
                }
            }

            if (server.CleanupOld)
            {
                var старые = версии
                    .Where(в => в.Serial is { } номер && номераKmp.Contains(номер))
                    .OrderByDescending(в => в.NotAfter)
                    .Skip(1)
                    .Select(в => в.Id)
                    .ToList();

                try
                {
                    убран = await УбратьAsync(сессия, старые, выпуск.CommonName, "старые версии", Отметить, cancellationToken) ?? убран;
                }
                catch (Exception ошибка) when (ошибка is KerioException)
                {
                    await Отметить(DeploySteps.Cleanup, $"Старые версии не убраны: {ошибка.Message}", false);
                }
            }

            await Отметить(DeploySteps.Done, $"В хранилище Kerio — {пара.Subject} до {пара.NotAfter:yyyy-MM-dd}; сертификат по умолчанию не изменён.");

            return new DeployResult(DeployOutcomes.Succeeded, пара.Sha256, пара.NotBefore, пара.NotAfter, новый, убран, поУмолчаниюSha, null, null, Warning: замечание);
        }
        // ⚠ Отмена до импорта — просто выход.
        catch (Exception ошибка) when (!(ошибка is OperationCanceledException && cancellationToken.IsCancellationRequested)
            || (импортирован && !вернули))
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

            log.LogWarning("kerio_domain_deploy_failed {Server} {CommonName} {Step} {Error}", server.Name, выпуск.CommonName, где, текст);
            await Отметить(где, текст, ok: false);

            if (импортирован && !вернули && сессия is not null && поУмолчанию is not null && поУмолчаниюSha is not null
                && где != DeploySteps.RestoreDefault)
            {
                вернули = await ВернутьПоУмолчаниюAsync(
                    сессия, server, поУмолчанию, поУмолчаниюSha, остановка ? ОткатПриОстановке : request.VerifyTimeout, Отметить);
            }

            return new DeployResult(
                DeployOutcomes.Failed,
                пара?.Sha256,
                пара?.NotBefore,
                пара?.NotAfter,
                новый,
                убран,
                поУмолчаниюSha,
                где,
                вернули ? текст : текст + " ⚠ Сертификат по умолчанию не вернулся — проверьте сервер вручную.",
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

    /// <summary>Сколько ждать нового по имени, прежде чем искать, какая версия мешает: Kerio подаёт новую сразу.</summary>
    private static readonly TimeSpan ПерваяПроверкаИмени = TimeSpan.FromSeconds(6);

    /// <summary>Версия сертификата в хранилище Kerio: идентификатор, субъект, серийный, срок.</summary>
    private sealed record Версия(string Id, string Subject, string? Serial, DateTimeOffset NotAfter);

    /// <summary>Сертификат по умолчанию — рукопожатием без SNI с портом администрирования; пережидает перезапуск HTTPS.</summary>
    private async Task<string?> ПоУмолчаниюAsync(KerioServer server, CancellationToken cancellationToken)
    {
        var адрес = KerioConnector.BaseUri(server.BaseUrl);
        var до = clock.GetUtcNow() + KerioSession.ОжиданиеПерезапуска;

        while (true)
        {
            try
            {
                return (await tls.InspectAsync(адрес.Host, адрес.Port, Sni.Default, cancellationToken)).Sha256;
            }
            catch (TlsException) when (clock.GetUtcNow() + ШагПроверки < до)
            {
                await Task.Delay(ШагПроверки, clock, cancellationToken);
            }
            catch (TlsException)
            {
                return null;
            }
        }
    }

    /// <summary>Вернуть сертификат по умолчанию и убедиться рукопожатием без SNI на порту администрирования.</summary>
    private async Task<bool> ВернутьПоУмолчаниюAsync(
        IKerioSession сессия,
        KerioServer server,
        string поУмолчанию,
        string поУмолчаниюSha,
        TimeSpan срок,
        Func<string, string, bool, Task> отметить)
    {
        using var свой = new CancellationTokenSource(срок + TimeSpan.FromSeconds(60));
        var token = свой.Token;

        try
        {
            await отметить(DeploySteps.RestoreDefault, $"Сертификат по умолчанию сменился — возврат «{поУмолчанию}».", true);
            await сессия.SetActiveAsync(поУмолчанию, token);

            var адрес = KerioConnector.BaseUri(server.BaseUrl);

            if (await ДождатьсяAsync(server, [адрес.Port], поУмолчаниюSha, срок, token, Sni.Default) is null)
            {
                await отметить(DeploySteps.RestoreDefault, "Сертификат по умолчанию снова подаётся.", true);
                return true;
            }

            await отметить(DeploySteps.RestoreDefault, "Сертификат по умолчанию не вернулся.", false);
            return false;
        }
        catch (Exception ошибка)
        {
            var текст = ошибка is KerioException or TlsException ? ошибка.Message : SafeError.Describe(ошибка);
            await отметить(DeploySteps.RestoreDefault, $"Возврат сертификата по умолчанию не удался: {текст}", false);
            return false;
        }
    }

    /// <summary>Серийные своих выпусков имени — из Key Manager Plus или поставленных сервисом; чужие не трогаются.</summary>
    private async Task<HashSet<string>> СвоиНомераAsync(
        string commonName, IEnumerable<string?> поставленные, CancellationToken cancellationToken) =>
        (await source.ListAsync(cancellationToken))
            .Where(в => string.Equals(в.CommonName, commonName, StringComparison.OrdinalIgnoreCase))
            .Select(в => в.SerialNumber)
            .Concat(поставленные)
            .Select(Fingerprints.Serial)
            .Where(н => н.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Другие версии этого имени в хранилище — кроме нового, сертификата по умолчанию и встроенного.</summary>
    private static async Task<IReadOnlyList<Версия>> ВерсииИмениAsync(
        IKerioSession сессия, string commonName, string новый, string? поУмолчанию, CancellationToken cancellationToken)
    {
        var итог = new List<Версия>();

        foreach (var с in (await сессия.CertificatesAsync(cancellationToken)).Where(с => с.IsServerCertificate
            && !с.IsDefault
            && с.Id != KerioCertificate.BuiltInId
            && с.Id != новый
            && с.Id != поУмолчанию
            && string.Equals(с.Subject, commonName, StringComparison.OrdinalIgnoreCase)))
        {
            using var лист = Fingerprints.FirstFromPem(await сессия.SourceAsync(с.Id, cancellationToken));

            итог.Add(лист is null
                ? new Версия(с.Id, с.Subject, null, DateTimeOffset.MaxValue)
                : new Версия(с.Id, с.Subject, Fingerprints.Serial(лист.SerialNumber), лист.NotAfter.ToUniversalTime()));
        }

        return итог;
    }

    /// <summary>Убрать версии имени; вернуть их идентификаторы через запятую или <c>null</c>, если убирать нечего.</summary>
    private static async Task<string?> УбратьAsync(
        IKerioSession сессия,
        IReadOnlyList<string> ids,
        string commonName,
        string почему,
        Func<string, string, bool, Task> отметить,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return null;
        }

        await отметить(DeploySteps.Cleanup, $"Свои версии {commonName} — из Key Manager Plus или поставленные сервисом ({почему}): {string.Join(", ", ids)} — убираются.", true);

        var отказы = await сессия.RemoveAsync(ids, cancellationToken);

        await отметить(
            DeploySteps.Cleanup,
            отказы.Count == 0 ? "Убраны." : $"Не всё убрано: {string.Join("; ", отказы)}",
            отказы.Count == 0);

        return string.Join(", ", ids);
    }
}
