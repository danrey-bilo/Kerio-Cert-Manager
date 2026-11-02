using System.Text.Json.Serialization;

namespace KerioCertManager.Modules.Kerio.Api;

/// <summary>Что подаёт сервер по TLS.</summary>
internal sealed record ServedOut(
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("serial")] string? Serial,
    [property: JsonPropertyName("not_before")] DateTimeOffset? NotBefore,
    [property: JsonPropertyName("not_after")] DateTimeOffset? NotAfter,
    [property: JsonPropertyName("days_left")] int? DaysLeft);

/// <summary>Что поставил сервис.</summary>
internal sealed record InstalledOut(
    [property: JsonPropertyName("serial")] string? Serial,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("not_before")] DateTimeOffset? NotBefore,
    [property: JsonPropertyName("not_after")] DateTimeOffset? NotAfter,
    [property: JsonPropertyName("at")] DateTimeOffset? At,
    [property: JsonPropertyName("kerio_id")] string? KerioId);

/// <summary>Текущий выпуск привязанного сертификата в Key Manager Plus.</summary>
internal sealed record SourceOut(
    [property: JsonPropertyName("common_name")] string CommonName,
    [property: JsonPropertyName("serial_number")] string SerialNumber,
    [property: JsonPropertyName("expires_on")] DateOnly? ExpiresOn,
    [property: JsonPropertyName("first_seen_at")] DateTimeOffset FirstSeenAt);

/// <summary>Сервер Kerio для панели.</summary>
internal sealed record ServerOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("base_url")] string BaseUrl,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password_set")] bool PasswordSet,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("auto_deploy")] bool AutoDeploy,
    [property: JsonPropertyName("cleanup_old")] bool CleanupOld,
    [property: JsonPropertyName("verify_ports")] int[] VerifyPorts,
    [property: JsonPropertyName("notify_emails")] string[] NotifyEmails,
    [property: JsonPropertyName("tls_mode")] string TlsMode,
    [property: JsonPropertyName("tls_server_name")] string? TlsServerName,
    [property: JsonPropertyName("tls_pin_sha256")] string? TlsPinSha256,
    [property: JsonPropertyName("common_name")] string? CommonName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("status_text")] string StatusText,
    [property: JsonPropertyName("busy")] bool Busy,
    [property: JsonPropertyName("last_check_at")] DateTimeOffset? LastCheckAt,
    [property: JsonPropertyName("last_check_error")] string? LastCheckError,
    [property: JsonPropertyName("auth_rejected")] bool AuthRejected,
    [property: JsonPropertyName("kerio_version")] string? KerioVersion,
    [property: JsonPropertyName("kerio_role")] string? KerioRole,
    [property: JsonPropertyName("kerio_certificate_count")] int? KerioCertificateCount,
    [property: JsonPropertyName("served")] ServedOut? Served,
    [property: JsonPropertyName("installed")] InstalledOut? Installed,
    [property: JsonPropertyName("source")] SourceOut? Source,
    [property: JsonPropertyName("last_deploy_at")] DateTimeOffset? LastDeployAt,
    [property: JsonPropertyName("last_deploy_error")] string? LastDeployError,
    [property: JsonPropertyName("next_attempt_at")] DateTimeOffset? NextAttemptAt,
    [property: JsonPropertyName("install_queue")] IReadOnlyList<string> InstallQueue,
    [property: JsonPropertyName("installing")] string? Installing,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("store")] IReadOnlyList<StoreCertOut> Store,
    [property: JsonPropertyName("store_checked_at")] DateTimeOffset? StoreCheckedAt,
    [property: JsonPropertyName("extras")] IReadOnlyList<ExtraOut> Extras,
    [property: JsonPropertyName("served_by_name")] IReadOnlyDictionary<string, string> ServedByName);

