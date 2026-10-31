using KerioCertManager.Modules.Kerio.Certificates;

namespace KerioCertManager.Modules.Kerio.Domain;

/// <summary>Как сервис доверяет сертификату административного порта Kerio.</summary>
internal static class TlsModes
{
    /// <summary>Цепочка до удостоверяющего центра и имя (<see cref="KerioServer.TlsServerName"/> или хост адреса).</summary>
    public const string Ca = "ca";

    /// <summary>Отпечаток SHA-256 сертификата, подтверждённый администратором.</summary>
    public const string Pin = "pin";

    public static bool Known(string? mode) => mode is Ca or Pin;
}

/// <summary>Сервер Kerio Connect: куда ставить сертификаты и что там сейчас.</summary>
internal sealed class KerioServer
{
    public const int MaxName = 100;

    public const int MaxExtras = 20;

    public const int MaxNotifyEmails = 20;

    public const int MaxVerifyPorts = 10;

    /// <summary>UUIDv7 из приложения.</summary>
    public required Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Адрес администрирования: <c>https://host:4040</c>.</summary>
    public required string BaseUrl { get; set; }

    /// <summary>Учётная запись Kerio для API: отдельная, с правами на сертификаты, без второго фактора.</summary>
    public required string Username { get; set; }

    /// <summary>Пароль, запечатанный конвертом и привязанный к адресу и логину.</summary>
    public byte[]? Password { get; set; }

    /// <summary>Ключ данных пароля, обёрнутый мастер-ключом.</summary>
    public byte[]? DataKey { get; set; }

    /// <summary><see cref="TlsModes"/>.</summary>
    public required string TlsMode { get; set; }

    /// <summary>Имя в сертификате для режима <see cref="TlsModes.Ca"/>, если адрес — IP или другое имя.</summary>
    public string? TlsServerName { get; set; }

    /// <summary>Закреплённый SHA-256 (hex, заглавные, без разделителей) для режима <see cref="TlsModes.Pin"/>.</summary>
    public string? TlsPinSha256 { get; set; }

    /// <summary>Сертификат по умолчанию из Key Manager Plus (Common Name); <c>null</c> — сервис его не меняет.</summary>
    public string? CommonName { get; set; }

    /// <summary>Сертификаты своих имён из Key Manager Plus: Kerio подаёт их по SNI, сервис держит их свежими.</summary>
    public List<ExtraCertificate> Extras { get; set; } = [];

    /// <summary>Ставить продлённый сам; иначе — только письмо и кнопка «Установить».</summary>
    public bool AutoDeploy { get; set; } = true;

    /// <summary>Порты служб, на которых проверять поданный сертификат после установки, кроме порта администрирования.</summary>
    public int[] VerifyPorts { get; set; } = [];

    /// <summary>Кому ещё писать о событиях этого сервера.</summary>
    public string[] NotifyEmails { get; set; } = [];

    /// <summary>Удалять прежние версии сертификата в Kerio после удачной установки, оставляя одну предыдущую.</summary>
    public bool CleanupOld { get; set; }

    /// <summary>Отключённый сервер не проверяется и не обновляется.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? LastCheckAt { get; set; }

    /// <summary><c>null</c> — проверка удалась.</summary>
    public string? LastCheckError { get; set; }

    /// <summary>Подряд неудачных проверок — письмо о недоступности не с первого сбоя.</summary>
    public int CheckFailures { get; set; }

    /// <summary>Kerio отверг логин или пароль: сам сервис больше не входит, пока их не сменят или не нажмут «Проверить».</summary>
    public bool AuthRejected { get; set; }

    /// <summary>Версия Kerio Connect из последней проверки.</summary>
    public string? KerioVersion { get; set; }

    /// <summary>Роль учётной записи API в Kerio (<c>FullAdmin</c>…).</summary>
    public string? KerioRole { get; set; }

    /// <summary>Сколько сертификатов в хранилище Kerio.</summary>
    public int? KerioCertificateCount { get; set; }

    public string? ServedSha256 { get; set; }

    public string? ServedSubject { get; set; }

    public string? ServedIssuer { get; set; }

    /// <summary>Серийный номер в нормальном виде (hex строчными без ведущих нулей).</summary>
    public string? ServedSerial { get; set; }

    public DateTimeOffset? ServedNotBefore { get; set; }

    public DateTimeOffset? ServedNotAfter { get; set; }

    /// <summary>Серийный номер в записи KMP последнего поставленного (или признанного своим) выпуска.</summary>
    public string? InstalledSerial { get; set; }

