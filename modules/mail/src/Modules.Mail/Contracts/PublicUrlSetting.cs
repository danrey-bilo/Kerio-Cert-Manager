using Modules.Mail.Settings;
using Modules.Core.Contracts.Settings;

namespace Modules.Mail.Contracts;

/// <summary>Ключ внешнего адреса установки — наружу, для хоста.</summary>
public static class PublicUrlSetting
{
    /// <summary>Ключ настройки внешнего адреса.</summary>
    public static SettingKey<string?> Key => MailSettings.PublicUrl;
}
