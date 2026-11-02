using System.Security.Cryptography;

namespace KerioCertManager.Modules.KeyManager.Contracts;

/// <summary>Выпуск сертификата из последнего удачного опроса Key Manager Plus.</summary>
public sealed record SourceCertificate(
    string CommonName,
    string SerialNumber,
    DateOnly? ExpiresOn,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>Итог опроса Key Manager Plus.</summary>
public sealed record SourceRefresh(bool Configured, bool Succeeded, int Certificates, string? Error)
{
    /// <summary>Интеграция выключена или не настроена — опроса не было.</summary>
    public static SourceRefresh NotConfigured { get; } = new(false, false, 0, null);
}

/// <summary>Сертификат с цепочкой и закрытый ключ — ровно то, что нужно для установки на сервер.</summary>
public sealed class CertificateMaterial : IDisposable
{
    private bool _disposed;

    public CertificateMaterial(
        string commonName,
        string serialNumber,
        IReadOnlyList<byte[]> certificates,
        byte[] privateKey,
        string? passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(privateKey);

        CommonName = commonName;
        SerialNumber = serialNumber;
        Certificates = certificates;
        PrivateKey = privateKey;
        Passphrase = passphrase;
    }

    public string CommonName { get; }

    public string SerialNumber { get; }

    /// <summary>Сертификаты в DER.</summary>
    public IReadOnlyList<byte[]> Certificates { get; }

    /// <summary>Файл закрытого ключа как его отдал KMP.</summary>
    public byte[] PrivateKey { get; }

    /// <summary>Пароль закрытого ключа; <c>null</c> — нет.</summary>
    public string? Passphrase { get; }

    /// <summary>Обнулить ключ в памяти.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(PrivateKey);
        _disposed = true;
    }

    /// <summary>Без содержимого: объект может попасть в лог.</summary>
    public override string ToString() =>
        $"{nameof(CertificateMaterial)} {CommonName} #{SerialNumber} ({Certificates.Count} серт., ключ {PrivateKey.Length} байт, "
        + $"пароль {(Passphrase is null ? "нет" : "есть")})";
}

/// <summary>Какой выпуск имени считается текущим — правило одно для всех модулей.</summary>
public static class SourceReleases
{
    /// <summary>Замеченный позже всех; при равенстве — дольше действующий, затем больший серийный.</summary>
    public static SourceCertificate? Pick(IEnumerable<SourceCertificate> releases) =>
        releases
            .OrderByDescending(с => с.FirstSeenAt)
            .ThenByDescending(с => с.ExpiresOn ?? DateOnly.MinValue)
            .ThenByDescending(с => с.SerialNumber, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>Текущие выпуски по Common Name (без учёта регистра).</summary>
    public static IReadOnlyDictionary<string, SourceCertificate> Current(IEnumerable<SourceCertificate> releases) =>
        releases
            .GroupBy(с => с.CommonName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(г => г.Key, г => Pick(г)!, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Отказ источника сертификатов: KMP недоступен, отказал или прислал не то.</summary>
public sealed class CertificateSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Источник сертификатов — ManageEngine Key Manager Plus.</summary>
public interface ICertificateSource
{
    /// <summary>Опросить KMP и обновить снимок; неудача — в итоге, не исключением.</summary>
    Task<SourceRefresh> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Включена и настроена ли интеграция: выключенная — ничего не отдаёт на установку, даже из старого снимка.</summary>
    Task<bool> AvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Все выпуски из последнего удачного опроса.</summary>
    Task<IReadOnlyList<SourceCertificate>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Текущий выпуск Common Name; <c>null</c> — в последнем опросе такого нет.</summary>
    Task<SourceCertificate?> CurrentAsync(string commonName, CancellationToken cancellationToken = default);

    /// <summary>Выгрузить сертификат с цепочкой и закрытый ключ выпуска из снимка.</summary>
    Task<CertificateMaterial> FetchAsync(
        string commonName, string serialNumber, CancellationToken cancellationToken = default);

    /// <summary>Сертификаты выпуска (DER) без закрытого ключа — для сверки отпечатка.</summary>
    Task<IReadOnlyList<byte[]>> FetchCertificatesAsync(
        string commonName, string serialNumber, CancellationToken cancellationToken = default);
}

/// <summary>Сигнал «синхронизировать сейчас»: будит фоновый цикл, не дожидаясь срока.</summary>
public sealed class SourceWakeup : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Разбудить цикл; повторный сигнал до пробуждения ничего не добавляет.</summary>
    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>Ждать сигнала или срока.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _signal.Dispose();
}

/// <summary>Настройки источника, нужные соседям.</summary>
public static class CertificateSourceSettings
{
    /// <summary>Интервал прохода синхронизации в минутах (<c>keymanager.poll_minutes</c>).</summary>
    public static global::Modules.Core.Contracts.Settings.SettingKey<int> IntervalMinutes => Settings.KeyManagerSettings.PollMinutes;
}
