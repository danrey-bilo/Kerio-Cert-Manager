using KerioCertManager.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KerioCertManager.IntegrationTests;

/// <summary>Заголовки защиты: на API, на панели, HSTS только по HTTPS.</summary>
[Collection(KerioCertManagerHost.Collection)]
public sealed class SecurityHeadersTests(KerioCertManagerHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ответ_API_не_кэшируется_и_несёт_заголовки_защиты()
    {
        var ответ = await host.CreateClient().GetAsync("/api/health", Ct);

        ответ.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        ответ.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        ответ.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");
        ответ.Headers.GetValues("Permissions-Policy").Single().ShouldContain("camera=()");
        ответ.Headers.GetValues("Cross-Origin-Opener-Policy").Single().ShouldBe("same-origin");
        ответ.Headers.GetValues("Content-Security-Policy").Single().ShouldBe("default-src 'none'; frame-ancestors 'none'");
        ответ.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
    }

    /// <summary>По HTTP через доверенный прокси — 308 на HTTPS; прямой запрос и проверка здоровья — как есть.</summary>
    [Theory]
    [InlineData("/", true, 308)]
    [InlineData("/api/me", true, 308)]
    [InlineData("/api/ready", true, 200)]
    [InlineData("/", false, 200)]
    public async Task HTTP_через_прокси_уходит_на_HTTPS(string путь, bool черезПрокси, int код)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("certs.example.com");
        context.Request.Path = путь;

        if (черезПрокси)
        {
            context.Request.Headers["X-Original-For"] = "172.31.250.2:51000";
        }

        await new HttpsBehindProxy(_ => { _.Response.StatusCode = 200; return Task.CompletedTask; }).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(код);

        if (код == 308)
        {
            context.Response.Headers.Location.ToString().ShouldBe("https://certs.example.com" + путь);
        }
    }

    [Fact]
    public async Task Ответ_с_ошибкой_тоже_несёт_заголовки_защиты()
    {
        // ⚠ Личность общая на коллекцию — без выхода запрос пришёл бы от соседа.
        host.User.SignOut();
        var ответ = await host.CreateClient().GetAsync("/api/me", Ct);

        ответ.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        ответ.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        ответ.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
    }

    [Fact]
    public async Task HSTS_только_на_ответ_по_HTTPS()
    {
        var поHttp = await host.CreateClient().GetAsync("/api/health", Ct);
        поHttp.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();

        var клиент = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var поHttps = await клиент.GetAsync("/api/health", Ct);

        поHttps.Headers.GetValues("Strict-Transport-Security").Single().ShouldStartWith("max-age=31536000");
    }
}
