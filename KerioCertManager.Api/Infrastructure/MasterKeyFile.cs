using Modules.Crypto;
using Modules.Core.Errors;

namespace KerioCertManager.Api.Infrastructure;

/// <summary>Откуда установка берёт мастер-ключ.</summary>
internal sealed record MasterKeySource(MasterKeyRing Ring, string Origin, bool FromEnvironment)
{
    /// <summary>Сказать в лог, чем работаем, без ключей.</summary>
    public void Announce(ILogger logger)
    {
        if (FromEnvironment)
        {
            // ⚠ Переменная видна в `docker inspect` — об этом предупреждаем вслух.
            logger.LogWarning(
                "master_key_from_environment {Origin} {Versions}",
                Origin, Ring.Count);

            return;
        }

        logger.LogInformation(
            "master_key_loaded {Origin} {Versions} {Current}",
            Origin, Ring.Count, Ring.CurrentVersion);
    }
}

/// <summary>Чтение мастер-ключа: файл главнее переменной, без ключа не стартуем.</summary>
internal static class MasterKeyFile
{
    /// <summary>Путь к файлу секрета.</summary>
    public const string FileKey = "KCM_MASTER_KEK_FILE";

    /// <summary>Ключ прямо в переменной.</summary>
    public const string InlineKey = "KCM_MASTER_KEK";

    /// <summary>Разрешает опубликованный ключ демо-стенда.</summary>
    public const string DemoMode = "KCM_DEMO_MODE";

    /// <summary>Ключ из docker-compose.demo.yml — известен всем.</summary>
    internal const string DemoKey = "1:ZGVtby1rZWstbm90LWZvci1wcm9kdWN0aW9uLXVzZSE=";

    /// <summary>Прочитать и разобрать мастер-ключ.</summary>
    public static MasterKeySource Load(IConfiguration configuration)
    {
        var (content, origin, fromEnvironment) = Content(configuration);

        if (content.Trim() == DemoKey && !string.Equals(configuration[DemoMode], "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Мастер-ключ — опубликованный ключ демо-стенда: им расшифровал бы ваши секреты кто угодно. "
                + $"Создайте свой: ./scripts/gen-secrets.sh. Демо-стенд — только с {DemoMode}=true.");
        }

        return new MasterKeySource(MasterKeyRing.Parse(content, Hint), origin, fromEnvironment);
    }

    private const string Hint = $"{FileKey} (создать: ./scripts/gen-secrets.sh)";

    /// <summary>Содержимое источника как есть — для отметки разового сброса доступа.</summary>
    internal static (string Content, string Origin, bool FromEnvironment) Content(IConfiguration configuration)
    {
        var path = configuration[FileKey];

        if (!string.IsNullOrWhiteSpace(path))
        {
            return (Read(path), path, false);
        }

        var inline = configuration[InlineKey];

        if (!string.IsNullOrWhiteSpace(inline))
        {
            return (inline, InlineKey, true);
        }

        throw new InvalidOperationException(
            $"Не задан мастер-ключ. Без него нечем расшифровать ни один секрет, поэтому "
            + $"запуск прерван. Задайте {FileKey} — путь к файлу секрета (в бою так), либо "
            + $"{InlineKey} — ключ строкой (только разработка: переменная видна в "
            + $"`docker inspect`). Создать файл: ./scripts/gen-secrets.sh");
    }

    private static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Файл мастер-ключа {path} не читается ({SafeError.Describe(error)}). "
                + "В контейнере он приезжает docker-секретом; создать его: ./scripts/gen-secrets.sh");
        }
    }
}
