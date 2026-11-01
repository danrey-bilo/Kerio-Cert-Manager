using System.Collections.Concurrent;

namespace Modules.Tokens.Tokens;

/// <summary>Когда сообщать, что предъявлен истёкший ключ интеграции: раз в сутки на ключ.</summary>
internal sealed class ExpiredKeyNotices(TimeProvider clock)
{
    /// <summary>Как часто сообщать об одном и том же ключе.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromDays(1);

    /// <summary>Сколько ключей помнить, прежде чем убрать давние отметки.</summary>
    private const int Предел = 10_000;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _сообщено = new();

    /// <summary>Сообщить ли о ключе сейчас.</summary>
    public bool ПораСообщить(Guid tokenId)
    {
        var сейчас = clock.GetUtcNow();

        if (_сообщено.Count > Предел)
        {
            foreach (var (ключ, когда) in _сообщено)
            {
                if (сейчас - когда >= Window)
                {
                    _сообщено.TryRemove(ключ, out _);
                }
            }
        }

        var было = _сообщено.GetOrAdd(tokenId, DateTimeOffset.MinValue);

        return сейчас - было >= Window && _сообщено.TryUpdate(tokenId, сейчас, было);
    }
}
