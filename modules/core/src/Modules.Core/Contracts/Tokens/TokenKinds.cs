namespace Modules.Core.Contracts.Tokens;

/// <summary>Виды предъявляемых токенов.</summary>
public static class TokenKinds
{
    /// <summary>Вход человека браузером.</summary>
    public const string Session = "session";

    /// <summary>Настольный клиент на конкретной машине.</summary>
    public const string Device = "device";

    /// <summary>Ключ интеграции: скрипт, выгрузка, чужая система.</summary>
    public const string ApiKey = "api_key";

    /// <summary>Двухминутный билет на скачивание установщика.</summary>
    public const string DownloadTicket = "download_ticket";

    /// <summary>Имя куки, которой предъявляется билет на скачивание.</summary>
    public const string DownloadTicketCookie = "eams_client_ticket";

    /// <summary>Все допустимые значения — для ограничения в базе.</summary>
    public static readonly string[] All = [Session, Device, ApiKey, DownloadTicket];
}
