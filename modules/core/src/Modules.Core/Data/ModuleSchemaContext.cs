using Modules.Core.Contracts.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Core.Data;

/// <summary>Контекст схемы модуля — только для его миграций (решение 0114).</summary>
public abstract class ModuleSchemaContext(DbContextOptions options, IEnumerable<IEntityConfigurationProvider> configurations)
    : DbContext(options)
{
    private readonly IReadOnlyList<IEntityConfigurationProvider> _configurations = [.. configurations];

    /// <inheritdoc />
    protected sealed override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        ModelConventions.Apply(configurationBuilder);

    /// <inheritdoc />
    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var configuration in _configurations)
        {
            configuration.Configure(modelBuilder);
        }
    }
}
