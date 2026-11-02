using KerioCertManager.Modules.Kerio.Sync;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Расписание проходов: опрос KMP идёт с настроенным интервалом, очередь — с паузой на перезапуск HTTPS.</summary>
public sealed class SyncWorkerTests
{
    private static readonly TimeSpan Интервал = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan Пауза = TimeSpan.FromSeconds(45);

    /// <summary>⚠ Ожидание до срока само ведёт к опросу: иначе он сдвигался на целый интервал, и проверки шли вдвое реже.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    public void Без_очереди_опрос_ровно_через_интервал(int минут) =>
        SyncWorker.СледующийПроход(false, TimeSpan.FromMinutes(минут), TimeSpan.Zero).ShouldBe((TimeSpan.FromMinutes(минут), true));

    [Fact]
    public void Очередь_идёт_с_паузой_и_без_опроса() =>
        SyncWorker.СледующийПроход(true, Интервал, TimeSpan.Zero).ShouldBe((Пауза, false));

    /// <summary>Очередь опустела — срок опроса прежний, а не интервал с этого момента.</summary>
    [Fact]
    public void Очередь_опустела_ждём_только_остаток() =>
        SyncWorker.СледующийПроход(false, Интервал, TimeSpan.FromMinutes(5)).ShouldBe((TimeSpan.FromMinutes(10), true));

    /// <summary>⚠ Срок опроса уже подошёл, но Kerio могла только что принять сертификат: пауза важнее.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Срок_ближе_паузы_опрос_после_паузы(bool очередь)
    {
        SyncWorker.СледующийПроход(очередь, Интервал, Интервал - TimeSpan.FromSeconds(10)).ShouldBe((Пауза, true));
        SyncWorker.СледующийПроход(очередь, Интервал, Интервал + TimeSpan.FromMinutes(1)).ShouldBe((Пауза, true));
    }
}
