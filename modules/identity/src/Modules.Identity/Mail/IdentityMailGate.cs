using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;

namespace Modules.Identity.Mail;

/// <summary>Почта для писем учётных записей — с переключателем <c>identity.mail</c>.</summary>
internal sealed class IdentityMailGate(IdentityModuleOptions options, IMailQueue? queue = null, IUserLocale? locales = null)
{
    /// <summary>Можно ли сейчас поставить письмо.</summary>
    public async Task<bool> CanSendAsync(CancellationToken cancellationToken = default) =>
        options.Features.Mail && queue is not null && await queue.CanSendAsync(cancellationToken);

    /// <summary>Поставить письмо; без подвозможности — «почта не настроена».</summary>
    public Task<MailQueueResult> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default) =>
        options.Features.Mail && queue is not null
            ? queue.EnqueueAsync(toAddress, kind, locale, values, expiresAt, cancellationToken)
            : Task.FromResult(MailQueueResult.NotConfigured);

    /// <summary>Язык писем человека; без модуля настроек — английский, как умолчание объявления.</summary>
    public Task<string> LocaleAsync(Guid userId, CancellationToken cancellationToken = default) =>
        locales?.ForAsync(userId, cancellationToken) ?? Task.FromResult("en");
}
