using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Mail.Templates;

/// <summary>Собирает справочник писем при старте.</summary>
internal sealed class MailTemplatesService(
    IServiceProvider services, ILogger<MailTemplatesService> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var справочник = services.GetRequiredService<MailTemplates>();

        log.LogInformation("mail_templates_ready {Kinds}", справочник.Kinds.Count);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
