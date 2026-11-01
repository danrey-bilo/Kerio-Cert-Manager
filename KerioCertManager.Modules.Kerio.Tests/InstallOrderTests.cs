using KerioCertManager.Modules.Kerio.Sync;

namespace KerioCertManager.Modules.Kerio.Tests;

/// <summary>Порядок очереди установки: нажатые первыми, затем по риску.</summary>
public sealed class InstallOrderTests
{
    private static readonly DateTimeOffset Сейчас = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Нажатые_первыми_по_времени_нажатия()
    {
        var очередь = InstallOrder.Sort(
            [
                new InstallCandidate("expired.example.com", false, null, Сейчас.AddDays(-3)),
                new InstallCandidate("second.example.com", false, Сейчас.AddMinutes(2), Сейчас.AddDays(60)),
                new InstallCandidate("first.example.com", false, Сейчас.AddMinutes(1), Сейчас.AddDays(80)),
            ],
            Сейчас);

        очередь.Select(к => к.CommonName).ShouldBe(["first.example.com", "second.example.com", "expired.example.com"]);
    }

    [Fact]
    public void Затем_истёкший_недостающий_истекающий()
    {
        var очередь = InstallOrder.Sort(
            [
                new InstallCandidate("later.example.com", false, null, Сейчас.AddDays(20)),
                new InstallCandidate("missing.example.com", false, null, null),
                new InstallCandidate("soon.example.com", false, null, Сейчас.AddDays(5)),
                new InstallCandidate("expired.example.com", false, null, Сейчас.AddDays(-1)),
            ],
            Сейчас);

        очередь.Select(к => к.CommonName)
            .ShouldBe(["expired.example.com", "missing.example.com", "soon.example.com", "later.example.com"]);
    }

    [Fact]
    public void При_равенстве_сертификат_по_умолчанию_затем_по_имени()
    {
        var до = Сейчас.AddDays(10);
        var очередь = InstallOrder.Sort(
            [
                new InstallCandidate("b.example.com", false, null, до),
                new InstallCandidate("mail.example.com", true, null, до),
                new InstallCandidate("A.example.com", false, null, до),
            ],
            Сейчас);

        очередь.Select(к => к.CommonName).ShouldBe(["mail.example.com", "A.example.com", "b.example.com"]);
    }
}
