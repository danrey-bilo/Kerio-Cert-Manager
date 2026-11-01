namespace Modules.Crypto;

/// <summary>Сколько обёрток пересмотрено, сколько переложено и откуда продолжать.</summary>
public readonly record struct RewrapBatch(int Seen, int Rewrapped, string? Next, int Unreadable = 0);

/// <summary>Модуль перекладывает своё запечатанное под действующую версию мастер-ключа.</summary>
public interface IRewrapTarget
{
    /// <summary>Что перекладываем — для строки в журнале и в логе.</summary>
    string Name { get; }

    /// <summary>Переложить очередную порцию.</summary>
    Task<RewrapBatch> RewrapAsync(
        int batch, string? after = null, CancellationToken cancellationToken = default);
}
