namespace KerioCertManager.Api.Infrastructure;

/// <summary>Обернуть последнюю регистрацию службы, сохранив её время жизни.</summary>
internal static class ServiceDecoration
{
    public static IServiceCollection Decorate<T>(this IServiceCollection services, Func<T, IServiceProvider, T> decorate)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(decorate);

        var прежняя = services.LastOrDefault(служба => служба.ServiceType == typeof(T) && !служба.IsKeyedService)
            ?? throw new InvalidOperationException($"Обернуть нечего: {typeof(T).Name} не зарегистрирован.");

        services.Remove(прежняя);
        services.Add(ServiceDescriptor.Describe(
            typeof(T), sp => decorate((T)Создать(sp, прежняя), sp), прежняя.Lifetime));

        return services;
    }

    private static object Создать(IServiceProvider sp, ServiceDescriptor прежняя) =>
        прежняя.ImplementationInstance
        ?? прежняя.ImplementationFactory?.Invoke(sp)
        ?? ActivatorUtilities.CreateInstance(sp, прежняя.ImplementationType!);
}
