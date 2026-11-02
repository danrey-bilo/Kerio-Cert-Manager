using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KerioCertManager.Emulators;
using KerioCertManager.Modules.Kerio.Sync;
using KerioCertManager.Modules.KeyManager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Core.Data;

namespace KerioCertManager.IntegrationTests;

/// <summary>Сквозь весь хост: подключение Kerio, проба и проверка входа, продление в KMP, установка циклом и вручную.</summary>
[Collection(KerioCertManagerHost.Collection)]
public sealed class KerioApi(KerioCertManagerHost host) : IAsyncLifetime
{
    private const string Серверы = "/api/kerio/servers";

    private const string Почта = "mail.demo.local";

    private EmulatorHost? _стенд;

    private readonly int _портKmp = EmulatorHost.FreePort();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EmulatorHost Стенд => _стенд ?? throw new InvalidOperationException("стенд не поднят");

    public async ValueTask InitializeAsync()
    {
        host.User.SignIn();
        host.Settings.Values.Clear();

        _стенд = EmulatorHost.Build(new EmulatorOptions
        {
            ControlPort = EmulatorHost.FreePort(),
            KmpPort = _портKmp,
            Kerio = [new KerioSpec("kerio-a", EmulatorHost.FreePort(), [EmulatorHost.FreePort()])],
            Certificates = [(Почта, 12)],
        });

        await Стенд.StartAsync(Ct);

        host.Services.GetRequiredService<KeyManagerModuleOptions>().TrustedRoots.Add(Стенд.Pki.Root);
        host.Settings.Values["keymanager.enabled"] = true;
        host.Settings.Values["keymanager.base_url"] = $"https://localhost:{_портKmp}";
        host.Settings.Values["kerio.verify_seconds"] = 10;

        (await host.CreateClient().PutAsJsonAsync("/api/keymanager/api-key", new { api_key = Стенд.Kmp.ApiKey }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    public async ValueTask DisposeAsync()
    {
        host.User.SignIn();
        var клиент = host.CreateClient();

        foreach (var сервер in await клиент.GetFromJsonAsync<JsonArray>(Серверы, CancellationToken.None) ?? [])
        {
            await клиент.DeleteAsync($"{Серверы}/{сервер!["id"]!.GetValue<Guid>()}", CancellationToken.None);
        }

        await клиент.PutAsJsonAsync("/api/keymanager/api-key", new { api_key = "" }, CancellationToken.None);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM kerio_deployments; DELETE FROM kerio_notifications; DELETE FROM keymanager_certificates; "
                + "DELETE FROM mail_outbox WHERE kind LIKE 'kerio_%';",
                CancellationToken.None);
        }

        host.Settings.Values.Clear();
        host.Services.GetRequiredService<KeyManagerModuleOptions>().TrustedRoots.Clear();
        host.User.SignOut();

        if (_стенд is not null)
        {
            await _стенд.DisposeAsync();
        }
    }

    [Fact]
    public async Task Подключение_продление_и_ручная_установка()
    {
        host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<SyncWorker>()
            .ShouldBeEmpty("фоновый цикл в тестах выключен — иначе он перехватывал бы аренду сервера");

        var kerio = Стенд.Kerio[0];
        var клиент = host.CreateClient();
        var адрес = $"https://localhost:{kerio.AdminPort}";

        await ЦиклAsync();

        var kmp = await Json(await клиент.GetAsync("/api/keymanager", Ct));
        kmp["last_poll_error"].ShouldBeNull(kmp.ToJsonString());
        kmp["last_poll_count"]!.GetValue<int>().ShouldBe(1);

        var проба = await Json(await клиент.PostAsJsonAsync($"{Серверы}/probe", new { base_url = адрес }, Ct));
        var отпечаток = проба["sha256"]!.GetValue<string>();
        отпечаток.ShouldBe(Convert.ToHexString(SHA256.HashData(kerio.ServedCertificate.RawData)));
        проба["suggested_mode"]!.GetValue<string>().ShouldBe("pin", "учебный корень системе неизвестен");
        проба["subject"]!.GetValue<string>().ShouldBe(Почта);

        var вход = await Json(await клиент.PostAsJsonAsync(
            $"{Серверы}/test",
            new { base_url = адрес, username = kerio.User, password = kerio.Password, tls_mode = "pin", tls_pin_sha256 = отпечаток },
            Ct));
        вход["ok"]!.GetValue<bool>().ShouldBeTrue(вход.ToJsonString());
        вход["suggested_common_names"]!.AsArray().Select(и => и!.GetValue<string>()).ShouldContain(Почта);

        var неверный = await Json(await клиент.PostAsJsonAsync(
            $"{Серверы}/test",
            new { base_url = адрес, username = kerio.User, password = "не тот", tls_mode = "pin", tls_pin_sha256 = отпечаток },
            Ct));
        неверный["ok"]!.GetValue<bool>().ShouldBeFalse();

        var создан = await клиент.PostAsJsonAsync(
            Серверы,
            new
            {
                name = "Kerio A",
                base_url = адрес,
                username = kerio.User,
                password = kerio.Password,
                tls_mode = "pin",
                tls_pin_sha256 = отпечаток,
                common_name = Почта,
                verify_ports = kerio.ServicePorts,
            },
            Ct);
        создан.StatusCode.ShouldBe(HttpStatusCode.Created);
        var сервер = await Json(создан);
        var id = сервер["id"]!.GetValue<Guid>();
        сервер["password_set"]!.GetValue<bool>().ShouldBeTrue();
        сервер.ToJsonString().ShouldNotContain(kerio.Password);
        сервер["last_check_error"].ShouldBeNull(сервер.ToJsonString());
        сервер["kerio_role"]!.GetValue<string>().ShouldBe("FullAdmin");

        await ЦиклAsync();
        сервер = await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct));
        сервер["status"]!.GetValue<string>().ShouldBe("expiring", сервер.ToJsonString());
        var исходный = Стенд.Kmp.Entries[0].Serial;
        сервер["installed"]!["serial"]!.GetValue<string>().ShouldBe(исходный.ToLowerInvariant());

        var новый = Стенд.Kmp.Issue(Почта, TimeSpan.FromDays(90));
        await ЦиклAsync();

