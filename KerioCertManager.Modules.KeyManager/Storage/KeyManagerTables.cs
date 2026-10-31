using KerioCertManager.Modules.KeyManager.Domain;
using Modules.Core.Contracts.Data;
using Microsoft.EntityFrameworkCore;

namespace KerioCertManager.Modules.KeyManager.Storage;

/// <summary>Таблицы модуля в общей модели; внешних ключей на чужие таблицы (<c>users</c>) нет.</summary>
internal sealed class KeyManagerTables : IEntityConfigurationProvider
{
    internal const string StateTable = "keymanager_state";

    internal const string CertificatesTable = "keymanager_certificates";

    public void Configure(ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Entity<KeyManagerState>(строка =>
        {
            строка.ToTable(StateTable, таблица => таблица.HasCheckConstraint(
                $"ck_{StateTable}_singleton", $"id = {KeyManagerState.SingletonId}"));

            строка.HasKey(с => с.Id).HasName($"pk_{StateTable}");
            строка.Property(с => с.Id).HasColumnName("id").ValueGeneratedNever();
            строка.Property(с => с.ApiKey).HasColumnName("api_key").HasColumnType("bytea");
            строка.Property(с => с.DataKey).HasColumnName("data_key").HasColumnType("bytea");
            строка.Property(с => с.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            строка.Property(с => с.UpdatedBy).HasColumnName("updated_by");
            строка.Property(с => с.LastPollAt).HasColumnName("last_poll_at").HasColumnType("timestamptz");
            строка.Property(с => с.LastPollError).HasColumnName("last_poll_error").HasColumnType("text");
            строка.Property(с => с.LastPollCount).HasColumnName("last_poll_count");
        });

        model.Entity<KnownCertificate>(строка =>
        {
            строка.ToTable(CertificatesTable);

            строка.HasKey(с => new { с.CommonName, с.SerialNumber }).HasName($"pk_{CertificatesTable}");
            строка.HasIndex(с => с.LastSeenAt).HasDatabaseName($"ix_{CertificatesTable}_last_seen");

            строка.Property(с => с.CommonName).HasColumnName("common_name").HasColumnType("text").IsRequired();
            строка.Property(с => с.SerialNumber).HasColumnName("serial_number").HasColumnType("text").IsRequired();
            строка.Property(с => с.ExpiresOn).HasColumnName("expires_on").HasColumnType("date");

            строка.Property(с => с.FirstSeenAt)
                .HasColumnName("first_seen_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");

            строка.Property(с => с.LastSeenAt)
                .HasColumnName("last_seen_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");
        });
    }
}