/// <summary>Сертификат своего имени на сервере и его состояние.</summary>
internal sealed record ExtraOut(
    [property: JsonPropertyName("common_name")] string CommonName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("status_text")] string StatusText,
    [property: JsonPropertyName("source")] SourceOut? Source,
    [property: JsonPropertyName("installed_serial")] string? InstalledSerial,
    [property: JsonPropertyName("installed_not_after")] DateTimeOffset? InstalledNotAfter,
    [property: JsonPropertyName("installed_at")] DateTimeOffset? InstalledAt,
    [property: JsonPropertyName("kerio_id")] string? KerioId,
    [property: JsonPropertyName("served_by_name")] bool? ServedByName,
    [property: JsonPropertyName("last_error")] string? LastError,
    [property: JsonPropertyName("next_attempt_at")] DateTimeOffset? NextAttemptAt,
    [property: JsonPropertyName("added_at")] DateTimeOffset AddedAt);

/// <summary>Сертификат в хранилище Kerio — с происхождением и сроком для обзора.</summary>
internal sealed record StoreCertOut(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("not_before")] DateTimeOffset? NotBefore,
    [property: JsonPropertyName("not_after")] DateTimeOffset? NotAfter,
    [property: JsonPropertyName("days_left")] int? DaysLeft,
    [property: JsonPropertyName("serial")] string? Serial,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("dns_names")] IReadOnlyList<string> DnsNames,
    [property: JsonPropertyName("self_signed")] bool SelfSigned,
    [property: JsonPropertyName("untrusted")] bool Untrusted,
    [property: JsonPropertyName("served")] bool Served,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("kmp_common_name")] string? KmpCommonName,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("update_from")] string? UpdateFrom,
    [property: JsonPropertyName("superseded")] bool Superseded);

/// <summary>Отправленное оповещение.</summary>
internal sealed record NotificationOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("server_id")] Guid? ServerId,
    [property: JsonPropertyName("server_name")] string? ServerName,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("recipients")] IReadOnlyList<string> Recipients,
    [property: JsonPropertyName("queued")] int Queued);

/// <summary>Итог пробного оповещения.</summary>
internal sealed record NotificationTestOut([property: JsonPropertyName("recipients")] IReadOnlyList<string> Recipients);

/// <summary>Шаг установки.</summary>
internal sealed record StepOut(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("ok")] bool Ok);

/// <summary>Установка сертификата.</summary>
internal sealed record DeploymentOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("server_id")] Guid ServerId,
    [property: JsonPropertyName("server_name")] string ServerName,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("finished_at")] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("actor_id")] Guid? ActorId,
    [property: JsonPropertyName("common_name")] string CommonName,
    [property: JsonPropertyName("serial_number")] string SerialNumber,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("not_after")] DateTimeOffset? NotAfter,
    [property: JsonPropertyName("previous_sha256")] string? PreviousSha256,
    [property: JsonPropertyName("kerio_certificate_id")] string? KerioCertificateId,
    [property: JsonPropertyName("previous_kerio_certificate_id")] string? PreviousKerioCertificateId,
    [property: JsonPropertyName("failed_step")] string? FailedStep,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("steps")] IReadOnlyList<StepOut>? Steps);

/// <summary>Новый сервер.</summary>
internal sealed record ServerIn(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("base_url")] string? BaseUrl,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("tls_mode")] string? TlsMode,
    [property: JsonPropertyName("tls_pin_sha256")] string? TlsPinSha256,
    [property: JsonPropertyName("tls_server_name")] string? TlsServerName,
    [property: JsonPropertyName("common_name")] string? CommonName,
    [property: JsonPropertyName("auto_deploy")] bool? AutoDeploy,
    [property: JsonPropertyName("cleanup_old")] bool? CleanupOld,
    [property: JsonPropertyName("verify_ports")] int[]? VerifyPorts,
    [property: JsonPropertyName("notify_emails")] string[]? NotifyEmails,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("extra_common_names")] string[]? ExtraCommonNames = null)
{
    /// <summary>⚠ Без пароля: запись может попасть в лог.</summary>
    public override string ToString() => $"{nameof(ServerIn)} {Name} {BaseUrl}";
}

