namespace Modules.Core.Contracts.Realtime;

/// <summary>Кадр реального времени: что произошло и кому это можно видеть.</summary>
public sealed record RealtimeFrame
{
    /// <summary>Сколько знаков помещается в одно значение тела.</summary>
    public const int MaxValueLength = 256;

    /// <summary>Сколько знаков помещается в тело кадра целиком.</summary>
    public const int MaxPayloadLength = 2048;

    /// <summary>Собрать кадр, проверив, что он остался указателем.</summary>
    public RealtimeFrame(
        string Type,
        string? RequiredPermission,
        IReadOnlyDictionary<string, object?> Payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Type);
        ArgumentNullException.ThrowIfNull(Payload);

        var всего = 0;

        foreach (var (ключ, значение) in Payload)
        {
            // ⚠ ⚠ Длина — сериализованного значения, а не `ToString()` (решение 0110, разбор 2026-10-21, находка 24).
            var длина = значение switch
            {
                null => 0,
                string строка => строка.Length,
                _ => System.Text.Json.JsonSerializer.Serialize(значение, значение.GetType()).Length,
            };

            if (длина > MaxValueLength)
            {
                throw new ArgumentException(
                    $"Значение «{ключ}» длиной {длина} не помещается в кадр: предел "
                    + $"{MaxValueLength}. Кадр — указатель, а не содержимое; содержимое клиент "
                    + "читает обычным запросом, где на нём висит проверка права.",
                    nameof(Payload));
            }

            всего += ключ.Length + длина;
        }

        if (всего > MaxPayloadLength)
        {
            throw new ArgumentException(
                $"Тело кадра длиной {всего} не помещается: предел {MaxPayloadLength}.",
                nameof(Payload));
        }

        this.Type = Type;
        this.RequiredPermission = RequiredPermission;
        this.Payload = Payload;
    }

    /// <summary>Тип события — <c>message.created</c>, <c>notification.created</c> и так далее.</summary>
    public string Type { get; }

    /// <summary>Право, без которого кадр не отдаётся.</summary>
    public string? RequiredPermission { get; }

    /// <summary>Тело кадра — указатель, не содержимое.</summary>
    public IReadOnlyDictionary<string, object?> Payload { get; }
}

/// <summary>Отправка кадров подключённым клиентам.</summary>
public interface IRealtimePublisher
{
    /// <summary>Отправить кадр перечисленным получателям.</summary>
    Task<int> PublishAsync(
        IReadOnlyCollection<Guid> recipients,
        RealtimeFrame frame,
        CancellationToken cancellationToken = default);
}