        сервер = await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct));
        var отладка = await клиент.GetStringAsync($"{Серверы}/{id}/deployments", Ct);
        сервер["status"]!.GetValue<string>().ShouldBe("ok", сервер.ToJsonString() + " DEPLOYMENTS " + отладка);
        kerio.ServedCertificate.SerialNumber.ShouldBe(новый.Certificate.SerialNumber);
        сервер["tls_pin_sha256"]!.GetValue<string>().ShouldBe(
            Convert.ToHexString(SHA256.HashData(новый.Certificate.RawData)), "закреплённый отпечаток переехал на поставленный");

        var установки = await клиент.GetFromJsonAsync<JsonArray>($"{Серверы}/{id}/deployments", Ct);
        var первая = установки!.ShouldHaveSingleItem()!;
        первая["outcome"]!.GetValue<string>().ShouldBe("succeeded");
        первая["trigger"]!.GetValue<string>().ShouldBe("expiring", "на сервере оставалось 11 дней — меньше порога предупреждения");
        первая["steps"]!.AsArray().Count.ShouldBeGreaterThan(5);

        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { auto_deploy = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var третий = Стенд.Kmp.Issue(Почта, TimeSpan.FromDays(47));
        await ЦиклAsync();

        (await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct)))["status"]!.GetValue<string>().ShouldBe("update_pending");
        kerio.ServedCertificate.SerialNumber.ShouldBe(новый.Certificate.SerialNumber);

        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        (await клиент.PostAsync($"{Серверы}/{id}/deploy", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        host.User.StepUpAt = DateTimeOffset.UtcNow;
        (await клиент.PostAsJsonAsync($"{Серверы}/{id}/deploy", new { common_name = "other.demo.local" }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest, "чужое имя — не сертификат этого сервера");
        (await клиент.PostAsJsonAsync($"{Серверы}/{id}/deploy", new { common_name = Почта.ToUpperInvariant() }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Accepted);
        Очередь(await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct))).ShouldBe([Почта]);
        await ЦиклAsync();

        Очередь(await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct))).ShouldBeEmpty("запрос погашен проходом");

        kerio.ServedCertificate.SerialNumber.ShouldBe(третий.Certificate.SerialNumber);

        // ⚠ Имя есть в KMP, но для сервера не выбрано — не трогаем; выбранное ставится сразу, хоть автоустановка продлений и выключена.
        var новое = Стенд.Kmp.Issue("extra.demo.local", TimeSpan.FromDays(90));
        await ЦиклAsync();
        host.User.StepUpAt = DateTimeOffset.UtcNow;
        (await клиент.PostAsJsonAsync($"{Серверы}/{id}/deploy", new { common_name = "extra.demo.local" }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest, "не выбран для этого сервера");
        kerio.Store.ShouldNotContain(з => з.Certificate.SerialNumber == новое.Certificate.SerialNumber);

        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { extra_common_names = new[] { "extra.demo.local" } }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        await ЦиклAsync();
        kerio.Store.ShouldContain(з => з.Certificate.SerialNumber == новое.Certificate.SerialNumber, "первая установка выбранного — без кнопки");

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var строка = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<KerioCertManager.Modules.Kerio.Domain.KerioServer>()
                .AsNoTracking()
                .SingleAsync(с => с.Id == id, Ct);

            строка.PreviousSerials.Select(KerioCertManager.Modules.Kerio.Certificates.Fingerprints.Serial)
                .ShouldBe(new[] { новый.Serial, исходный }.Select(KerioCertManager.Modules.Kerio.Certificates.Fingerprints.Serial));
        }

        var сводка = await Json(await клиент.GetAsync("/api/kerio/overview", Ct));
        сводка["servers"]!.AsArray().Count.ShouldBe(1);

        var журналУстановок = await клиент.GetFromJsonAsync<JsonArray>("/api/kerio/deployments?limit=5&steps=true", Ct);
        журналУстановок![0]!["trigger"]!.GetValue<string>().ShouldBe("initial", "последним встал только что выбранный extra.demo.local");
        журналУстановок[1]!["trigger"]!.GetValue<string>().ShouldBe("manual");
        журналУстановок[1]!["steps"]!.AsArray().Count.ShouldBeGreaterThan(5);

        var журнал = await клиент.GetFromJsonAsync<JsonNode>($"/api/audit?action=kerio.certificate_deployed&resource_id={id}", Ct);
        журнал!.ToJsonString().ShouldContain(Почта);
    }

    /// <summary>⚠ Сервер подаёт сертификат с серийным номером выпуска KMP, но другой (подмена).</summary>
    [Fact]
    public async Task Подделка_с_серийным_номером_KMP_не_признаётся()
    {
        var kerio = Стенд.Kerio[0];
        var клиент = host.CreateClient();
        await ЦиклAsync();

        var отпечаток = Convert.ToHexString(SHA256.HashData(kerio.ServedCertificate.RawData));
        var создан = await Json(await клиент.PostAsJsonAsync(
            Серверы,
            new
            {
                name = "Kerio подмена",
                base_url = $"https://localhost:{kerio.AdminPort}",
                username = kerio.User,
                password = kerio.Password,
                tls_mode = "pin",
                tls_pin_sha256 = отпечаток,
                common_name = Почта,
                verify_ports = kerio.ServicePorts,
            },
            Ct));
        var id = создан["id"]!.GetValue<Guid>();

        await ЦиклAsync();
        var прежний = (await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct)))["installed"]!["serial"]!.GetValue<string>();

        var выпуск = Стенд.Kmp.Issue(Почта, TimeSpan.FromDays(90));
        using var подделка = Стенд.Pki.Issue([Почта], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90),
            Convert.FromHexString(выпуск.Serial));
        kerio.Seed(подделка, подделка.ExportCertificatePem());

        await ЦиклAsync();

        var сервер = await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct));
        сервер["installed"]!["serial"]!.GetValue<string>().ShouldBe(прежний, "подделку не признали установленной");
        сервер["tls_pin_sha256"]!.GetValue<string>().ShouldBe(отпечаток, "закреплённый отпечаток не переехал на подделку");
        сервер["served"]!["serial"]!.GetValue<string>().ShouldBe(выпуск.Serial.ToLowerInvariant().TrimStart('0'));
        сервер["last_check_error"]!.GetValue<string>().ShouldContain("доверия", Case.Insensitive, "в API с подделкой не входили");
    }

    [Fact]
    public async Task Пробный_режим_не_меняет_Kerio_и_не_повторяется_каждый_проход()
    {
        var kerio = Стенд.Kerio[0];
        var id = await ПодключитьAsync(kerio);
        host.Settings.Values["kerio.dry_run"] = true;

        var прежний = kerio.ServedCertificate.SerialNumber;
        Стенд.Kmp.Issue(Почта, TimeSpan.FromDays(90));

        await ЦиклAsync();
        await ЦиклAsync();

        kerio.ServedCertificate.SerialNumber.ShouldBe(прежний, "в пробном режиме Kerio не меняется");
        kerio.Store.Count.ShouldBe(2);

        var установки = await host.CreateClient().GetFromJsonAsync<JsonArray>($"{Серверы}/{id}/deployments", Ct);
        установки!.ShouldHaveSingleItem()!["outcome"]!.GetValue<string>().ShouldBe("dry_run", "второй проход пробу не повторил");

        var сервер = await Json(await host.CreateClient().GetAsync($"{Серверы}/{id}", Ct));
        сервер["status_text"]!.GetValue<string>().ShouldContain("пробный режим");
    }

    [Fact]
    public async Task Отказ_установки_возвращает_прежний_и_ждёт_перед_повтором()
    {
        var kerio = Стенд.Kerio[0];
        var id = await ПодключитьAsync(kerio);

        var прежний = kerio.ServedCertificate.SerialNumber;
        kerio.Stuck = true;
        Стенд.Kmp.Issue(Почта, TimeSpan.FromDays(90));

        await ЦиклAsync();
        await ЦиклAsync();

        kerio.ServedCertificate.SerialNumber.ShouldBe(прежний);
        kerio.ConfigActiveId.ShouldBe(kerio.ServedId, "прежний снова активный и в настройках Kerio");

        var установки = await host.CreateClient().GetFromJsonAsync<JsonArray>($"{Серверы}/{id}/deployments", Ct);
        var отказ = установки!.ShouldHaveSingleItem("повтор ждёт паузы — второй проход не ставил")!;
        отказ["outcome"]!.GetValue<string>().ShouldBe("failed");
        отказ["failed_step"]!.GetValue<string>().ShouldBe("verify");

        var сервер = await Json(await host.CreateClient().GetAsync($"{Серверы}/{id}", Ct));
        сервер["status"]!.GetValue<string>().ShouldBe("deploy_failed");
        сервер["next_attempt_at"].ShouldNotBeNull();
    }

    /// <summary>Процесс умер посреди установки — запись «идёт» закрывается следующим проходом, а не висит вечно.</summary>
    [Fact]
    public async Task Брошенная_установка_закрывается_следующим_проходом()
    {
        var id = await ПодключитьAsync(Стенд.Kerio[0]);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Set<KerioCertManager.Modules.Kerio.Domain.KerioDeployment>().Add(new KerioCertManager.Modules.Kerio.Domain.KerioDeployment
            {
                Id = Guid.CreateVersion7(),
                ServerId = id,
                ServerName = "Kerio kerio-a",
                Trigger = "renewal",
                Outcome = "running",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-40),
                CommonName = Почта,
                SerialNumber = "01",
            });
            await db.SaveChangesAsync(Ct);
        }

        await ЦиклAsync();

        var установки = await host.CreateClient().GetFromJsonAsync<JsonArray>($"{Серверы}/{id}/deployments", Ct);
        var брошенная = установки!.ShouldHaveSingleItem()!;
        брошенная["outcome"]!.GetValue<string>().ShouldBe("failed");
        брошенная["error"]!.GetValue<string>().ShouldContain("прервана");
    }

    /// <summary>Обзор видит всё хранилище Kerio: подаваемый свой, встроенный и положенный вручную — с происхождением и сроком.</summary>
    [Fact]
    public async Task Хранилище_Kerio_в_обзоре_с_происхождением()
    {
        var kerio = Стенд.Kerio[0];
        var цепь = Стенд.Pki.Intermediate.ExportCertificatePem();
        var чужой = Стенд.Pki.Issue(["legacy.demo.local"], DateTimeOffset.UtcNow.AddDays(-400), DateTimeOffset.UtcNow.AddDays(-5));
        kerio.Keep(чужой, чужой.ExportCertificatePem() + "\n" + цепь);

        var id = await ПодключитьAsync(kerio);
        var сервер = await Json(await host.CreateClient().GetAsync($"{Серверы}/{id}", Ct));

        сервер["store_checked_at"].ShouldNotBeNull(сервер.ToJsonString());
        var хранилище = сервер["store"]!.AsArray().Select(з => з!.AsObject()).ToList();
        хранилище.Count.ShouldBe(kerio.Store.Count);

        var подаётся = хранилище.Where(з => з["served"]!.GetValue<bool>()).ShouldHaveSingleItem();
        подаётся["origin"]!.GetValue<string>().ShouldBe("managed");
        подаётся["sha256"]!.GetValue<string>().ShouldBe(Convert.ToHexString(SHA256.HashData(kerio.ServedCertificate.RawData)));

        var ручной = хранилище.Where(з => з["subject"]!.GetValue<string>() == "legacy.demo.local").ShouldHaveSingleItem();
        ручной["origin"]!.GetValue<string>().ShouldBe("foreign");
        ручной["state"]!.GetValue<string>().ShouldBe("expired");

        хранилище.ShouldContain(з => з["origin"]!.GetValue<string>() == "builtin");

        var сводка = await Json(await host.CreateClient().GetAsync("/api/kerio/overview", Ct));
        сводка["servers"]![0]!["store"]!.AsArray().Count.ShouldBe(хранилище.Count);
    }

    /// <summary>⚠ Удаление сервера — по свежему коду на каждое удаление, а не по общему окну переподтверждения.</summary>
    [Fact]
    public async Task Удаление_сервера_только_со_свежим_кодом()
    {
        var id = await ПодключитьAsync(Стенд.Kerio[0]);
        var клиент = host.CreateClient();

        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-4);
        var отказ = await клиент.DeleteAsync($"{Серверы}/{id}", Ct);
        отказ.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Json(отказ))["code"]!.GetValue<string>().ShouldBe("SECOND_FACTOR_REQUIRED");
        (await клиент.GetAsync($"{Серверы}/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-4);
        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { notify_emails = new[] { "noc@example.com" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        host.User.StepUpAt = DateTimeOffset.UtcNow;
        (await клиент.DeleteAsync($"{Серверы}/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await клиент.GetAsync($"{Серверы}/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Пробное оповещение уходит общим получателям, попадает в журнал оповещений и не чаще раза в минуту.</summary>
    [Fact]
    public async Task Пробное_оповещение_и_журнал_оповещений()
    {
        var клиент = host.CreateClient();

        (await клиент.PostAsync("/api/kerio/notifications/test", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "почта не настроена");

        host.Settings.Values["mail.enabled"] = true;
        host.Settings.Values["mail.smtp_host"] = "127.0.0.1";
        host.Settings.Values["mail.smtp_port"] = 1;
        host.Settings.Values["mail.smtp_security"] = "none";
        host.Settings.Values["mail.from_address"] = "kcm@example.com";
        host.Settings.Values["kerio.alert_emails"] = "noc@example.com, it@example.com";

        var ушло = await Json(await клиент.PostAsync("/api/kerio/notifications/test", null, Ct));
        ушло["recipients"]!.AsArray().Select(а => а!.GetValue<string>()).ShouldBe(["noc@example.com", "it@example.com"]);

        var журнал = await клиент.GetFromJsonAsync<JsonArray>("/api/kerio/notifications", Ct);
        var строка = журнал!.ShouldHaveSingleItem()!;
        строка["event"]!.GetValue<string>().ShouldBe("test");
        строка["queued"]!.GetValue<int>().ShouldBe(2);
        строка["server_id"].ShouldBeNull();

        (await клиент.PostAsync("/api/kerio/notifications/test", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    /// <summary>Выключенное во вкладке «Оповещения» событие не пишется, включённое — пишется и попадает в журнал.</summary>
    [Fact]
    public async Task Выключенное_оповещение_не_уходит()
    {
        НастроитьПочту();
        host.Settings.Values["kerio.alert_expiring"] = false;

        var id = await ПодключитьAsync(Стенд.Kerio[0]);
        var журнал = await host.CreateClient().GetFromJsonAsync<JsonArray>($"/api/kerio/notifications?server_id={id}", Ct);
        журнал!.Select(з => з!["event"]!.GetValue<string>()).ShouldNotContain("expiring");

        host.Settings.Values["kerio.alert_expiring"] = true;
        await ЦиклAsync();

        журнал = await host.CreateClient().GetFromJsonAsync<JsonArray>($"/api/kerio/notifications?server_id={id}", Ct);
        var письмо = журнал!.Where(з => з!["event"]!.GetValue<string>() == "expiring").ShouldHaveSingleItem()!;
        письмо["level"]!.GetValue<string>().ShouldBe("warning");
        письмо["recipients"]!.AsArray().ShouldHaveSingleItem()!.GetValue<string>().ShouldBe("noc@example.com");
    }

    /// <summary>⚠ Сертификат своего имени ложится в хранилище, а по умолчанию остаётся прежний, хотя импорт в 10.0.6 делает новый активным.</summary>
    [Fact]
    public async Task Сертификат_своего_имени_ставится_не_трогая_сертификат_по_умолчанию()
    {
        const string Имя = "webmail.demo.local";
        var kerio = Стенд.Kerio[0];
        var первый = Стенд.Kmp.Issue(Имя, TimeSpan.FromDays(60));
        var id = await ПодключитьAsync(kerio);
        var клиент = host.CreateClient();
        var поУмолчанию = kerio.ServedCertificate.Thumbprint;

        kerio.CertificateFor(Имя).Certificate.Thumbprint.ShouldBe(поУмолчанию, "своего сертификата у имени ещё нет");

        await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { extra_common_names = new[] { Имя } }, Ct);
        await ЦиклAsync();

        kerio.ServedCertificate.Thumbprint.ShouldBe(поУмолчанию, "сертификат по умолчанию не изменился");
        kerio.CertificateFor(Имя).Certificate.SerialNumber.ShouldBe(первый.Certificate.SerialNumber);

        var сервер = await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct));
        var свой = сервер["extras"]!.AsArray().ShouldHaveSingleItem()!;
        свой["common_name"]!.GetValue<string>().ShouldBe(Имя);
        свой["status"]!.GetValue<string>().ShouldBe("ok", сервер.ToJsonString());
        свой["served_by_name"]!.GetValue<bool>().ShouldBeTrue();

        var установки = await клиент.GetFromJsonAsync<JsonArray>($"{Серверы}/{id}/deployments", Ct);
        var установка = установки!.Where(у => у!["common_name"]!.GetValue<string>() == Имя).ShouldHaveSingleItem()!;
        установка["outcome"]!.GetValue<string>().ShouldBe("succeeded", установка.ToJsonString());
        установка["steps"]!.AsArray().Select(ш => ш!["step"]!.GetValue<string>()).ShouldContain("restore_default");

        var второй = Стенд.Kmp.Issue(Имя, TimeSpan.FromDays(90));
        await ЦиклAsync();

        kerio.CertificateFor(Имя).Certificate.SerialNumber.ShouldBe(второй.Certificate.SerialNumber);
        kerio.Store.ShouldContain(з => з.Certificate.SerialNumber == первый.Certificate.SerialNumber);
        kerio.ServedCertificate.Thumbprint.ShouldBe(поУмолчанию);

        Стенд.Kmp.Remove(первый.Serial);
        Стенд.Kmp.Remove(второй.Serial);
        var короче = Стенд.Kmp.Issue(Имя, TimeSpan.FromDays(30));
        await ЦиклAsync();

        kerio.CertificateFor(Имя).Certificate.SerialNumber.ShouldBe(короче.Certificate.SerialNumber);
        kerio.Store.ShouldNotContain(з => з.Certificate.SerialNumber == второй.Certificate.SerialNumber);
        kerio.Store.ShouldNotContain(з => з.Certificate.SerialNumber == первый.Certificate.SerialNumber);

        сервер = await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct));
        сервер["store"]!.AsArray()
            .Where(з => з!["subject"]!.GetValue<string>() == Имя)
            .ShouldHaveSingleItem()!["origin"]!.GetValue<string>().ShouldBe("managed");
    }

    /// <summary>
    /// ⚠ Очередь установки: за проход — один сертификат; нажатые встают первыми и копятся по порядку нажатия. Об удачах
    /// очереди — одно сводное письмо, когда она опустела; одиночная установка — обычным письмом.
    /// </summary>
    [Fact]
    public async Task Очередь_ставит_по_одному_сертификату_за_проход()
    {
        НастроитьПочту();
        var kerio = Стенд.Kerio[0];
        string[] имена = ["a.demo.local", "b.demo.local", "c.demo.local", "d.demo.local"];
        var выпуски = имена.ToDictionary(и => и, и => Стенд.Kmp.Issue(и, TimeSpan.FromDays(90)));
        var id = await ПодключитьAsync(kerio);
        var клиент = host.CreateClient();

        int Стоят() => выпуски.Values.Count(в => kerio.Store.Any(з => з.Certificate.SerialNumber == в.Certificate.SerialNumber));

        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { extra_common_names = имена }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ЦиклAsync()).ShouldBeTrue("в очереди ещё есть что ставить");

        Стоят().ShouldBe(1, "за проход — один сертификат");
        (await ТемыAsync("kerio_deployed")).ShouldBeEmpty("в очереди несколько — удача ждёт сводки");
        var очередь = Очередь(await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct)));
        очередь.Count.ShouldBe(3);

        host.User.StepUpAt = DateTimeOffset.UtcNow;

        foreach (var имя in new[] { очередь[2], очередь[1], очередь[2] })
        {
            (await клиент.PostAsJsonAsync($"{Серверы}/{id}/deploy", new { common_name = имя.ToUpperInvariant() }, Ct)).StatusCode
                .ShouldBe(HttpStatusCode.Accepted);
        }

        Очередь(await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct)))
            .ShouldBe([очередь[2], очередь[1], очередь[0]], "нажатые первыми, повторное нажатие — не второй запрос");

        await ЦиклAsync();
        kerio.Store.ShouldContain(з => з.Certificate.SerialNumber == выпуски[очередь[2]].Certificate.SerialNumber, "первым — нажатый первым");
        Стоят().ShouldBe(2);

        await ЦиклAsync();
        Стоят().ShouldBe(3);
        (await ТемыAsync("kerio_queue_summary")).ShouldBeEmpty("очередь ещё не пуста");

        (await ЦиклAsync()).ShouldBeFalse("очередь пуста");
        Стоят().ShouldBe(4);
        Очередь(await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct))).ShouldBeEmpty();

        var установки = await клиент.GetFromJsonAsync<JsonArray>($"{Серверы}/{id}/deployments", Ct);
        установки!.Count(у => у!["trigger"]!.GetValue<string>() == "manual").ShouldBe(2);

        (await ТемыAsync("kerio_deployed")).ShouldBeEmpty("установки из очереди — не по письму на каждую");
        (await ТемыAsync("kerio_queue_summary")).ShouldHaveSingleItem("одно письмо, когда очередь опустела")
            .ShouldBe("Очередь установки на «Kerio kerio-a» пройдена: установлено 4 из 4");

        var журнал = await клиент.GetFromJsonAsync<JsonArray>($"/api/kerio/notifications?server_id={id}", Ct);
        журнал!.Where(з => з!["event"]!.GetValue<string>() == "queue_summary").ShouldHaveSingleItem()!["level"]!.GetValue<string>()
            .ShouldBe("info");

        var продление = Стенд.Kmp.Issue(имена[0], TimeSpan.FromDays(120));
        (await ЦиклAsync()).ShouldBeFalse("продление одного — очередь из одного");
        kerio.Store.ShouldContain(з => з.Certificate.SerialNumber == продление.Certificate.SerialNumber);

        (await ТемыAsync("kerio_deployed")).ShouldHaveSingleItem("одиночная установка — обычным письмом").ShouldContain(имена[0]);
        (await ТемыAsync("kerio_queue_summary")).Count.ShouldBe(1);
    }

    /// <summary>⚠ Отказ посреди очереди — письмом сразу; удачи — одним сводным письмом, когда очередь опустела.</summary>
    [Fact]
    public async Task Отказ_в_очереди_пишется_сразу_удачи_сводкой()
    {
        НастроитьПочту();
        var kerio = Стенд.Kerio[0];
        string[] имена = ["a.demo.local", "b.demo.local", "c.demo.local"];

        foreach (var имя in имена)
        {
            Стенд.Kmp.Issue(имя, TimeSpan.FromDays(90));
        }

        var id = await ПодключитьAsync(kerio);

        (await host.CreateClient().PatchAsJsonAsync($"{Серверы}/{id}", new { extra_common_names = имена }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await ЦиклAsync()).ShouldBeTrue();

        kerio.FailingMethods.Add("ConnectCertificate.importCertificate");
        await ЦиклAsync();
        kerio.FailingMethods.Clear();

        (await ТемыAsync("kerio_deploy_failed")).ShouldHaveSingleItem("отказ не ждёт конца очереди").ShouldContain(имена[1]);
        (await ТемыAsync("kerio_queue_summary")).ShouldBeEmpty();

        (await ЦиклAsync()).ShouldBeFalse("отказавший ждёт паузы перед повтором — очередь пуста");

        (await ТемыAsync("kerio_deployed")).ShouldBeEmpty();
        (await ТемыAsync("kerio_queue_summary")).ShouldHaveSingleItem()
            .ShouldBe("🟠 Очередь установки на «Kerio kerio-a» пройдена: установлено 2 из 3");
    }

    /// <summary>⚠ Слот установки один на весь сервис: второй не возьмёт его, пока первый не отпустит; чужой не отпустит.</summary>
    [Fact]
    public async Task Слот_установки_один_на_весь_сервис()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var слот = scope.ServiceProvider.GetRequiredService<InstallGate>();
        var первый = Guid.CreateVersion7();
        var второй = Guid.CreateVersion7();

        (await слот.TryAcquireAsync(первый, Ct)).ShouldBeTrue();
        (await слот.TryAcquireAsync(второй, Ct)).ShouldBeFalse();

        await слот.ReleaseAsync(второй);
        (await слот.TryAcquireAsync(второй, Ct)).ShouldBeFalse("чужой владелец слот не отпускает");

        await слот.ReleaseAsync(первый);
        (await слот.TryAcquireAsync(второй, Ct)).ShouldBeTrue();
        await слот.ReleaseAsync(второй);
    }

    /// <summary>Сертификатов своих имён у сервера сколько угодно: лимита нет.</summary>
    [Fact]
    public async Task Сертификатов_имён_без_лимита()
    {
        var имена = Enumerable.Range(1, 30).Select(н => $"host{н}.demo.local").ToArray();

        foreach (var имя in имена)
        {
            Стенд.Kmp.Issue(имя, TimeSpan.FromDays(90));
        }

        var id = await ПодключитьAsync(Стенд.Kerio[0]);
        var клиент = host.CreateClient();

        var ответ = await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { extra_common_names = имена }, Ct);
        ответ.StatusCode.ShouldBe(HttpStatusCode.OK, await ответ.Content.ReadAsStringAsync(Ct));
        (await Json(ответ))["extras"]!.AsArray().Count.ShouldBe(30);
    }

    /// <summary>⚠ Проход держит сервер и сохраняет его в конце: правка посреди него молча пропала бы — отказ «занят».</summary>
    [Fact]
    public async Task Правка_во_время_прохода_отвергается()
    {
        var id = await ПодключитьAsync(Стенд.Kerio[0]);
        var клиент = host.CreateClient();

        await АрендаAsync(id, DateTimeOffset.UtcNow.AddMinutes(30));

        var занят = await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { name = "Kerio переименован" }, Ct);
        занят.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Json(занят))["message"]!.GetValue<string>().ShouldContain("проверяется или обновляется");

        // Адреса оповещений проход не трогает: сохранение нескольких серверов разом не должно упираться в его аренду.
        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { notify_emails = new[] { "noc@example.com" } }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        await АрендаAsync(id, null);

        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { name = "Kerio переименован" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Чего нет в Key Manager Plus — не сохраняется: причина по каждому имени и подсказка написания.</summary>
    [Fact]
    public async Task Сертификат_не_из_KMP_не_сохраняется()
    {
        var id = await ПодключитьAsync(Стенд.Kerio[0]);
        var клиент = host.CreateClient();

        var отказ = await клиент.PatchAsJsonAsync(
            $"{Серверы}/{id}", new { extra_common_names = new[] { "mail.demo.locl", "", "vpn.example.com" } }, Ct);

        отказ.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var тело = await Json(отказ);
        тело["details"]!["field"]!.GetValue<string>().ShouldBe("certificates");
        тело["message"]!.GetValue<string>().ShouldContain("нет в Key Manager Plus");

        var проблемы = тело["details"]!["problems"]!.AsArray().Select(п => п!.AsObject()).ToList();
        проблемы.Count.ShouldBe(3);
        проблемы[0]["suggestion"]!.GetValue<string>().ShouldBe(Почта);
        проблемы[1]["reason"]!.GetValue<string>().ShouldContain("пустое");
        проблемы[2]["suggestion"].ShouldBeNull();

        (await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct)))["extras"]!.AsArray().ShouldBeEmpty();

        var дважды = await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { extra_common_names = new[] { Почта } }, Ct);
        дважды.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Json(дважды))["message"]!.GetValue<string>().ShouldContain("указан дважды");
    }

    /// <summary>Подключить сервер стенда с закреплённым отпечатком и привязкой к <see cref="Почта"/>; первый проход признаёт стоящий.</summary>
    private async Task<Guid> ПодключитьAsync(KerioEmulator kerio)
    {
        await ЦиклAsync();

        var создан = await Json(await host.CreateClient().PostAsJsonAsync(
            Серверы,
            new
            {
                name = "Kerio " + kerio.Name,
                base_url = $"https://localhost:{kerio.AdminPort}",
                username = kerio.User,
                password = kerio.Password,
                tls_mode = "pin",
                tls_pin_sha256 = Convert.ToHexString(SHA256.HashData(kerio.ServedCertificate.RawData)),
                common_name = Почта,
                verify_ports = kerio.ServicePorts,
            },
            Ct));

        await ЦиклAsync();

        return создан["id"]!.GetValue<Guid>();
    }

    [Fact]
    public async Task Неверный_ввод_отвергается_с_именем_поля()
    {
        var клиент = host.CreateClient();

        foreach (var (тело, поле) in new (object Тело, string Поле)[]
        {
            (new { name = "x", base_url = "http://kerio:4040", username = "u", password = "p", tls_mode = "ca" }, "base_url"),
            (new { name = "x", base_url = "https://kerio:4040", username = "u", password = "p", tls_mode = "pin", tls_pin_sha256 = "abc" }, "tls_pin_sha256"),
            (new { name = "x", base_url = "https://kerio:4040", username = "u", password = "p", tls_mode = "none" }, "tls_mode"),
            (new { name = "", base_url = "https://kerio:4040", username = "u", password = "p", tls_mode = "ca" }, "name"),
            (new { name = "x", base_url = "https://kerio:4040", username = "u", tls_mode = "ca" }, "password"),
            (new { name = "x", base_url = "https://kerio:4040", username = "u", password = "p", tls_mode = "ca", verify_ports = new[] { 70000 } }, "verify_ports"),
            (new { name = "x", base_url = "https://kerio:4040", username = "u", password = "p", tls_mode = "ca", verify_ports = new[] { 993, 587 } }, "verify_ports"),
            (new { name = "x", base_url = "https://kerio:4040", username = "u", password = "p", tls_mode = "ca", notify_emails = new[] { "Имя <a@b.c>" } }, "notify_emails"),
        })
        {
            var ответ = await клиент.PostAsJsonAsync(Серверы, тело, Ct);

            ответ.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(ответ))["details"]!["field"]!.GetValue<string>().ShouldBe(поле);
        }
    }

    /// <summary>⚠ Нажатие не переживает отключения и переезда: через месяц оно встало бы ручным решением, в обход правил.</summary>
    [Fact]
    public async Task Отключение_и_переезд_гасят_нажатия()
    {
        var kerio = Стенд.Kerio[0];
        var id = await ПодключитьAsync(kerio);
        var клиент = host.CreateClient();

        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { auto_deploy = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Стенд.Kmp.Issue(Почта, TimeSpan.FromDays(90));
        await ЦиклAsync();

        await НажатьAsync();
        (await Json(await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { enabled = false }, Ct)))["install_queue"]!.AsArray().ShouldBeEmpty();
        (await НажатийAsync(id)).ShouldBe(0);

        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { enabled = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await НажатьAsync();
        var переехал = await Json(await клиент.PatchAsJsonAsync(
            $"{Серверы}/{id}", new { base_url = $"https://127.0.0.1:{kerio.AdminPort}", password = kerio.Password }, Ct));
        переехал["install_queue"]!.AsArray().ShouldBeEmpty();
        (await НажатийAsync(id)).ShouldBe(0);

        async Task НажатьAsync()
        {
            host.User.StepUpAt = DateTimeOffset.UtcNow;
            (await клиент.PostAsJsonAsync($"{Серверы}/{id}/deploy", new { common_name = Почта }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await НажатийAsync(id)).ShouldBe(1);
        }
    }

    /// <summary>⚠ Kerio уже отверг пароль: ещё один вход приближает блокировку учётной записи.</summary>
    [Fact]
    public async Task Отвергнутый_пароль_установку_не_запускает()
    {
        var id = await ПодключитьAsync(Стенд.Kerio[0]);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<KerioCertManager.Modules.Kerio.Domain.KerioServer>()
                .Where(с => с.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(с => с.AuthRejected, true), Ct);
        }

        host.User.StepUpAt = DateTimeOffset.UtcNow;
        var ответ = await host.CreateClient().PostAsJsonAsync($"{Серверы}/{id}/deploy", new { common_name = Почта }, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Json(ответ))["details"]!["field"]!.GetValue<string>().ShouldBe("password");
    }

    /// <summary>Пустое имя снимает сертификат по умолчанию: сервис больше его не меняет и не помнит прежнего.</summary>
    [Fact]
    public async Task Пустое_имя_снимает_сертификат_по_умолчанию()
    {
        var kerio = Стенд.Kerio[0];
        var id = await ПодключитьAsync(kerio);
        var клиент = host.CreateClient();

        var сервер = await Json(await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { common_name = "" }, Ct));
        сервер["common_name"].ShouldBeNull();
        сервер["installed"]?["serial"].ShouldBeNull();

        var был = kerio.ServedCertificate.SerialNumber;
        Стенд.Kmp.Issue(Почта, TimeSpan.FromDays(90));
        await ЦиклAsync();

        kerio.ServedCertificate.SerialNumber.ShouldBe(был);
        (await клиент.GetFromJsonAsync<JsonArray>($"{Серверы}/{id}/deployments", Ct))!.ShouldBeEmpty();
    }

    /// <summary>Все отказали — письма об отказах уже ушли, сводка «0 из 2» ничего не добавила бы.</summary>
    [Fact]
    public async Task Все_отказали_сводки_нет()
    {
        НастроитьПочту();
        var kerio = Стенд.Kerio[0];
        string[] имена = ["a.demo.local", "b.demo.local"];

        foreach (var имя in имена)
        {
            Стенд.Kmp.Issue(имя, TimeSpan.FromDays(90));
        }

        var id = await ПодключитьAsync(kerio);
        (await host.CreateClient().PatchAsJsonAsync($"{Серверы}/{id}", new { extra_common_names = имена }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        kerio.FailingMethods.Add("ConnectCertificate.importCertificate");
        await ЦиклAsync();
        await ЦиклAsync();
        (await ЦиклAsync()).ShouldBeFalse("оба ждут паузы перед повтором");
        kerio.FailingMethods.Clear();

        (await ТемыAsync("kerio_deploy_failed")).Count.ShouldBe(2);
        (await ТемыAsync("kerio_queue_summary")).ShouldBeEmpty();
    }

    /// <summary>Проход очереди идёт каждые 45 секунд — и только к серверам, где есть что ставить.</summary>
    [Fact]
    public async Task Проход_очереди_не_трогает_серверы_без_работы()
    {
        var id = await ПодключитьAsync(Стенд.Kerio[0]);
        var клиент = host.CreateClient();
        var проверен = (await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct)))["last_check_at"]!.GetValue<DateTimeOffset>();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<SyncCycle>().RunAsync(full: false, Ct)).ShouldBeFalse();
        }

        (await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct)))["last_check_at"]!.GetValue<DateTimeOffset>().ShouldBe(проверен);

        await ЦиклAsync();
        (await Json(await клиент.GetAsync($"{Серверы}/{id}", Ct)))["last_check_at"]!.GetValue<DateTimeOffset>().ShouldBeGreaterThan(проверен);
    }

    /// <summary>⚠ Выключенный Key Manager Plus — ничего не ставится, даже из старого снимка и по кнопке.</summary>
    [Fact]
    public async Task Выключенный_KMP_ничего_не_ставит()
    {
        var kerio = Стенд.Kerio[0];
        var id = await ПодключитьAsync(kerio);
        var клиент = host.CreateClient();

        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { auto_deploy = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var был = kerio.ServedCertificate.SerialNumber;
        Стенд.Kmp.Issue(Почта, TimeSpan.FromDays(90));
        await ЦиклAsync();

        host.Settings.Values["keymanager.enabled"] = false;

        host.User.StepUpAt = DateTimeOffset.UtcNow;
        (await клиент.PostAsJsonAsync($"{Серверы}/{id}/deploy", new { common_name = Почта }, Ct)).StatusCode.ShouldNotBe(HttpStatusCode.Accepted);

        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { auto_deploy = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ЦиклAsync();

        kerio.ServedCertificate.SerialNumber.ShouldBe(был);
        (await клиент.GetFromJsonAsync<JsonArray>($"{Серверы}/{id}/deployments", Ct))!.ShouldBeEmpty();
    }

    /// <summary>⚠ KMP заново показал старую пару имени: сам сервис откат не делает.</summary>
    [Fact]
    public async Task Старая_пара_имени_сама_не_ставится()
    {
        const string имя = "a.demo.local";
        var kerio = Стенд.Kerio[0];
        Стенд.Kmp.Issue(имя, TimeSpan.FromDays(90));

        var id = await ПодключитьAsync(kerio);
        var клиент = host.CreateClient();
        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { extra_common_names = new[] { имя } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ЦиклAsync();

        var старая = Стенд.Kmp.Issue(имя, TimeSpan.FromDays(90), TimeSpan.FromDays(30));
        await ЦиклAsync();

        kerio.Store.ShouldNotContain(з => з.Certificate.SerialNumber == старая.Certificate.SerialNumber);
        var последняя = (await клиент.GetFromJsonAsync<JsonArray>($"{Серверы}/{id}/deployments", Ct))![0]!;
        последняя["outcome"]!.GetValue<string>().ShouldBe("failed");
        последняя["error"]!.GetValue<string>().ShouldContain("старше установленного");
    }

    /// <summary>Несколько условий разом — одно письмо, иначе проход с десятком имён завалит ящик.</summary>
    [Fact]
    public async Task Несколько_условий_разом_одно_письмо()
    {
        НастроитьПочту();
        host.Settings.Values["kerio.alert_expiring"] = false;
        var kerio = Стенд.Kerio[0];
        string[] имена = ["a.demo.local", "b.demo.local"];

        foreach (var имя in имена)
        {
            Стенд.Kmp.Issue(имя, TimeSpan.FromDays(12));
        }

        var id = await ПодключитьAsync(kerio);
        var клиент = host.CreateClient();
        (await клиент.PatchAsJsonAsync($"{Серверы}/{id}", new { extra_common_names = имена }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ЦиклAsync();
        await ЦиклAsync();
        await ЦиклAsync();

        host.Settings.Values["kerio.alert_expiring"] = true;
        await ЦиклAsync();

        var журнал = await клиент.GetFromJsonAsync<JsonArray>($"/api/kerio/notifications?server_id={id}", Ct);
        var события = журнал!.Select(з => з!["event"]!.GetValue<string>()).ToList();
        события.ShouldContain("alerts_digest");
        события.ShouldNotContain("expiring");
    }

    /// <summary>Память «уже писали»: тихое условие её держит, неполный проход не стирает, полный — стирает.</summary>
    [Fact]
    public async Task Память_оповещений_переживает_тихое_условие_и_неполный_проход()
    {
        НастроитьПочту();
        var id = await ПодключитьAsync(Стенд.Kerio[0]);

        await using var scope = host.Services.CreateAsyncScope();
        var notifier = scope.ServiceProvider.GetRequiredService<KerioCertManager.Modules.Kerio.Notifications.KerioNotifier>();
        var сервер = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<KerioCertManager.Modules.Kerio.Domain.KerioServer>()
            .SingleAsync(с => с.Id == id, Ct);
        сервер.Alerts.Clear();

        var условие = new ServerAlert(AlertKinds.Drift, global::KerioCertManager.Modules.Kerio.Sync.AlertLevel.Warning, "Сменили вручную", "Сервер подаёт другой сертификат.");

        await notifier.AlertAsync(сервер, [условие], true, Ct);
        сервер.Alerts.ShouldHaveSingleItem();
        var ключ = сервер.Alerts.Keys.Single();

        await notifier.AlertAsync(сервер, [условие with { Quiet = true }], true, Ct);
        сервер.Alerts.Keys.ShouldBe([ключ]);

        await notifier.AlertAsync(сервер, [], false, Ct);
        сервер.Alerts.Keys.ShouldBe([ключ]);

        await notifier.AlertAsync(сервер, [], true, Ct);
        сервер.Alerts.ShouldBeEmpty();
    }

    /// <summary>Сколько нажатий «Обновить» и «Добавить» ждут прохода.</summary>
    private async Task<int> НажатийAsync(Guid id)
    {
        await using var scope = host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<KerioCertManager.Modules.Kerio.Domain.InstallRequest>()
            .CountAsync(з => з.ServerId == id, Ct);
    }

    /// <summary>Аренда сервера, как у прохода синхронизации; <c>null</c> — снять.</summary>
    private async Task АрендаAsync(Guid id, DateTimeOffset? до)
    {
        await using var scope = host.Services.CreateAsyncScope();
        Guid? владелец = до is null ? null : Guid.CreateVersion7();

        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<KerioCertManager.Modules.Kerio.Domain.KerioServer>()
            .Where(с => с.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(с => с.LeaseUntil, до).SetProperty(с => с.LeaseOwner, владелец), Ct);
    }

    /// <summary>Почта настроена (письма не уходят: SMTP закрыт), получатель писем о серверах — noc@example.com.</summary>
    private void НастроитьПочту()
    {
        host.Settings.Values["mail.enabled"] = true;
        host.Settings.Values["mail.smtp_host"] = "127.0.0.1";
        host.Settings.Values["mail.smtp_port"] = 1;
        host.Settings.Values["mail.smtp_security"] = "none";
        host.Settings.Values["mail.from_address"] = "kcm@example.com";
        host.Settings.Values["kerio.alert_emails"] = "noc@example.com";
    }

    /// <summary>Темы писем этого вида в очереди почты, по порядку.</summary>
    private async Task<List<string>> ТемыAsync(string kind)
    {
        await using var scope = host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<string>($"SELECT subject AS \"Value\" FROM mail_outbox WHERE kind = {kind} ORDER BY created_at")
            .ToListAsync(Ct);
    }

    /// <summary>Проход; <c>true</c> — в очереди установки ещё есть что ставить.</summary>
    private async Task<bool> ЦиклAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SyncCycle>().RunAsync(Ct);
    }

    private static List<string> Очередь(JsonObject сервер) =>
        [.. сервер["install_queue"]!.AsArray().Select(и => и!.GetValue<string>())];

    private static async Task<JsonObject> Json(HttpResponseMessage ответ) =>
        (await ответ.Content.ReadFromJsonAsync<JsonObject>(Ct)) ?? throw new InvalidOperationException("пустой ответ");
}
