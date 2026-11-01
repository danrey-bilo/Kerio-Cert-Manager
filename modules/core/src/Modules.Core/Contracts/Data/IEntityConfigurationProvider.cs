using Microsoft.EntityFrameworkCore;

namespace Modules.Core.Contracts.Data;

/// <summary>Модуль приносит конфигурации своих сущностей в общую модель.</summary>
public interface IEntityConfigurationProvider
{
    /// <summary>Описать свои сущности в общей модели.</summary>
    void Configure(ModelBuilder modelBuilder);
}
