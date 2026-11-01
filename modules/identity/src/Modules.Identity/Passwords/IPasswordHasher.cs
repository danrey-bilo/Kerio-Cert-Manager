namespace Modules.Identity.Passwords;

/// <summary>Хеширование и проверка паролей.</summary>
internal interface IPasswordHasher
{
    /// <summary>Посчитать хеш нового пароля.</summary>
    Task<string> HashAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>Тот ли это пароль.</summary>
    Task<bool> VerifyAsync(string? encoded, string password, CancellationToken cancellationToken = default);

    /// <summary>Пора ли пересчитать этот хеш нынешними параметрами.</summary>
    bool NeedsRehash(string? encoded);

    /// <summary>Потратить столько же времени, сколько потратила бы проверка.</summary>
    Task WasteVerificationTimeAsync(CancellationToken cancellationToken = default);
}
