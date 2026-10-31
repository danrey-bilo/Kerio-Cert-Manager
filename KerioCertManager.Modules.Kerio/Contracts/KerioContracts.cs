using Modules.Core.Contracts.Audit;

namespace KerioCertManager.Modules.Kerio.Contracts;

/// <summary>События журнала модуля Kerio.</summary>
public static class KerioActions
{
    /// <summary>Вид ресурса в строке журнала.</summary>
    public const string ServerResource = "kerio_server";

    public static readonly AuditAction ServerCreated = new("kerio.server_created", "Подключён сервер Kerio Connect");

    public static readonly AuditAction ServerUpdated = new("kerio.server_updated", "Изменены настройки сервера Kerio Connect");

    public static readonly AuditAction ServerDeleted = new("kerio.server_deleted", "Сервер Kerio Connect отключён от сервиса");

    public static readonly AuditAction PasswordSet = new("kerio.password_set", "Задан пароль учётной записи Kerio");

    public static readonly AuditAction TrustUpdated = new("kerio.trust_updated", "Изменено доверие к сертификату Kerio");

    public static readonly AuditAction DeployRequested = new("kerio.deploy_requested", "Запрошена установка сертификата");

    public static readonly AuditAction CertificateDeployed = new("kerio.certificate_deployed", "Сертификат установлен на Kerio");

    public static readonly AuditAction DeployDryRun =
        new("kerio.deploy_dry_run", "Пробный прогон установки: всё готово, Kerio не изменён");

    public static readonly AuditAction DeployFailed = new("kerio.deploy_failed", "Установка сертификата не удалась");

    public static readonly AuditAction DeployRolledBack =
        new("kerio.deploy_rolled_back", "Новый сертификат не встал — возвращён прежний");

    public static IReadOnlyList<AuditAction> All { get; } =
    [
        ServerCreated,
        ServerUpdated,
        ServerDeleted,
        PasswordSet,
        TrustUpdated,
        DeployRequested,
        CertificateDeployed,
        DeployDryRun,
        DeployFailed,
        DeployRolledBack,
    ];
}

internal sealed class KerioAuditActions : IAuditActionProvider
{
    /// <inheritdoc />
    public IReadOnlyList<AuditActionGroup> Groups { get; } = [new("kerio", KerioActions.All, "Kerio Connect")];
}

/// <summary>Коды ошибок модуля; строки не менять — по ним решает панель.</summary>
public static class KerioErrorCodes
{
    /// <summary>Сервера нет (<c>404</c>).</summary>
    public const string ServerNotFound = "KERIO_SERVER_NOT_FOUND";

    /// <summary>Имя сервера уже занято (<c>409</c>).</summary>
    public const string NameTaken = "KERIO_SERVER_NAME_TAKEN";

    /// <summary>Kerio недоступен или отказал: <c>details.reason</c> (<c>409</c>, не <c>5xx</c>: Cloudflare).</summary>
    public const string Unavailable = "KERIO_UNAVAILABLE";

    /// <summary>Сертификат Kerio не совпал с закреплённым или не прошёл проверку (<c>409</c>).</summary>
    public const string TlsUntrusted = "KERIO_TLS_UNTRUSTED";

    /// <summary>Сервер сейчас занят установкой (<c>409</c>).</summary>
    public const string Busy = "KERIO_SERVER_BUSY";
}
