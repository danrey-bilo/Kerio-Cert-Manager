using Microsoft.AspNetCore.Http;

namespace Modules.Core.Composition;

/// <summary>Дорогой путь модуля и класс его предела частоты.</summary>
public sealed record RateLimitRule(string Module, string Class, string Paths, Func<PathString, bool> Matches);

/// <summary>Классы пределов частоты: что делает дорогой путь.</summary>
public static class RateLimitClasses
{
    /// <summary>Вход паролем или кодом: перебор пароля чужой учётной записи.</summary>
    public const string SignIn = "sign-in";

    /// <summary>Переспрос личности внутри сессии: перебор пароля или кода угнанной сессией.</summary>
    public const string Reauthentication = "reauthentication";

    /// <summary>Погашение одноразовой ссылки — активации, сброса: перебор секрета ссылки.</summary>
    public const string OneTimeLink = "one-time-link";

    /// <summary>Открытие ссылки доступа без входа: перебор пароля ссылки.</summary>
    public const string PublicLink = "public-link";

    /// <summary>Выгрузка и загрузка файлом: тяжёлая работа на один запрос.</summary>
    public const string BulkTransfer = "bulk-transfer";

    /// <summary>Создание ссылки доступа: каждая — снимок секрета наружу.</summary>
    public const string LinkCreate = "link-create";

    /// <summary>Показ, копия, запуск, подстановка секрета: вынос секретов.</summary>
    public const string SecretReveal = "secret-reveal";

    /// <summary>Поиск секретов по сайту: перебор того, что хранится.</summary>
    public const string SecretLookup = "secret-lookup";

    /// <summary>Синхронизация машины: рамка зеркала и страницы изменений — каждая пишет строку журнала и запечатывает ключи (решение 0118).</summary>
    public const string Sync = "sync";

    /// <summary>Все классы.</summary>
    public static readonly IReadOnlyList<string> All =
        [SignIn, Reauthentication, OneTimeLink, PublicLink, BulkTransfer, LinkCreate, SecretReveal, SecretLookup, Sync];
}

/// <summary>Тот, кто ограничивает частоту обращений к путям модулей.</summary>
public interface IRateLimitEnforcer
{
    /// <summary>Ограничивает ли этот класс.</summary>
    bool Enforces(string rateLimitClass);
}
