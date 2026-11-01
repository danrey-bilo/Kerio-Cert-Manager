namespace Modules.Mail.Sending;

/// <summary>Почта настроена так, что слать нельзя: отказ составлен модулем, до чужого сервера.</summary>
internal sealed class MailConfigurationException(string message) : InvalidOperationException(message);
