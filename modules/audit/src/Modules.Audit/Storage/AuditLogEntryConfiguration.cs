using Modules.Audit.Domain;
using Modules.Core.Contracts.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Audit.Storage;

/// <summary>Модуль описывает свою таблицу в общей модели.</summary>
internal sealed class AuditLogEntryConfiguration : IEntityConfigurationProvider, IEntityTypeConfiguration<AuditLogEntry>
{
    /// <summary>Имя таблицы.</summary>
    public const string TableName = "audit_log";

    public void Configure(ModelBuilder modelBuilder) => modelBuilder.ApplyConfiguration(this);

    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable(TableName);

        builder.HasKey(entry => new { entry.Id, entry.Ts });

        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedOnAdd();

        builder.Property(entry => entry.Ts).HasColumnName("ts").HasColumnType("timestamptz");

        builder.Property(entry => entry.ActorId).HasColumnName("actor_id");
        builder.Property(entry => entry.ActorType).HasColumnName("actor_type").HasColumnType("text");
        builder.Property(entry => entry.ActorIp).HasColumnName("actor_ip").HasColumnType("inet");
        builder.Property(entry => entry.UserAgent).HasColumnName("user_agent").HasColumnType("text");
        builder.Property(entry => entry.Permission).HasColumnName("permission").HasColumnType("text");
        builder.Property(entry => entry.Action).HasColumnName("action").HasColumnType("text").IsRequired();
        builder.Property(entry => entry.ResourceType).HasColumnName("resource_type").HasColumnType("text");
        builder.Property(entry => entry.ResourceId).HasColumnName("resource_id");
        builder.Property(entry => entry.Result).HasColumnName("result").HasColumnType("text");
        builder.Property(entry => entry.Reason).HasColumnName("reason").HasColumnType("text");
        builder.Property(entry => entry.ChangesJson).HasColumnName("changes").HasColumnType("jsonb");
        builder.Property(entry => entry.RequestId).HasColumnName("request_id").HasColumnType("text");
        builder.Property(entry => entry.SessionId).HasColumnName("session_id");
        builder.Property(entry => entry.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");

        builder.HasIndex(entry => new { entry.Ts, entry.Id })
            .HasDatabaseName("ix_audit_log_ts_id")
            .IsDescending(true, true);
        builder.HasIndex(entry => new { entry.ActorId, entry.Ts, entry.Id })
            .HasDatabaseName("ix_audit_log_actor_ts")
            .IsDescending(false, true, true);
        builder.HasIndex(entry => new { entry.ResourceType, entry.ResourceId, entry.Ts })
            .HasDatabaseName("ix_audit_log_resource_ts")
            .IsDescending(false, false, true);
        builder.HasIndex(entry => new { entry.Result, entry.Ts })
            .HasDatabaseName("ix_audit_log_result_ts")
            .IsDescending(false, true);

        // ⚠ Три индекса разбора инцидента (этап 23.4).
        builder.HasIndex(entry => new { entry.SessionId, entry.Ts })
            .HasDatabaseName("ix_audit_log_session_ts")
            .IsDescending(false, true)
            .HasFilter("session_id IS NOT NULL");
        builder.HasIndex(entry => new { entry.RequestId, entry.Ts })
            .HasDatabaseName("ix_audit_log_request_ts")
            .IsDescending(false, true)
            .HasFilter("request_id IS NOT NULL");
        builder.HasIndex(entry => new { entry.ActorIp, entry.Ts })
            .HasDatabaseName("ix_audit_log_ip_ts")
            .IsDescending(false, true)
            .HasFilter("actor_ip IS NOT NULL");
    }
}
