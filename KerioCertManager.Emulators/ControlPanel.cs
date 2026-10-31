using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KerioCertManager.Emulators;

/// <summary>Панель стенда: что в KMP, что на серверах, кнопки «продлить», «залипнуть», «отказать».</summary>
internal static class ControlPanel
{
    public static void Map(
        WebApplication app, EmulatorOptions options, TestPki pki, KmpEmulator kmp, IReadOnlyDictionary<int, KerioEmulator> серверы)
    {
        bool Панель(HttpContext c) => c.Connection.LocalPort == options.ControlPort;

        app.MapGet("/", (HttpContext c) => Панель(c) ? Results.Content(Страница(options, kmp, серверы), "text/html; charset=utf-8") : Results.NotFound());

        app.MapGet("/crl/root.crl", () => Results.Bytes(pki.RootCrl(), "application/pkix-crl"));
        app.MapGet("/crl/issuing.crl", () => Results.Bytes(pki.IssuingCrl(), "application/pkix-crl"));

        app.MapGet("/demo-ca.pem", (HttpContext c) =>
            Панель(c) ? Results.Text(pki.Root.ExportCertificatePem(), "application/x-pem-file") : Results.NotFound());

        app.MapPost("/emulator/kmp/renew", (HttpContext c, string cn, int? days) =>
        {
            if (!Панель(c))
            {
                return Results.NotFound();
            }

            var выпуск = kmp.Issue(cn, TimeSpan.FromDays(days ?? 90));
            return Results.Redirect($"/?done=renewed:{WebUtility.UrlEncode(выпуск.CommonName)}");
        });

        app.MapPost("/emulator/kerio/{name}/stuck", (HttpContext c, string name, bool on) =>
        {
            if (!Панель(c))
            {
                return Results.NotFound();
            }

            серверы.Values.First(к => к.Name == name).Stuck = on;
            return Results.Redirect("/");
        });

        app.MapPost("/emulator/kerio/{name}/role", (HttpContext c, string name, string role) =>
        {
            if (!Панель(c))
            {
                return Results.NotFound();
            }

            серверы.Values.First(к => к.Name == name).Role = role;
            return Results.Redirect("/");
        });

        app.MapPost("/emulator/kerio/{name}/fail", (HttpContext c, string name, string? method) =>
        {
            if (!Панель(c))
            {
                return Results.NotFound();
            }

            var kerio = серверы.Values.First(к => к.Name == name);
            kerio.FailingMethods.Clear();

            if (!string.IsNullOrWhiteSpace(method))
            {
                kerio.FailingMethods.Add(method);
            }

            return Results.Redirect("/");
        });

        app.MapGet("/emulator/state", (HttpContext c) => Панель(c)
            ? Results.Json(new
            {
                kmp = kmp.Entries.Select(в => new { в.CommonName, в.Serial, notAfter = в.Certificate.NotAfter }),
                kerio = серверы.Values.Distinct().Select(к => new
                {
                    к.Name,
                    к.AdminPort,
                    к.ServicePorts,
                    к.ConfigActiveId,
                    к.ServedId,
                    к.Stuck,
                    к.Role,
                    к.Logins,
                    к.FailedLogins,
                    store = к.Store.Select(з => new { з.Id, subject = з.Certificate.Subject, serial = з.Certificate.SerialNumber, notAfter = з.Certificate.NotAfter }),
                }),
            })
            : Results.NotFound());
    }

