using Modules.Mail.Catalog;
using Modules.Mail.Secrets;
using Modules.Mail.Sending;
using Modules.Mail.Settings;
using Modules.Mail.Storage;
using Modules.Mail.Templates;
using Modules.Core.Data;
using Modules.Core.Contracts.Data;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;
using Modules.Crypto;
using Modules.Core.Redaction;
using Modules.Mail.Contracts;
using Modules.Core.Features;
using Modules.Core.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Mail;

/// <summary>Подключение модуля почты.</summary>
public sealed class MailModuleOptions
{
    /// <summary>Название продукта по умолчанию — нейтральное: имя задаёт проект.</summary>
    public const string DefaultProductName = "Vault";

    /// <summary>Название продукта в письмах всех модулей и в имени отправителя по умолчанию.</summary>
    public string ProductName { get; set; } = DefaultProductName;

    /// <summary>Переключатели подвозможностей.</summary>
    public MailModuleFeatures Features { get; } = new();

    /// <summary>Пароль SMTP, переданный проектом, — например, из файла секрета.</summary>
    public string? SmtpPassword { get; set; }

    /// <summary>Разрешить вход на сервер SMTP без TLS.</summary>
    public bool AllowPlaintextAuth { get; set; }

    /// <summary>Рамка письма: приветствие в начале, «спасибо» и название продукта в конце.</summary>
    public bool Frame { get; set; }

    /// <summary>Привязать хранимый пароль SMTP к серверу, порту, защите и учётной записи, для которых его ввели.</summary>
    public bool BindSmtpPassword { get; set; }

    /// <summary>Слать каждое письмо двумя частями: текстом и HTML, собранным модулем из того же текста.</summary>
    public bool Html { get; set; }

    /// <summary>Обслуживать ли уборку отправленных в этом процессе.</summary>
    public bool RunMaintenance { get; set; }
}

/// <summary>Переключатели модуля почты (решения 0111, 0112).</summary>
public sealed class MailModuleFeatures() : ModuleFeatures("mail")
{
    /// <summary>Проверочное письмо из админки.</summary>
    [Feature("mail.test_message", Description = "Проверочное письмо из админки: соединение с сервером из настроек и его ответ")]
    public bool TestMessage { get; set; } = true;

    /// <summary>Пароль SMTP в базе, запечатанным, с правкой из админки.</summary>
    [Feature("mail.stored_password", Description = "Пароль SMTP хранится в базе запечатанным и меняется из админки")]
    public bool StoredPassword { get; set; } = true;

    /// <inheritdoc />
    protected override IEnumerable<FeatureOwnership> Owned() =>
    [
        new("mail.test_message", Actions: [MailActions.TestSent.Name, MailActions.TestFailed.Name]),
        new("mail.stored_password", Actions: [MailActions.PasswordSet.Name]),
    ];
}

public static class MailModule
{
    /// <summary>Подключить почту.</summary>
    public static IMvcBuilder AddMailModule(
        this IMvcBuilder mvc, Action<MailModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var options = new MailModuleOptions();
        configure?.Invoke(options);

        // ⚠ Два источника пароля — отказ, а не выбор одного молча.
        if (options.SmtpPassword is not null && options.Features.StoredPassword)
        {
            throw new InvalidOperationException(
                "Пароль SMTP передан проектом, а хранение пароля в базе включено (mail.stored_password): "
                + "у пароля стало бы два источника. Выключите mail.stored_password.");
        }

        var services = mvc.Services;
        services.AddSingleton(options);

        services.AddSingleton<IEntityConfigurationProvider, MailEntities>();

        // ⚠ Свой набор миграций (решение 0114): проект применяет его своей командой миграций.
        services.AddModuleSchema(Schema.MailSchema.Declaration);

        services.AddScoped<SystemSecrets>();
        services.AddScoped<IRewrapTarget, SystemSecretsRewrap>();

        // ⚠ Тела писем в очереди запечатаны тем же конвертом (решение 0029), и цели ротации у них не было до решения 0117.
        services.AddScoped<IRewrapTarget, Sending.MailOutboxRewrap>();

        services.AddSingleton<MailTemplates>();
        services.AddHostedService<MailTemplatesService>();
        services.AddSingleton<IMailTemplateProvider, MailOwnTemplates>();

        services.AddScoped<IMailQueue, MailQueue>();

        services.AddSingleton<MailWakeup>();
        services.AddScoped<IMailSender, SmtpSender>();
        services.AddScoped<MailDelivery>();
        services.AddHostedService<MailDeliveryService>();

        // ⚠ Уборка — единственное необратимое действие модуля, и поднимается она только в обслуживающем процессе.
        if (options.RunMaintenance)
        {
            services.AddHostedService<SentMailCleanup>();
        }

        services.AddSingleton(options.Features.Filter(new MailSettingDeclarations()));
        services.AddSingleton(options.Features.Filter(new MailOwnActions()));
        services.AddSingleton<ISensitiveKeyProvider, MailSensitiveKeys>();

        services.Require<ISettingsReader>("mail", "почта не знает ни сервера, ни отправителя, и письма не уходят");

        // ⚠ Без этого `internal`-контроллеры не находятся, и получается 404 без единой ошибки при старте.
        return mvc.AddModule(typeof(MailModule).Assembly, options.Features);
    }
}
