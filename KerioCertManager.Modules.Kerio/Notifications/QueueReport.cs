using System.Globalization;
using KerioCertManager.Modules.Kerio.Domain;
using KerioCertManager.Modules.Kerio.Sync;

namespace KerioCertManager.Modules.Kerio.Notifications;

/// <summary>Сводное письмо об очереди установки: сколько встало, сколько нет и итог по каждому имени.</summary>
internal static class QueueReport
{
    /// <summary>Установка из журнала: имя, исход, срок нового сертификата, причина отказа.</summary>
    internal sealed record Entry(string CommonName, string Outcome, DateTimeOffset? NotAfter, string? Error);

    /// <summary>Строка таблицы письма: имя, итог, подробности.</summary>
    internal sealed record Row(string CommonName, string Outcome, string Details);

    internal sealed record Summary(int Installed, int Failed, string Title, string Message, IReadOnlyList<Row> Rows, AlertLevel Level);

    /// <summary>
    /// По последнему исходу каждого имени: повтор после отказа в той же очереди заменяет отказ. Сначала неудачные, затем
    /// удачные — в порядке установки. <c>null</c> — сводить нечего (только пробные прогоны или ничего).
    /// </summary>
    public static Summary? Build(string serverName, IEnumerable<Entry> entries)
    {
        var последние = entries
            .Where(у => у.Outcome is DeployOutcomes.Succeeded or DeployOutcomes.Failed or DeployOutcomes.RolledBack)
            .GroupBy(у => у.CommonName, StringComparer.OrdinalIgnoreCase)
            .Select(г => г.Last())
            .OrderBy(у => у.Outcome == DeployOutcomes.Succeeded)
            .ToList();

        if (последние.Count == 0)
        {
            return null;
        }

        var встало = последние.Count(у => у.Outcome == DeployOutcomes.Succeeded);
        var нет = последние.Count - встало;

        var заголовок = $"Очередь установки на «{serverName}» пройдена: установлено {Число(встало)} из {Число(последние.Count)}";

        return new Summary(
            встало,
            нет,
            нет > 0 ? "🟠 " + заголовок : заголовок,
            $"Очередь установки пройдена. Установлено и подтверждено по TLS: {Число(встало)}. Не удалось: {Число(нет)}.",
            [.. последние.Select(Строка)],
            нет > 0 ? AlertLevel.Warning : AlertLevel.Info);
    }

    private static Row Строка(Entry у) => у.Outcome switch
    {
        DeployOutcomes.Succeeded => new Row(
            у.CommonName,
            "Установлен",
            у.NotAfter is { } до ? $"Действует до {до.ToUniversalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} UTC" : "—"),
        DeployOutcomes.RolledBack => new Row(у.CommonName, "Возвращён прежний", у.Error ?? "—"),
        _ => new Row(у.CommonName, "Не установлен", у.Error ?? "—"),
    };

    private static string Число(int n) => n.ToString(CultureInfo.InvariantCulture);
}