    public string? InstalledSha256 { get; set; }

    /// <summary>Защита от отката: выпущенный раньше сам не ставится.</summary>
    public DateTimeOffset? InstalledNotBefore { get; set; }

    public DateTimeOffset? InstalledNotAfter { get; set; }

    public DateTimeOffset? InstalledAt { get; set; }

    /// <summary>Идентификатор сертификата в хранилище Kerio (<c>server3</c>).</summary>
    public string? InstalledKerioId { get; set; }

    /// <summary>Серийные прежних выпусков, поставленных сервисом, — новые первыми.</summary>
    public string[] PreviousSerials { get; set; } = [];

    /// <summary>Выпуск поставлен сервисом как сертификат по умолчанию: текущий или прежний.</summary>
    public bool Installed(string? serial) => InstalledHistory.Contains(InstalledSerial, PreviousSerials, serial);

    public DateTimeOffset? LastDeployAt { get; set; }

    /// <summary><c>null</c> — последняя установка удалась или её не было.</summary>
    public string? LastDeployError { get; set; }

    /// <summary>Серийный KMP, который не удалось поставить, — неудачи считаются по нему.</summary>
    public string? FailedSerial { get; set; }

    public int DeployFailures { get; set; }

    /// <summary>Раньше этого повтор не делается.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Выпуск, уже прогнанный пробно: пробный прогон не повторяется каждый проход.</summary>
    public string? DryRunSerial { get; set; }

    /// <summary>Администратор нажал «Установить сейчас».</summary>
    public DateTimeOffset? DeployRequestedAt { get; set; }

    public Guid? DeployRequestedBy { get; set; }

    /// <summary>Аренда сервера одним проходом: двое не ставят одновременно.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    public Guid? LeaseOwner { get; set; }

    /// <summary>Когда какое предупреждение уходило письмом: вид → время (повтор не чаще срока).</summary>
    public Dictionary<string, DateTimeOffset> Alerts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Что лежит в хранилище Kerio по последнему удачному входу в API — для обзора.</summary>
    public List<StoredCertificate> Store { get; set; } = [];

    /// <summary>Когда хранилище прочитано; <c>null</c> — ни разу.</summary>
    public DateTimeOffset? StoreCheckedAt { get; set; }

    /// <summary>Что сервер подаёт на порту администрирования для каждого имени (SNI): имя → SHA-256.</summary>
    public Dictionary<string, string> ServedByName { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Все имена сертификатов KMP, которые сервис ведёт на этом сервере: по умолчанию и свои.</summary>
    public IReadOnlyList<string> ManagedNames() =>
        [.. (CommonName is null ? [] : new[] { CommonName }).Concat(Extras.Select(д => д.CommonName))];
}

/// <summary>Сертификат своего имени на сервере: Kerio подаёт его по SNI, сервис ставит продления в хранилище.</summary>
internal sealed class ExtraCertificate
{
    /// <summary>Common Name в Key Manager Plus.</summary>
    public required string CommonName { get; set; }

    /// <summary>Когда добавлен в список: от этого считается «не обновился сам».</summary>
    public DateTimeOffset AddedAt { get; set; }

    /// <summary>Серийный KMP выпуска, который лежит в хранилище Kerio.</summary>
    public string? InstalledSerial { get; set; }

    public string? InstalledSha256 { get; set; }

    public DateTimeOffset? InstalledNotAfter { get; set; }

    public DateTimeOffset? InstalledAt { get; set; }

    /// <summary>Идентификатор в хранилище Kerio.</summary>
    public string? KerioId { get; set; }

    public DateTimeOffset? LastDeployAt { get; set; }

    /// <summary><c>null</c> — последняя установка удалась или её не было.</summary>
    public string? LastError { get; set; }

    public string? FailedSerial { get; set; }

    public int Failures { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Выпуск, уже прогнанный пробно.</summary>
    public string? DryRunSerial { get; set; }

    /// <summary>Серийные прежних выпусков, поставленных сервисом, — новые первыми.</summary>
    public List<string> PreviousSerials { get; set; } = [];

    /// <summary>Выпуск поставлен сервисом: текущий или прежний.</summary>
    public bool Installed(string? serial) => InstalledHistory.Contains(InstalledSerial, PreviousSerials, serial);
}

/// <summary>Какие выпуски сервис поставил: текущий и прежние серийные — по ним уборка отличает свои версии от чужих.</summary>
internal static class InstalledHistory
{
    /// <summary>Сколько прежних серийных помнить.</summary>
    public const int MaxPrevious = 10;