/// <summary>Правка сервера: пустое поле — без изменений.</summary>
internal sealed record ServerPatch(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("base_url")] string? BaseUrl,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("common_name")] string? CommonName,
    [property: JsonPropertyName("auto_deploy")] bool? AutoDeploy,
    [property: JsonPropertyName("cleanup_old")] bool? CleanupOld,
    [property: JsonPropertyName("verify_ports")] int[]? VerifyPorts,
    [property: JsonPropertyName("notify_emails")] string[]? NotifyEmails,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("extra_common_names")] string[]? ExtraCommonNames = null)
{
    /// <summary>Правятся только адреса оповещений: проход их не трогает.</summary>
    public bool OnlyNotifyEmails =>
        NotifyEmails is not null && Name is null && BaseUrl is null && Username is null && Password is null && CommonName is null
        && AutoDeploy is null && CleanupOld is null && VerifyPorts is null && Enabled is null && ExtraCommonNames is null;

    /// <summary>⚠ Без пароля.</summary>
    public override string ToString() => $"{nameof(ServerPatch)} {Name} {BaseUrl}";
}

/// <summary>Какой сертификат поставить сейчас; без имени — все выбранные для сервера.</summary>
internal sealed record DeployIn([property: JsonPropertyName("common_name")] string? CommonName);

/// <summary>Как доверять сертификату Kerio.</summary>
internal sealed record TrustIn(
    [property: JsonPropertyName("tls_mode")] string? TlsMode,
    [property: JsonPropertyName("tls_pin_sha256")] string? TlsPinSha256,
    [property: JsonPropertyName("tls_server_name")] string? TlsServerName);

/// <summary>Что посмотреть по TLS.</summary>
internal sealed record ProbeIn(
    [property: JsonPropertyName("base_url")] string? BaseUrl,
    [property: JsonPropertyName("tls_server_name")] string? TlsServerName);

/// <summary>Сертификат, который подаёт адрес.</summary>
internal sealed record ProbeOut(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("serial")] string Serial,
    [property: JsonPropertyName("not_before")] DateTimeOffset NotBefore,
    [property: JsonPropertyName("not_after")] DateTimeOffset NotAfter,
    [property: JsonPropertyName("dns_names")] IReadOnlyList<string> DnsNames,
    [property: JsonPropertyName("self_signed")] bool SelfSigned,
    [property: JsonPropertyName("chain_trusted")] bool ChainTrusted,
    [property: JsonPropertyName("suggested_mode")] string SuggestedMode,
    [property: JsonPropertyName("suggested_server_name")] string? SuggestedServerName,
    [property: JsonPropertyName("same_certificate_ports")] IReadOnlyList<int> SameCertificatePorts,
    [property: JsonPropertyName("pem")] string Pem);

/// <summary>Проверка входа до сохранения: подключённый сервер проверяет <c>POST servers/{id}/check</c>.</summary>
internal sealed record TestIn(
    [property: JsonPropertyName("base_url")] string? BaseUrl,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("tls_mode")] string? TlsMode,
    [property: JsonPropertyName("tls_pin_sha256")] string? TlsPinSha256,
    [property: JsonPropertyName("tls_server_name")] string? TlsServerName)
{
    public override string ToString() => $"{nameof(TestIn)} {BaseUrl}";
}

/// <summary>Сертификат в хранилище Kerio.</summary>
internal sealed record KerioCertificateOut(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("valid_to")] DateOnly? ValidTo,
    [property: JsonPropertyName("self_signed")] bool SelfSigned);

/// <summary>Итог проверки входа.</summary>
internal sealed record TestOut(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("login_name")] string? LoginName,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("certificates")] IReadOnlyList<KerioCertificateOut> Certificates,
    [property: JsonPropertyName("suggested_common_names")] IReadOnlyList<string> SuggestedCommonNames);

/// <summary>Сводка для главной страницы.</summary>
internal sealed record OverviewOut(
    [property: JsonPropertyName("servers")] IReadOnlyList<ServerOut> Servers,
    [property: JsonPropertyName("source")] SourceStatusOut Source,
    [property: JsonPropertyName("auto_deploy")] bool AutoDeploy,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("warning_days")] int WarningDays,
    [property: JsonPropertyName("interval_minutes")] int IntervalMinutes);

/// <summary>Связь с Key Manager Plus.</summary>
internal sealed record SourceStatusOut([property: JsonPropertyName("certificates")] int Certificates);
