using System.Globalization;
using System.Text;
using Modules.Mail.Secrets;
using Modules.Core.Contracts.Mail;
using Modules.Core.Errors;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;

namespace Modules.Mail.Sending;

/// <summary>Одно письмо, готовое к отправке.</summary>
internal readonly record struct OutgoingMail(
    string ToAddress,
    string Subject,
    string Body,
    IReadOnlyList<MailAttachment>? Attachments = null,
    string? Html = null);

/// <summary>Кто умеет открыть транспорт для отправки.</summary>
internal interface IMailSender
{
    /// <summary>Открыть транспорт.</summary>
    IMailSession Open(MailPolicy policy);
}

/// <summary>Открытый транспорт: сколько угодно писем, одно соединение.</summary>
internal interface IMailSession : IAsyncDisposable
{
    /// <summary>Послать одно письмо.</summary>
    Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default);
}

/// <summary>Отправка писем по SMTP: одно соединение на порцию.</summary>
internal sealed class SmtpSender(SystemSecrets secrets, MailModuleOptions? options = null) : IMailSender
{
    public IMailSession Open(MailPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var хост = policy.Host;

        // ⚠ Проверка здесь, а не в сессии.
        if (string.IsNullOrWhiteSpace(хост))
        {
            throw new AppException(ErrorCodes.Validation, "Адрес почтового сервера не задан.");
        }

        // ⚠ Пустое имя отправителя — название продукта из параметров подключения (решение 0111).
        if (string.IsNullOrWhiteSpace(policy.FromName))
        {
            policy = policy with { FromName = options?.ProductName ?? MailModuleOptions.DefaultProductName };
        }

        var политика = policy;

        return new SmtpSession(
            policy,
            хост,
            token => ПарольAsync(политика, token),
            options?.AllowPlaintextAuth ?? false,
            passwordBeforeConnect: Привязан);
    }

    /// <summary>Привязан ли пароль к адресу: параметр включён и пароль хранится в базе (решение 0120).</summary>
    private bool Привязан => options is { BindSmtpPassword: true, Features.StoredPassword: true };

    /// <summary>Пароль SMTP — из базы или от проекта (решение 0112, <c>mail.stored_password</c>).</summary>
    internal Task<string?> ПарольAsync(CancellationToken cancellationToken) => ПарольAsync(null, cancellationToken);

    /// <summary>Пароль SMTP для соединения по этим настройкам.</summary>
    internal Task<string?> ПарольAsync(MailPolicy? policy, CancellationToken cancellationToken)
    {
        if (options?.Features.StoredPassword == false)
        {
            return Task.FromResult(options.SmtpPassword);
        }

        if (Привязан)
        {
            ArgumentNullException.ThrowIfNull(policy);

            return secrets.GetForAsync(SystemSecretNames.SmtpPassword, Цель(policy), cancellationToken);
        }

        return secrets.GetAsync(SystemSecretNames.SmtpPassword, cancellationToken);
    }

    /// <summary>«https://Mail.Example.com/» → «Mail.Example.com»: имя сервера без схемы и хвостовой косой.</summary>
    internal static string ИмяСервера(string хост)
    {
        ArgumentNullException.ThrowIfNull(хост);

        var имя = хост.Trim();
        var схема = имя.IndexOf("://", StringComparison.Ordinal);

        return (схема >= 0 ? имя[(схема + 3)..] : имя).TrimEnd('/');
    }

    /// <summary>Куда уходит пароль SMTP: защита, учётная запись, имя сервера и порт одной строкой (решение 0120).</summary>
    internal static string Цель(MailPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var имя = ИмяСервера(policy.Host ?? string.Empty).ToLowerInvariant();
        var учётка = Uri.EscapeDataString(policy.Username?.Trim() ?? string.Empty);

        return string.Create(CultureInfo.InvariantCulture, $"{policy.Security}://{учётка}@{имя}:{policy.Port}");
    }

    /// <summary>Кодировка тем и тел — одна на всё.</summary>
    static SmtpSender() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}

