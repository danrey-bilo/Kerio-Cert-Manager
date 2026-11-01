using Modules.Core.Errors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Mail.Sending;

/// <summary>Насос очереди писем: по сигналу и по сроку, срок разрежается на пустой очереди.</summary>
internal sealed class MailDeliveryService(
    IServiceScopeFactory scopes,
    MailWakeup wakeup,
    ILogger<MailDeliveryService> log) : BackgroundService
{
    /// <summary>Обычный срок: столько ждали всегда, и столько ждём при живой очереди.</summary>
    private static readonly TimeSpan Обычный = TimeSpan.FromSeconds(15);

    /// <summary>Потолок разрежения.</summary>
    private static readonly TimeSpan Потолок = TimeSpan.FromMinutes(1);

    /// <summary>Сколько пустых проходов терпим, прежде чем удваивать срок.</summary>
    private const int ПустыхДоРазрежения = 4;

    /// <summary>Короткий догляд после пустого прохода, разбуженного сигналом.</summary>
    internal static readonly TimeSpan Догляд = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var пустых = 0;
        var поСигналу = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var после = await ПройтиAsync(пустых, stoppingToken);
            (пустых, var срок) = Следующий(пустых, после, поСигналу);

            var ожидание = await ЖдатьAsync(срок, stoppingToken);

            if (ожидание is null)
            {
                return;
            }

            поСигналу = ожидание.Value;
        }
    }

    /// <summary>Что делать после прохода: новый счётчик пустых и сколько спать.</summary>
    internal static (int Пустых, TimeSpan Срок) Следующий(int было, int после, bool поСигналу) =>
        поСигналу && после > было
            ? (было, Догляд)
            : (после, Срок(после));

    /// <summary>Сколько спать после N пустых проходов подряд.</summary>
    internal static TimeSpan Срок(int пустыхПодряд)
    {
        if (пустыхПодряд < ПустыхДоРазрежения)
        {
            return Обычный;
        }

        var множитель = 1 << Math.Min(пустыхПодряд - ПустыхДоРазрежения + 1, 8);
        var срок = Обычный * множитель;

        return срок < Потолок ? срок : Потолок;
    }

    /// <summary>Один проход.</summary>
    private async Task<int> ПройтиAsync(int пустых, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();

            var итог = await scope.ServiceProvider
                .GetRequiredService<MailDelivery>()
                .RunAsync(cancellationToken);

            // ⚠ Считаются ВЗЯТЫЕ, а не ушедшие: порция, которая вся отказала, — это «сервер не отвечает, но работа есть», и разреживать ход тут нельзя.
            return итог.Taken == 0 ? пустых + 1 : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return пустых;
        }
        catch (Exception ошибка)
        {
            // ⚠ Описанием, а не исключением объектом: строка подключения Npgsql содержит пароль целиком (правило 14).
            log.LogError("mail_delivery_failed {Error}", SafeError.Describe(ошибка));

            // ⚠ Отказ прохода — не пустая очередь: недоступная база не повод спать дольше.
            return 0;
        }
    }

    /// <summary>Подождать письма или срока: <c>true</c> — разбудил сигнал, <c>false</c> — срок, <c>null</c> — процесс останавливают.</summary>
    private async Task<bool?> ЖдатьAsync(TimeSpan сколько, CancellationToken stoppingToken)
    {
        try
        {
            return await wakeup.WaitAsync(сколько, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