    public static bool Contains(string? installed, IEnumerable<string> previous, string? serial) =>
        serial is not null
        && (Fingerprints.SameSerial(installed, serial) || previous.Any(п => Fingerprints.SameSerial(п, serial)));

    /// <summary>Прежние серийные после смены <paramref name="installed"/> на <paramref name="next"/>: сменённый — первым.</summary>
    public static string[] Push(string? installed, IEnumerable<string> previous, string next) =>
        installed is null || Fingerprints.SameSerial(installed, next)
            ? [.. previous]
            : [installed, .. previous.Where(п => !Fingerprints.SameSerial(п, installed)).Take(MaxPrevious - 1)];
}

/// <summary>Сертификат в хранилище Kerio: поля <c>ConnectCertificate.get</c>, отпечаток и срок — из <c>toSource</c>.</summary>
internal sealed record StoredCertificate(
    string Id,
    string Type,
    string Subject,
    string Issuer,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    string? Serial,
    string? Sha256,
    IReadOnlyList<string> DnsNames,
    bool SelfSigned,
    bool Untrusted)
{
    /// <summary>Встроенный самоподписанный сертификат Kerio — тот, на который Kerio падает без своего.</summary>
    public bool BuiltIn => Id == "server" && SelfSigned;
}

/// <summary>Почему началась установка.</summary>
internal static class DeployTriggers
{
    /// <summary>В Key Manager Plus новый выпуск того же Common Name.</summary>
    public const string Renewal = "renewal";

    /// <summary>Сертификат на сервере скоро истекает, а в KMP есть дольше действующий.</summary>
    public const string Expiring = "expiring";

    /// <summary>Привязка только что задана, на сервере стоит другой сертификат.</summary>
    public const string Initial = "initial";

    /// <summary>Администратор нажал «Установить сейчас».</summary>
    public const string Manual = "manual";

    /// <summary>Повтор после неудачи.</summary>
    public const string Retry = "retry";
}

/// <summary>Чем кончилась установка.</summary>
internal static class DeployOutcomes
{
    public const string Running = "running";

    public const string Succeeded = "succeeded";

    public const string Failed = "failed";

    /// <summary>Новый не встал по TLS — вернули прежний.</summary>
    public const string RolledBack = "rolled_back";

    /// <summary>Пробный прогон: всё проверено, в Kerio ничего не менялось.</summary>
    public const string DryRun = "dry_run";
}

/// <summary>Шаг установки — для ленты в панели и для разбора отказа.</summary>
internal sealed record DeployStep(DateTimeOffset At, string Step, string Message, bool Ok = true);

/// <summary>Одна установка сертификата на сервер.</summary>
internal sealed class KerioDeployment
{
    public required Guid Id { get; set; }

    public required Guid ServerId { get; set; }

    /// <summary>Имя сервера на момент установки: строка переживает удаление сервера.</summary>
    public required string ServerName { get; set; }

    public required string Trigger { get; set; }

    public required string Outcome { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Кто нажал «Установить сейчас»; <c>null</c> — сам сервис.</summary>
    public Guid? ActorId { get; set; }

    public required string CommonName { get; set; }

    public required string SerialNumber { get; set; }

    public string? Sha256 { get; set; }

    public DateTimeOffset? NotAfter { get; set; }

    public string? PreviousSha256 { get; set; }

    public string? KerioCertificateId { get; set; }

    public string? PreviousKerioCertificateId { get; set; }

    /// <summary>На каком шаге отказ.</summary>
    public string? FailedStep { get; set; }

    public string? Error { get; set; }

    public List<DeployStep> Steps { get; set; } = [];
}

/// <summary>Отправленное оповещение: что, о каком сервере, кому — для вкладки «Оповещения».</summary>
internal sealed class KerioNotification
{
    public const int MaxRecipients = 50;

    public required Guid Id { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary><c>null</c> — общее письмо (проверка оповещений).</summary>
    public Guid? ServerId { get; set; }

    /// <summary>Имя сервера на момент письма: строка переживает удаление сервера.</summary>
    public string? ServerName { get; set; }

    /// <summary>Событие: вид предупреждения (<see cref="Sync.AlertKinds"/>) или исход установки.</summary>
    public required string Event { get; set; }

    /// <summary><c>info</c>, <c>warning</c>, <c>critical</c>.</summary>
    public required string Level { get; set; }

    public required string Title { get; set; }

    public string[] Recipients { get; set; } = [];

    /// <summary>Сколько писем принято в очередь.</summary>
    public int Queued { get; set; }
}
