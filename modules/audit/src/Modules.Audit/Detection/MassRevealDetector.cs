using System.Globalization;
using Modules.Audit.Contracts;
using Modules.Audit.Settings;
using Modules.Audit.Writing;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Modules.Audit.Detection;

/// <summary>Кто-то раскрывает секреты пачками.</summary>
[Modules.Core.Features.Feature("audit.mass_reveal")]
internal sealed class MassRevealDetector(
    IConnectionMultiplexer? redis,
    IDbContextFactory<AppDbContext> contexts,
    ISettingsReader settings,
    ChangesRedactor redactor,
    IAuditContextAccessor context,
    IEnumerable<IAuditAlertSink> sinks,
    TimeProvider clock,
    ILogger<MassRevealDetector> log) : ISecretAccessMonitor
{
    /// <summary>Считает окно, добавляет события и решает, стрелять ли — одним неделимым шагом.</summary>
    private const string Script =
        """
        local now = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local weight = function(member) return tonumber(string.match(member, ':(%d+)$')) or 0 end
        local total = redis.call('GET', KEYS[3])
        if total then
            total = tonumber(total)
            for _, member in ipairs(redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', now - window)) do
                total = total - weight(member)
            end
        end
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - window)
        if not total or total < 0 then
            total = 0
            for _, member in ipairs(redis.call('ZRANGE', KEYS[1], 0, -1)) do
                total = total + weight(member)
            end
        end
        redis.call('ZADD', KEYS[1], now, ARGV[4] .. ':' .. ARGV[5])
        total = total + tonumber(ARGV[5])
        redis.call('PEXPIRE', KEYS[1], window)
        redis.call('SET', KEYS[3], total, 'PX', window)
        local count = total
        if count < tonumber(ARGV[3]) then
            return {0, count}
        end
        if redis.call('SET', KEYS[2], '1', 'NX', 'PX', window) then
            return {1, count}
        end
        return {0, count}
        """;

    public async Task<bool> NoteAsync(
        Guid actorId, int events = 1, CancellationToken cancellationToken = default)
    {
        if (redis is null || events <= 0)
        {
            return false;
        }

        // ⚠ Соединения нет — не ждём таймаута на каждом показе (решение 0095): ответ «детектор недоступен» известен сразу — так же поступает кэш прав.
        if (!redis.IsConnected)
        {
            log.LogWarning("mass_reveal_detector_unavailable {Error}", "not_connected");

            return false;
        }

        var threshold = Math.Max(2, await settings.GetAsync(AuditSettings.MassRevealThreshold, cancellationToken));
        var window = TimeSpan.FromSeconds(
            Math.Max(60, await settings.GetAsync(AuditSettings.MassRevealWindowSeconds, cancellationToken)));

        long fired;
        long count;

        try
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
                Script,
                [
                    $"detect:mass_reveal:{actorId:N}:calls",
                    $"detect:mass_reveal:{actorId:N}:fired",
                    $"detect:mass_reveal:{actorId:N}:count",
                ],
                [
                    now,
                    (long)window.TotalMilliseconds,
                    threshold,
                    Guid.CreateVersion7().ToString("N"),
                    events,
                ]))!;

            fired = (long)result[0];
            count = (long)result[1];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(
                "mass_reveal_detector_unavailable {Error}", SafeError.Describe(error));

            return false;
        }

        if (fired == 0)
        {
            return false;
        }

        // ⚠ ⚠ Решено стрелять — дальше запрос тревоге не хозяин (ревизия 2026-10-26, решение 0118).
        using var срок = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        return await RaiseAsync(actorId, (int)count, threshold, window, срок.Token);
    }

    /// <summary>Записать тревогу и разослать её подписчикам.</summary>
    private async Task<bool> RaiseAsync(
        Guid actorId, int count, int threshold, TimeSpan window, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, object?>
        {
            ["count"] = count,
            ["threshold"] = threshold,
            ["window_seconds"] = (int)window.TotalSeconds,
        };

        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);

            db.Add(AuditRow.From(
                new AuditEntry(
                    AuditActions.MassReveal,
                    ActorId: actorId,
                    Reason: "threshold_exceeded",
                    Changes: details),
                context.Current,
                clock.GetUtcNow(),
                redactor));

            // ⚠ С пределом ожидания головы цепи (решение 0110): разбор у OwnConnection.
            await Writing.OwnConnection.SaveAsync(db, cancellationToken);
        }
        catch (Exception error)
        {
            log.LogError("mass_reveal_not_recorded {Error} {Actor}", SafeError.Describe(error), actorId);

            await ForgetAsync(actorId);

            return false;
        }

        log.LogWarning(
            "mass_reveal_detected {Actor} {Count} {Threshold} {WindowSeconds}",
            actorId, count, threshold, (int)window.TotalSeconds);

        await NotifyAsync(
            new AuditAlert(
                "mass_reveal",
                actorId,
                $"Раскрыто секретов: {count.ToString(CultureInfo.InvariantCulture)} "
                + $"за {((int)window.TotalSeconds).ToString(CultureInfo.InvariantCulture)} с",
                details),
            cancellationToken);

        return true;
    }

    /// <summary>Разослать тревогу подписчикам.</summary>
    private async Task NotifyAsync(AuditAlert alert, CancellationToken cancellationToken)
    {
        foreach (var sink in sinks)
        {
            try
            {
                await sink.RaiseAsync(alert, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.LogError(
                    "audit_alert_sink_failed {Sink} {Error}", sink.GetType().Name, SafeError.Describe(error));
            }
        }
    }

    private async Task ForgetAsync(Guid actorId)
    {
        try
        {
            await redis!.GetDatabase().KeyDeleteAsync($"detect:mass_reveal:{actorId:N}:fired");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning("mass_reveal_flag_stuck {Error}", SafeError.Describe(error));
        }
    }
}
