using Modules.Settings.Domain;
using Modules.Core.Contracts.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Settings.Storage;

/// <summary>Две таблицы настроек в общей модели.</summary>
internal sealed class SettingsEntities : IEntityConfigurationProvider
{
    /// <summary>Таблица системных значений.</summary>
    internal const string SystemTable = "system_settings";

    /// <summary>Таблица личных предпочтений.</summary>
    internal const string UserTable = "user_preferences";

    /// <summary>Наибольшая длина имени настройки.</summary>
    internal const int KeyLength = 128;

    public void Configure(ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Entity<SystemSetting>(строка =>
        {
            строка.ToTable(SystemTable);

            строка.HasKey(запись => запись.Key).HasName($"pk_{SystemTable}");

            строка.Property(запись => запись.Key)
                .HasColumnName("key").HasMaxLength(KeyLength).IsRequired();

            строка.Property(запись => запись.ValueJson)
                .HasColumnName("value").HasColumnType("jsonb").IsRequired();

            // ⚠ Без внешнего ключа: таблица пользователей принадлежит чужому модулю.
            строка.Property(запись => запись.UpdatedBy).HasColumnName("updated_by");

            // ⚠ Умолчание базы ЕСТЬ, хотя приложение всегда задаёт время само.
            строка.Property(запись => запись.UpdatedAt)
                .HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");
        });

        model.Entity<UserPreference>(строка =>
        {
            строка.ToTable(UserTable);

            // ⚠ Человек ВЕДУЩИЙ в составном ключе: единственный запрос к этой таблице — «все предпочтения вот этого».
            строка.HasKey(запись => new { запись.UserId, запись.Key }).HasName($"pk_{UserTable}");

            строка.Property(запись => запись.UserId).HasColumnName("user_id").IsRequired();

            строка.Property(запись => запись.Key)
                .HasColumnName("key").HasMaxLength(KeyLength).IsRequired();

            строка.Property(запись => запись.ValueJson)
                .HasColumnName("value").HasColumnType("jsonb").IsRequired();

            строка.Property(запись => запись.UpdatedAt)
                .HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");
        });
    }
}
