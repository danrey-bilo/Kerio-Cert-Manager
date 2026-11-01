namespace Modules.Crypto;

/// <summary>Что сказала проверка подписи значения, пришедшего из недоверенного хранилища.</summary>
public enum IntegrityVerdict
{
    /// <summary>Подпись сошлась.</summary>
    Ok,

    /// <summary>Значение подделано: разобрать не удалось либо подпись не сошлась при <b>известном</b> ключе.</summary>
    Tampered,

    /// <summary>Подписано ключом, которого в ринге нет.</summary>
    Foreign,
}

/// <summary>Подпись значений, живущих <b>вне процесса</b>: кэшей, счётчиков, зеркал, очередей.</summary>
public interface IIntegrityProtector
{
    /// <summary>Подписать значение перед отправкой наружу.</summary>
    string Protect(string purpose, string recordKey, string value);

    /// <summary>Проверить конверт и достать значение.</summary>
    IntegrityVerdict TryUnprotect(string purpose, string recordKey, string envelope, out string? value);

    /// <summary>Отпечаток значения: равные значения дают равные отпечатки, а по отпечатку значение не восстановить и не подобрать без ключа установки.</summary>
    string Fingerprint(string purpose, string value);
}