    private static string Страница(EmulatorOptions options, KmpEmulator kmp, IReadOnlyDictionary<int, KerioEmulator> серверы)
    {
        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Kerio Cert Manager — стенд</title><meta http-equiv="refresh" content="10">
            <style>
            body{font:14px/1.5 system-ui,sans-serif;margin:0;background:#0f172a;color:#e2e8f0}
            main{max-width:1100px;margin:0 auto;padding:24px 16px}
            h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 8px;color:#93c5fd}
            p.muted{color:#94a3b8;margin:0 0 16px}
            table{width:100%;border-collapse:collapse;background:#111827;border-radius:12px;overflow:hidden}
            th,td{padding:8px 10px;border-bottom:1px solid #1f2937;text-align:left;font-size:13px;vertical-align:top}
            th{color:#94a3b8;font-weight:600}
            code{font:12px ui-monospace,monospace;color:#cbd5e1}
            .card{background:#111827;border:1px solid #1f2937;border-radius:12px;padding:14px;margin:10px 0}
            .row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
            button,input,select{font:inherit;border-radius:8px;border:1px solid #334155;background:#1e293b;color:#e2e8f0;padding:6px 10px}
            button{cursor:pointer;background:#2563eb;border-color:#2563eb}
            .pill{display:inline-block;padding:1px 8px;border-radius:999px;font-size:12px;background:#1e293b}
            .ok{background:#064e3b;color:#6ee7b7}.warn{background:#78350f;color:#fcd34d}
            </style></head><body><main>
            <h1>Kerio Cert Manager — демо-стенд</h1>
            <p class="muted">Эмулятор Key Manager Plus и серверов Kerio Connect 10.0.6. Страница обновляется каждые 10 секунд.</p>
            """);

        html.Append($"<h2>Key Manager Plus — https://…:{options.KmpPort}, ключ API <code>{WebUtility.HtmlEncode(options.KmpApiKey)}</code></h2>");
        html.Append("<table><tr><th>Common Name</th><th>Серийный</th><th>Действует до</th><th>Добавлен</th></tr>");

        foreach (var в in kmp.Entries.OrderBy(в => в.CommonName).ThenByDescending(в => в.CreatedAt))
        {
            html.Append($"<tr><td>{WebUtility.HtmlEncode(в.CommonName)}</td><td><code>{в.Serial}</code></td>"
                + $"<td>{в.Certificate.NotAfter.ToUniversalTime():yyyy-MM-dd HH:mm}</td><td>{в.CreatedAt:yyyy-MM-dd HH:mm:ss}</td></tr>");
        }

        html.Append("</table>");

        foreach (var имя in kmp.Entries.Select(в => в.CommonName).Distinct())
        {
            html.Append($"""
                <form method="post" action="/emulator/kmp/renew?cn={WebUtility.UrlEncode(имя)}" class="row" style="margin-top:8px">
                <span>Продлить <b>{WebUtility.HtmlEncode(имя)}</b> в KMP</span>
                <select name="days"><option value="90">90 дней</option><option value="47">47 дней</option><option value="365">365 дней</option></select>
                <button>Продлить</button></form>
                """);
        }

        foreach (var kerio in серверы.Values.Distinct())
        {
            var подаётся = kerio.ServedCertificate;

            html.Append($"""
                <h2>Kerio «{WebUtility.HtmlEncode(kerio.Name)}» — https://…:{kerio.AdminPort}, службы: {string.Join(", ", kerio.ServicePorts)}</h2>
                <div class="card"><div class="row">
                <span>Подаётся по TLS: <b>{WebUtility.HtmlEncode(подаётся.GetNameInfo(X509NameType.SimpleName, false))}</b> до {подаётся.NotAfter:yyyy-MM-dd} <code>{kerio.ServedId}</code></span>
                <span class="pill">в списке активный: {kerio.ConfigActiveId}</span>
                <span class="pill {(kerio.Stuck ? "warn" : "ok")}">{(kerio.Stuck ? "залипание включено" : "переключается")}</span>
                <span class="pill">роль: {kerio.Role}</span>
                <span class="pill">входов: {kerio.Logins}, неудачных: {kerio.FailedLogins}</span>
                </div><div class="row" style="margin-top:10px">
                <form method="post" action="/emulator/kerio/{kerio.Name}/stuck?on={(!kerio.Stuck).ToString().ToLowerInvariant()}"><button>{(kerio.Stuck ? "Снять залипание" : "Залипнуть (проверить откат)")}</button></form>
                <form method="post" action="/emulator/kerio/{kerio.Name}/role?role={(kerio.Role == "FullAdmin" ? "Auditor" : "FullAdmin")}"><button>Роль → {(kerio.Role == "FullAdmin" ? "Auditor" : "FullAdmin")}</button></form>
                <form method="post" action="/emulator/kerio/{kerio.Name}/fail" class="row"><select name="method">
                <option value="">без отказов</option><option>ConnectCertificate.importCertificate</option><option>ConnectCertificate.setActive</option><option>ConnectCertificate.importPrivateKey</option>
                </select><button>Отказ метода</button></form></div></div>
                <table><tr><th>id</th><th>Субъект</th><th>Серийный</th><th>До</th></tr>
                """);

            foreach (var з in kerio.Store)
            {
                html.Append($"<tr><td><code>{з.Id}</code></td><td>{WebUtility.HtmlEncode(з.Certificate.GetNameInfo(X509NameType.SimpleName, false))}</td>"
                    + $"<td><code>{з.Certificate.SerialNumber}</code></td><td>{з.Certificate.NotAfter:yyyy-MM-dd}</td></tr>");
            }

            html.Append("</table>");
        }

        html.Append($"<p class=\"muted\" style=\"margin-top:24px\">Учётная запись Kerio: <code>{WebUtility.HtmlEncode(options.KerioUser)}</code> / <code>{WebUtility.HtmlEncode(options.KerioPassword)}</code>. Корень стенда — <a style=\"color:#93c5fd\" href=\"/demo-ca.pem\">demo-ca.pem</a>.</p>");
        html.Append("</main></body></html>");

        return html.ToString();
    }
}
