using Modules.Core.Contracts.Mail;

namespace Modules.Identity.Mail;

/// <summary>Виды писем, которые шлёт модуль личностей.</summary>
internal static class IdentityMailKinds
{
    /// <summary>Приглашение: ссылка, по которой человек задаёт себе пароль.</summary>
    public const string Invitation = "invitation";

    /// <summary>Восстановление пароля.</summary>
    public const string PasswordReset = "password_reset";

    /// <summary>Доступ приостановлен.</summary>
    public const string AccountSuspended = "account_suspended";

    /// <summary>Одноразовый код входа.</summary>
    public const string LoginCode = "login_code";
}

/// <summary>Тексты писем модуля личностей: четыре вида на трёх языках.</summary>
[Modules.Core.Features.Feature("identity.mail")]
internal sealed class IdentityMailTemplates : IMailTemplateProvider
{
    public IReadOnlyList<MailTemplate> Templates { get; } =
    [
        new(
            IdentityMailKinds.Invitation,
            MailLocales.Ru,
            "{product}: приглашение",
            "Для вас создана учётная запись в {product} — хранилище учётных данных.\n\n"
            + "Пароль задаёте вы сами, по ссылке. Она действует {minutes} минут и работает\n"
            + "один раз:\n{link}\n\n"
            + "Администратор вашего пароля не знает и знать не будет.\n\n"
            + "Ссылка истекла — попросите администратора прислать новую."),
        new(
            IdentityMailKinds.Invitation,
            MailLocales.Uk,
            "{product}: запрошення",
            "Для вас створено обліковий запис у {product} — сховищі облікових даних.\n\n"
            + "Пароль задаєте ви самі, за посиланням. Воно діє {minutes} хвилин і працює\n"
            + "один раз:\n{link}\n\n"
            + "Адміністратор вашого пароля не знає і знати не буде.\n\n"
            + "Посилання застаріло — попросіть адміністратора надіслати нове."),
        new(
            IdentityMailKinds.Invitation,
            MailLocales.En,
            "{product}: invitation",
            "An account has been created for you in {product}, a credential store.\n\n"
            + "You set the password yourself, through this link. It works once and expires\n"
            + "in {minutes} minutes:\n{link}\n\n"
            + "Your administrator does not know your password and never will.\n\n"
            + "If the link has expired, ask your administrator for a new one."),

        new(
            IdentityMailKinds.PasswordReset,
            MailLocales.Ru,
            "{product}: восстановление пароля",
            "Кто-то запросил смену пароля для этого адреса.\n\n"
            + "Ссылка действует {minutes} минут и срабатывает один раз:\n{link}\n\n"
            + "После смены пароля все ваши сессии будут закрыты — войти придётся заново на\n"
            + "всех устройствах.\n\n"
            + "Если запрос не ваш, ничего делать не нужно: без перехода по ссылке пароль\n"
            + "не меняется. Но если такие письма приходят повторно, сообщите администратору —\n"
            + "значит, ваш адрес кому-то известен и его пробуют.\n\n"
            + "Пароль от приложения на компьютере этой ссылкой не меняется: им зашифрована\n"
            + "локальная копия, и сервер её открыть не может."),
        new(
            IdentityMailKinds.PasswordReset,
            MailLocales.Uk,
            "{product}: відновлення пароля",
            "Хтось запросив зміну пароля для цієї адреси.\n\n"
            + "Посилання діє {minutes} хвилин і працює один раз:\n{link}\n\n"
            + "Після зміни пароля всі ваші сесії буде закрито — увійти доведеться заново на\n"
            + "всіх пристроях.\n\n"
            + "Якщо запит не ваш, робити нічого не потрібно: без переходу за посиланням пароль\n"
            + "не змінюється. Але якщо такі листи надходять повторно, повідомте адміністратора —\n"
            + "отже, вашу адресу комусь відомо і її пробують.\n\n"
            + "Пароль від програми на комп’ютері цим посиланням не змінюється: ним зашифровано\n"
            + "локальну копію, і сервер її відкрити не може."),
        new(
            IdentityMailKinds.PasswordReset,
            MailLocales.En,
            "{product}: password reset",
            "Someone asked to change the password for this address.\n\n"
            + "The link works once and expires in {minutes} minutes:\n{link}\n\n"
            + "Changing the password closes all your sessions — you will sign in again on\n"
            + "every device.\n\n"
            + "If this was not you, do nothing: the password does not change until the link is\n"
            + "opened. But if these letters keep arriving, tell your administrator — it means\n"
            + "someone knows your address and is trying it.\n\n"
            + "This link does not change the password of the desktop app: that one encrypts\n"
            + "your local copy, and the server cannot open it."),

        new(
            IdentityMailKinds.AccountSuspended,
            MailLocales.Ru,
            "{product}: доступ приостановлен",
            "Доступ к {product} для этого адреса приостановлен.\n\n"
            + "Форма входа причину не называет — она отвечает одинаково на все случаи, чтобы\n"
            + "не подсказывать посторонним, какие адреса существуют. Поэтому мы пишем письмом.\n\n"
            + "Восстановить доступ может администратор."),
        new(
            IdentityMailKinds.AccountSuspended,
            MailLocales.Uk,
            "{product}: доступ припинено",
            "Доступ до {product} для цієї адреси припинено.\n\n"
            + "Форма входу причину не називає — вона відповідає однаково на всі випадки, щоб\n"
            + "не підказувати стороннім, які адреси існують. Тому ми пишемо листом.\n\n"
            + "Відновити доступ може адміністратор."),
        new(
            IdentityMailKinds.AccountSuspended,
            MailLocales.En,
            "{product}: access suspended",
            "Access to {product} for this address has been suspended.\n\n"
            + "The sign-in form does not say why — it answers the same way in every case, so it\n"
            + "cannot tell strangers which addresses exist. That is why we write instead.\n\n"
            + "Your administrator can restore access."),

        new(
            IdentityMailKinds.LoginCode,
            MailLocales.Ru,
            "{product}: код для входа",
            "Код для входа: {code}\n\n"
            + "Он действует {minutes} минут и работает один раз.\n\n"
            + "Если вы не входили — кто-то знает ваш пароль. Смените его и сообщите\n"
            + "администратору."),
        new(
            IdentityMailKinds.LoginCode,
            MailLocales.Uk,
            "{product}: код для входу",
            "Код для входу: {code}\n\n"
            + "Він діє {minutes} хвилин і працює один раз.\n\n"
            + "Якщо ви не входили — хтось знає ваш пароль. Змініть його та повідомте\n"
            + "адміністратора."),
        new(
            IdentityMailKinds.LoginCode,
            MailLocales.En,
            "{product}: sign-in code",
            "Sign-in code: {code}\n\n"
            + "It works once and expires in {minutes} minutes.\n\n"
            + "If this was not you, someone knows your password. Change it and tell your\n"
            + "administrator."),
    ];
}
