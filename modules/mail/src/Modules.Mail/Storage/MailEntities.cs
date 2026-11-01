using Modules.Mail.Domain;
using Modules.Core.Contracts.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Mail.Storage;

/// <summary>Две таблицы почты в общей модели.</summary>
internal sealed class MailEntities : IEntityConfigurationProvider
{
    /// <summary>Таблица очереди писем.</summary>
    internal const string OutboxTable = "mail_outbox";

    /// <summary>Таблица секретов установки.</summary>
    internal const string SecretsTable = "system_secrets";

    public void Configure(ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Entity<MailOutbox>(строка =>
        {
            строка.ToTable(OutboxTable);

            строка.HasKey(п => п.Id).HasName($"pk_{OutboxTable}");

            строка.Property(п => п.Id).HasColumnName("id").ValueGeneratedNever();

            строка.Property(п => п.ToAddress)
                .HasColumnName("to_address").HasColumnType("text").IsRequired();

            строка.Property(п => п.Subject)
                .HasColumnName("subject").HasColumnType("text").IsRequired();

            строка.Property(п => п.SealedBody)
                .HasColumnName("body").HasColumnType("bytea").IsRequired();

            строка.Property(п => п.DataKey)
                .HasColumnName("data_key").HasColumnType("bytea").IsRequired();

            строка.Property(п => п.SealedHtml)
                .HasColumnName("html_body").HasColumnType("bytea");

            // ⚠ Обнуляемая (решение 0120): у письма без вложений колонки нет, у стёртого — пустое значение, а не NULL.
            строка.Property(п => п.SealedAttachments)
                .HasColumnName("attachments").HasColumnType("bytea");

            строка.Property(п => п.Kind)
                .HasColumnName("kind").HasColumnType("text").IsRequired();

            строка.Property(п => п.CreatedAt)
                .HasColumnName("created_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");

            строка.Property(п => п.NextAttemptAt)
                .HasColumnName("next_attempt_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");

            строка.Property(п => п.Attempts)
                .HasColumnName("attempts").IsRequired().HasDefaultValue(0);

            строка.Property(п => п.SentAt).HasColumnName("sent_at").HasColumnType("timestamptz");
            строка.Property(п => п.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");

            строка.Property(п => п.LastError).HasColumnName("last_error").HasColumnType("text");

            // ⚠ Тот самый частичный индекс.
            строка.HasIndex(п => п.NextAttemptAt)
                .HasDatabaseName($"ix_{OutboxTable}_pending")
                .HasFilter("sent_at IS NULL");

            строка.HasIndex(п => new { п.ToAddress, п.Kind, п.CreatedAt })
                .HasDatabaseName($"ix_{OutboxTable}_rate");
        });

        model.Entity<SystemSecret>(строка =>
        {
            строка.ToTable(SecretsTable);

            строка.HasKey(с => с.Key).HasName($"pk_{SecretsTable}");

            строка.Property(с => с.Key).HasColumnName("key").HasColumnType("text").IsRequired();

            строка.Property(с => с.Sealed)
                .HasColumnName("sealed").HasColumnType("bytea").IsRequired();

            строка.Property(с => с.DataKey)
                .HasColumnName("data_key").HasColumnType("bytea").IsRequired();

            строка.Property(с => с.UpdatedAt)
                .HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");

            // ⚠ Без внешнего ключа: `users` — чужая таблица (правило 1).
            строка.Property(с => с.UpdatedBy).HasColumnName("updated_by");
        });
    }
}
