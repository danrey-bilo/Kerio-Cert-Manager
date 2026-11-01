using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Modules.Core.Data;

/// <summary>Ключ кэша модели, учитывающий набор конфигураций модулей.</summary>
public sealed class AppModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);

        var набор = context is AppDbContext наш ? наш.ConfigurationKey : string.Empty;

        return (context.GetType(), набор, designTime);
    }
}
