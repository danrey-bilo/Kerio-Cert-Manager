using Modules.Core.Contracts.Settings;

namespace KerioCertManager.Modules.Kerio.Settings;

/// <summary>Настройки установки сертификатов на серверы Kerio Connect.</summary>
internal static class KerioSettings
{
    public const string Category = "kerio";

    public static readonly SettingKey<bool> AutoDeploy = new(
        "kerio.auto_deploy",
        Default: true,
        "Automatic installation",
        "Install a renewed certificate on every server with automatic installation turned on. Turn off to "
        + "pause all automatic changes: checks, warnings and the Update button keep working.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<bool> DryRun = new(
        "kerio.dry_run",
        Default: false,
        "Dry run",
        "Do everything except changing Kerio: download, verify the key and the chain, sign in to Kerio, "
        + "find the current certificate. Useful for the first days after connecting a server.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<int> WarningDays = new(
        "kerio.warning_days",
        Default: 21,
        "Expiry warning, days",
        "Warn by mail when the certificate a server presents expires within this many days and no newer "
        + "one can be installed. When Key Manager Plus has a newer one, it is installed right away.",
        Category,
        Minimum: 1,
        Maximum: 180);

    public static readonly SettingKey<string?> AlertEmails = new(
        "kerio.alert_emails",
        Default: null,
        "Notification recipients",
        "Comma-separated addresses for installation reports and warnings. Empty: every active user of the "
        + "panel. Each server may add its own recipients.",
        Category,
        Maximum: 2000,
        IsSecurity: true);

    public static readonly SettingKey<string> AlertLocale = new(
        "kerio.alert_locale",
        Default: "ru",
        "Notification language",
        "Language of the notification letters.",
        Category,
        Choices: ["ru", "uk", "en"]);

    public static readonly SettingKey<int> AlertRepeatHours = new(
        "kerio.alert_repeat_hours",
        Default: 24,
        "Repeat warnings, hours",
        "A warning that is still true is mailed again no sooner than this. 0: mail each warning once until "
        + "its condition goes away.",
        Category,
        Minimum: 0,
        Maximum: 720,
        IsSecurity: true);

    public static readonly SettingKey<bool> AlertExpiring = new(
        "kerio.alert_expiring",
        Default: true,
        "Mail: certificate expiring",
        "Warn when the certificate a server presents expires within the warning period.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<string?> AlertReminderDays = new(
        "kerio.alert_reminder_days",
        Default: "7,3,1",
        "Mail: expiry reminders, days",
        "Comma-separated days before expiry, below the warning period: entering each one sends a fresh "
        + "letter at once, whatever the repeat interval. Empty: no reminders.",
        Category,
        Maximum: 100,
        Nullable: true,
        IsSecurity: true);

    public static readonly SettingKey<bool> AlertExpired = new(
        "kerio.alert_expired",
        Default: true,
        "Mail: certificate expired",
        "Warn when a server presents an expired certificate.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<bool> AlertOverdue = new(
        "kerio.alert_overdue",
        Default: true,
        "Mail: not updated by itself",
        "Warn when a renewed certificate from Key Manager Plus is still not on the server after the set time: "
        + "automatic installation is off, keeps failing or the server is unreachable.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<int> AlertOverdueHours = new(
        "kerio.alert_overdue_hours",
        Default: 24,
        "Not updated by itself after, hours",
        "How long a renewed certificate may wait before the warning.",
        Category,
        Minimum: 1,
        Maximum: 720,
        IsSecurity: true);

    public static readonly SettingKey<bool> AlertDeployed = new(
        "kerio.alert_deployed",
        Default: true,
        "Mail: certificate installed",
        "Report successful installations: a single one at once; a queue of several in one summary letter when "
        + "the queue is empty.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<bool> AlertDeployFailed = new(
        "kerio.alert_deploy_failed",
        Default: true,
        "Mail: installation failed",
        "Report an installation that failed or was rolled back to the previous certificate.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<bool> AlertPending = new(
        "kerio.alert_pending",
        Default: true,
        "Mail: waiting for manual installation",
        "Warn when a renewed certificate waits for the Update button because automatic installation is off.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<bool> AlertUnreachable = new(
        "kerio.alert_unreachable",
        Default: true,
        "Mail: server unreachable",
        "Warn when a server does not answer several checks in a row.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<int> AlertUnreachableChecks = new(
        "kerio.alert_unreachable_checks",
        Default: 2,
        "Unreachable after, checks",
        "How many failed checks in a row make a server unreachable.",
        Category,
        Minimum: 1,
        Maximum: 100,
        IsSecurity: true);

    public static readonly SettingKey<bool> AlertAccess = new(
        "kerio.alert_access",
        Default: true,
        "Mail: access problems",
        "Warn when Kerio rejects the service account, the account lacks rights or the server certificate is not trusted.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<bool> AlertDrift = new(
        "kerio.alert_drift",
        Default: true,
        "Mail: changed by hand",
        "Warn when a server presents another certificate than the one the service installed.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<bool> AlertSourceMissing = new(
        "kerio.alert_source_missing",
        Default: true,
        "Mail: missing in Key Manager Plus",
        "Warn when the certificate a server is bound to is missing from Key Manager Plus.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<int> VerifySeconds = new(
        "kerio.verify_seconds",
        Default: 60,
        "Verification time, seconds",
        "After activation the new certificate must appear on every checked port within this time, "
        + "otherwise the previous certificate is put back.",
        Category,
        IsSecurity: true,
        Minimum: 10,
        Maximum: 600);
}

internal sealed class KerioSettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        KerioSettings.AutoDeploy,
        KerioSettings.DryRun,
        KerioSettings.WarningDays,
        KerioSettings.AlertEmails,
        KerioSettings.AlertLocale,
        KerioSettings.AlertRepeatHours,
        KerioSettings.VerifySeconds,
        KerioSettings.AlertExpiring,
        KerioSettings.AlertReminderDays,
        KerioSettings.AlertExpired,
        KerioSettings.AlertOverdue,
        KerioSettings.AlertOverdueHours,
        KerioSettings.AlertDeployed,
        KerioSettings.AlertDeployFailed,
        KerioSettings.AlertPending,
        KerioSettings.AlertUnreachable,
        KerioSettings.AlertUnreachableChecks,
        KerioSettings.AlertAccess,
        KerioSettings.AlertDrift,
        KerioSettings.AlertSourceMissing,
    ];
}