/// <summary>Одно соединение SMTP на порцию писем.</summary>
internal sealed class SmtpSession(
    MailPolicy policy,
    string host,
    Func<CancellationToken, Task<string?>> password,
    bool allowPlaintextAuth = false,
    bool passwordBeforeConnect = false)
    : IMailSession
{
    /// <summary>Сколько ждём сервер.</summary>
    private const int ТаймаутМиллисекунд = 30_000;

    private readonly SmtpClient _клиент = new() { Timeout = ТаймаутМиллисекунд };

    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        // ⚠ ⚠ Проверка перед КАЖДЫМ письмом, а не только перед первым.
        if (!_клиент.IsConnected)
        {
            await ПодключитьсяAsync(cancellationToken);
        }

        using var сообщение = Собрать(policy, mail);

        await _клиент.SendAsync(сообщение, cancellationToken);
    }

    /// <summary>Подключиться и войти; при любом отказе — разорвать.</summary>
    private async Task ПодключитьсяAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ПодключитьсяИВойтиAsync(cancellationToken);
        }
        catch
        {
            await РазорватьAsync(CancellationToken.None);

            throw;
        }
    }

    /// <summary>Разорвать соединение без <c>QUIT</c>; отказ при этом не важен.</summary>
    private async Task РазорватьAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_клиент.IsConnected)
            {
                await _клиент.DisconnectAsync(quit: false, cancellationToken);
            }
        }
        catch (Exception ошибка) when (ошибка is not OperationCanceledException)
        {
        }
    }

    private async Task ПодключитьсяИВойтиAsync(CancellationToken cancellationToken)
    {
        // ⚠ ⚠ Привязанный пароль (решение 0120) открывается ДО соединения.
        var заранее = passwordBeforeConnect && !string.IsNullOrWhiteSpace(policy.Username)
            ? await password(cancellationToken)
            : null;

        await _клиент.ConnectAsync(host, policy.Port, Защита(policy.Security), cancellationToken);

        // ⚠ Реле в той же сети авторизации не требует, и требовать её значило бы запретить законную установку.
        if (string.IsNullOrWhiteSpace(policy.Username))
        {
            return;
        }

        // ⚠ Вход без TLS — отказ до AUTH (ревизия 2026-10-26, решение 0118).
        if (!_клиент.IsSecure && !allowPlaintextAuth)
        {
            throw new PlaintextAuthRefusedException();
        }

        var пароль = passwordBeforeConnect ? заранее : await password(cancellationToken);

        await _клиент.AuthenticateAsync(policy.Username, пароль ?? string.Empty, cancellationToken);
    }

    /// <summary>Закрыть соединение по-хорошему.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_клиент.IsConnected)
            {
                await _клиент.DisconnectAsync(quit: true, CancellationToken.None);
            }
        }
        catch (Exception ошибка) when (ошибка is not OperationCanceledException)
        {
        }
        finally
        {
            _клиент.Dispose();
        }
    }

    /// <summary>Собрать сообщение.</summary>
    private static MimeMessage Собрать(MailPolicy policy, OutgoingMail mail)
    {
        var сообщение = new MimeMessage();

        // ⚠ ⚠ Адрес отправителя ЗАДАН, а не угадан (вскрытие 44).
        var отправитель = policy.FromAddress
            ?? throw new InvalidOperationException(
                "Адрес отправителя не задан — MailPolicy.Configured обязан был это остановить");

        // ⚠ Отображаемое имя кодируется MimeKit само (RFC 2047) — вручную его собирать не надо, и попытка сделать это руками ломает нелатинские имена.
        сообщение.From.Add(new MailboxAddress(policy.FromName, отправитель));
        сообщение.To.Add(MailboxAddress.Parse(mail.ToAddress));
        сообщение.Subject = mail.Subject;

        сообщение.Headers.Add("Auto-Submitted", "auto-generated");

        // ⚠ ⚠ Переводы строк — CRLF ДО кодирования (решение 0120).
        var текст = new TextPart(TextFormat.Plain)
        {
            Text = mail.Body.ReplaceLineEndings("\r\n"),
            ContentTransferEncoding = ContentEncoding.Base64,
        };

        // ⚠ С HTML (решение 0121) — `multipart/alternative`: текст первым, HTML последним — клиент показывает последнюю понятную ему часть.
        MimeEntity тело = текст;

        if (mail.Html is { } html)
        {
            тело = new MultipartAlternative
            {
                текст,
                new TextPart(TextFormat.Html)
                {
                    Text = html.ReplaceLineEndings("\r\n"),
                    ContentTransferEncoding = ContentEncoding.Base64,
                },
            };
        }

        if (mail.Attachments is not { Count: > 0 } вложения)
        {
            сообщение.Body = тело;

            return сообщение;
        }

        // ⚠ С вложениями — `multipart/mixed`: тело первой частью (текст или пара «текст + HTML»), файлы — частями-вложениями.
        var смесь = new Multipart("mixed") { тело };

        foreach (var файл in вложения)
        {
            смесь.Add(new MimePart(ContentType.Parse(MailAttachments.Тип(файл.ContentType)))
            {
                Content = new MimeContent(new MemoryStream(файл.Content, writable: false)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = MailAttachments.Имя(файл.FileName),
            });
        }

        сообщение.Body = смесь;

        return сообщение;
    }

    /// <summary>Собрать сообщение — для проверки состава письма без сервера.</summary>
    internal static MimeMessage СобратьДляПроверки(MailPolicy policy, OutgoingMail mail) => Собрать(policy, mail);

    /// <summary>Режим защиты соединения из настройки.</summary>
    private static SecureSocketOptions Защита(string security) => security switch
    {
        "tls" => SecureSocketOptions.SslOnConnect,
        "starttls" => SecureSocketOptions.StartTls,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };

}

/// <summary>Вход на сервер SMTP без TLS не выполняется: пароль ушёл бы открытым текстом.</summary>
internal sealed class PlaintextAuthRefusedException()
    : Exception("Вход на сервер SMTP без TLS запрещён: включите STARTTLS или SSL либо уберите имя пользователя.");
