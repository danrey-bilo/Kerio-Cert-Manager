namespace Modules.Core.Contracts.Quotas;

/// <summary>Кто платит за это действие: либо названный субъект, либо объект, чьего владельца ещё надо спросить.</summary>
public abstract class QuotaPayer
{
    // ⚠ Закрытый конструктор — единственное, что закрывает иерархию.
    private QuotaPayer()
    {
    }

    /// <summary>Плательщик назван прямо.</summary>
    public static QuotaPayer Subject(QuotaSubject subject) => new BySubject(subject);

    /// <summary>Плательщик выводится из объекта — его назовёт владелец типа ресурса.</summary>
    public static QuotaPayer Resource(string resourceType, Guid resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);

        return new ByResource(resourceType, resourceId);
    }

    /// <summary>Плательщик назван прямо.</summary>
    public sealed class BySubject : QuotaPayer
    {
        internal BySubject(QuotaSubject value) => Value = value;

        /// <summary>Человек или команда.</summary>
        public QuotaSubject Value { get; }

        /// <inheritdoc />
        public override string ToString() => $"{Value.Type}:{Value.Id}";
    }

    /// <summary>Плательщика назовёт владелец типа ресурса.</summary>
    public sealed class ByResource : QuotaPayer
    {
        internal ByResource(string resourceType, Guid resourceId)
        {
            ResourceType = resourceType;
            ResourceId = resourceId;
        }

        /// <summary>Тип объекта: <c>vault</c>, <c>credential</c>.</summary>
        public string ResourceType { get; }

        /// <summary>Идентификатор объекта.</summary>
        public Guid ResourceId { get; }

        /// <inheritdoc />
        public override string ToString() => $"{ResourceType}:{ResourceId}";
    }
}
