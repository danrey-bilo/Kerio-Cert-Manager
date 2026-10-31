using KerioCertManager.Modules.Kerio.Certificates;
using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Domain;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Клиент Kerio по настоящему HTTPS: вход, CSRF-токен, хранилище, доверие к сертификату порта.</summary>
public sealed class ClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Сессия_читает_хранилище_роль_и_версию()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);

        await using var сессия = await стенд.Соединитель.OpenAsync(сервер, Ct);

        var хранилище = await сессия.CertificatesAsync(Ct);
        хранилище.Count.ShouldBe(2, "встроенный и установленный");

        var встроенный = хранилище.Single(с => с.Id == KerioCertificate.BuiltInId);
        встроенный.Type.ShouldBe(KerioCertificate.Active);
        встроенный.IsSelfSigned.ShouldBeTrue();

        var активный = хранилище.Single(с => с.IsDefault);
        активный.Subject.ShouldBe(Stand.Почта);
        активный.ValidTo.ShouldBe(DateOnly.FromDateTime(стенд.A.ServedCertificate.NotAfter.ToUniversalTime()), "месяц у Kerio с нуля");
        активный.Chain.ShouldContain(з => з.StartsWith("Kerio Cert Manager Demo Issuing CA", StringComparison.Ordinal));

        (await сессия.WhoAmIAsync(Ct)).Role.ShouldBe("FullAdmin");
        (await сессия.VersionAsync(Ct)).ShouldNotBeNull().ShouldStartWith("10.0.6");

        using var лист = Fingerprints.FirstFromPem(await сессия.SourceAsync(активный.Id, Ct));
        Fingerprints.Sha256(лист!).ShouldBe(Fingerprints.Sha256(стенд.A.ServedCertificate));
    }

    /// <summary>⚠ Как живой 10.0.6: после изменения хранилища все сессии истекают — сессия входит снова и повторяет вызов.</summary>
    [Fact]
    public async Task Сессия_входит_снова_после_перезапуска_HTTPS()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        var лишний = стенд.Host.Pki.Issue(["old.example.com"], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var id = стенд.A.Keep(лишний, лишний.ExportCertificatePem());
        стенд.A.SessionResetAfterChange = TimeSpan.FromMilliseconds(200);

        await using var сессия = await стенд.Соединитель.OpenAsync(сервер, Ct);
        var входов = стенд.A.Logins;

        (await сессия.RemoveAsync([id], Ct)).ShouldBeEmpty();
        await Task.Delay(TimeSpan.FromMilliseconds(600), Ct);

        (await сессия.CertificatesAsync(Ct)).ShouldNotContain(с => с.Id == id);
        стенд.A.Logins.ShouldBe(входов + 1, "истёкшая сессия — новый вход, а не отказ");
    }

    /// <summary>Kerio не даёт удалить сертификат по умолчанию — причина читается из его ответа.</summary>
    [Fact]
    public async Task Сертификат_по_умолчанию_удалить_нельзя()
    {
        await using var стенд = await Stand.StartAsync();
        await using var сессия = await стенд.Соединитель.OpenAsync(стенд.Сервер(стенд.A, Stand.Почта), Ct);

        (await сессия.RemoveAsync([стенд.A.ConfigActiveId], Ct)).ShouldHaveSingleItem().ShouldBe("Cannot delete the default certificate.");
    }

    [Fact]
    public async Task Неверный_пароль_отказ_входа()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта, password: "неверный");

        var ошибка = await Should.ThrowAsync<KerioException>(() => стенд.Соединитель.OpenAsync(сервер, Ct));

        ошибка.Kind.ShouldBe(KerioFailure.Authentication);
        ошибка.Message.ShouldContain("логин или пароль");
        ошибка.Message.ShouldNotContain("неверный", Case.Sensitive, "пароль не попадает в текст отказа");
        стенд.A.FailedLogins.ShouldBe(1);
    }

    [Fact]
    public async Task Чужой_отпечаток_не_пускает_пароль_на_сервер()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        сервер.TlsPinSha256 = new string('A', 64);

        var ошибка = await Should.ThrowAsync<KerioException>(() => стенд.Соединитель.OpenAsync(сервер, Ct));

        ошибка.Kind.ShouldBe(KerioFailure.Tls);
        стенд.A.Logins.ShouldBe(0, "до входа дело не дошло");
        стенд.A.FailedLogins.ShouldBe(0);
    }

    [Fact]
    public async Task Режим_центра_сертификации_не_доверяет_неизвестному_корню()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        сервер.TlsMode = TlsModes.Ca;
        сервер.TlsServerName = Stand.Почта;

        (await Should.ThrowAsync<KerioException>(() => стенд.Соединитель.OpenAsync(сервер, Ct))).Kind.ShouldBe(KerioFailure.Tls);
    }

    [Fact]
    public async Task Доверие_ставящемуся_сертификату_сверх_закреплённого()
    {
        await using var стенд = await Stand.StartAsync();
        var сервер = стенд.Сервер(стенд.A, Stand.Почта);
        var настоящий = Fingerprints.Sha256(стенд.A.ServedCertificate);
        сервер.TlsPinSha256 = new string('B', 64);

        await using var сессия = await стенд.Соединитель.OpenAsync(сервер, [настоящий], Ct);

        (await сессия.CertificatesAsync(Ct)).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Инспектор_видит_что_подаётся_на_каждом_порту()
    {
        await using var стенд = await Stand.StartAsync();

        foreach (var порт in new[] { стенд.A.AdminPort, стенд.A.ServicePorts[0] })
        {
            var подан = await стенд.Tls.InspectAsync("localhost", порт, Ct);

            подан.Sha256.ShouldBe(Fingerprints.Sha256(стенд.A.ServedCertificate));
            подан.Subject.ShouldBe(Stand.Почта);
            подан.SelfSigned.ShouldBeFalse();
            подан.ChainTrusted.ShouldBeFalse("учебный корень системе неизвестен");
        }

        var закрыт = await Should.ThrowAsync<TlsException>(() => стенд.Tls.InspectAsync("localhost", Emulators.EmulatorHost.FreePort(), Ct));
        закрыт.Message.ShouldContain("порт закрыт");
    }

    [Theory]
    [InlineData("http://kerio.example.com:4040")]
    [InlineData("ftp://kerio.example.com")]
    [InlineData("kerio.example.com")]
    public void Адрес_только_https(string адрес) =>
        Should.Throw<KerioException>(() => KerioConnector.BaseUri(адрес)).Kind.ShouldBe(KerioFailure.Transport);

    [Fact]
    public void Ошибки_JSON_RPC_разбираются_по_видам()
    {
        Отказ(1000, "Session.login").Kind.ShouldBe(KerioFailure.Authentication);
        Отказ(-32001, "ConnectCertificate.get").Kind.ShouldBe(KerioFailure.Transport, "истёкшая сессия — не отказ пароля");
        Отказ(1004, "ConnectCertificate.get").Kind.ShouldBe(KerioFailure.Forbidden);
        Отказ(-32601, "ConnectCertificate.get").Kind.ShouldBe(KerioFailure.Protocol);
        Отказ(-32602, "ConnectCertificate.get").Kind.ShouldBe(KerioFailure.Protocol);

        var прочее = Отказ(1000, "ConnectCertificate.importCertificate");
        прочее.Kind.ShouldBe(KerioFailure.Rejected);
        прочее.Message.ShouldContain("importCertificate");

        static KerioException Отказ(int код, string метод) =>
            KerioSession.Отказ(new System.Text.Json.Nodes.JsonObject { ["code"] = код, ["message"] = "текст Kerio" }, метод);
    }
}
