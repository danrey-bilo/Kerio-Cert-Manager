namespace Modules.Identity.Passwords;

/// <summary>Параметры argon2id: во что обходится один хеш.</summary>
internal sealed record Argon2Profile(
    int MemoryKib,
    int Iterations,
    int Parallelism,
    int SaltBytes = 16,
    int HashBytes = 32)
{
    /// <summary>Действующие параметры.</summary>
    public static Argon2Profile Current { get; } = new(MemoryKib: 65_536, Iterations: 3, Parallelism: 4);

    /// <summary>Дешёвый профиль — <b>только для тестов</b>, которым нужен сам факт хеширования.</summary>
    public static Argon2Profile Cheap { get; } = new(MemoryKib: 64, Iterations: 1, Parallelism: 1);

    /// <summary>Проверить, что числа вообще осмысленны.</summary>
    public void Validate()
    {
        if (Parallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism), Parallelism, "Нужна хотя бы одна полоса");
        }

        if (Iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Iterations), Iterations, "Нужен хотя бы один проход");
        }

        if (MemoryKib < 8 * Parallelism)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MemoryKib), MemoryKib, $"Памяти должно быть не меньше 8×p = {8 * Parallelism} КиБ");
        }

        if (SaltBytes < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(SaltBytes), SaltBytes, "Соль короче восьми байт бессмысленна");
        }

        if (HashBytes < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(HashBytes), HashBytes, "Хеш короче шестнадцати байт бессмысленен");
        }

        // ⚠ ⚠ Верхние границы — не педантизм, а защита процесса от данных из базы.
        if (MemoryKib > MaxMemoryKib)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MemoryKib), MemoryKib, $"Памяти больше {MaxMemoryKib} КиБ (4 ГиБ) не бывает у честной строки");
        }

        if (Iterations > MaxIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Iterations), Iterations, $"Проходов больше {MaxIterations} не бывает у честной строки");
        }

        if (Parallelism > MaxParallelism)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Parallelism), Parallelism, $"Полос больше {MaxParallelism} не бывает у честной строки");
        }
    }

    /// <summary>Потолок памяти: 4 ГиБ.</summary>
    public const int MaxMemoryKib = 4 * 1024 * 1024;

    /// <summary>Потолок проходов.</summary>
    public const int MaxIterations = 64;

    /// <summary>Потолок полос.</summary>
    public const int MaxParallelism = 64;

    /// <summary>Слабее ли этот профиль, чем <paramref name="other"/>.</summary>
    public bool IsWeakerThan(Argon2Profile other) =>
        MemoryKib < other.MemoryKib
        || Iterations < other.Iterations
        || Parallelism < other.Parallelism
        || HashBytes < other.HashBytes
        || SaltBytes < other.SaltBytes;
}
