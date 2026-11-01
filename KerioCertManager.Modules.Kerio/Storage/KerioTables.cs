using System.Text.Json;
using KerioCertManager.Modules.Kerio.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Modules.Core.Contracts.Data;

namespace KerioCertManager.Modules.Kerio.Storage;

/// <summary>Таблицы модуля Kerio в общей модели; внешних ключей на чужие таблицы (<c>users</c>) нет.</summary>
internal sealed class KerioTables : IEntityConfigurationProvider
{
    internal const string ServersTable = "kerio_servers";

    internal const string DeploymentsTable = "kerio_deployments";

    internal const string NotificationsTable = "kerio_notifications";

    internal const string RequestsTable = "kerio_install_requests";

    internal const string SlotTable = "kerio_install_slot";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Entity<KerioServer>(строка =>
        {
            строка.ToTable(ServersTable, таблица =>
            {
                таблица.HasCheckConstraint($"ck_{ServersTable}_name", $"char_length(name) BETWEEN 1 AND {KerioServer.MaxName}");
                таблица.HasCheckConstraint($"ck_{ServersTable}_base_url", "base_url LIKE 'https://%'");
                таблица.HasCheckConstraint($"ck_{ServersTable}_tls_mode", $"tls_mode IN ('{TlsModes.Ca}', '{TlsModes.Pin}')");
                таблица.HasCheckConstraint(
                    $"ck_{ServersTable}_pin", $"tls_mode <> '{TlsModes.Pin}' OR tls_pin_sha256 ~ '^[0-9A-F]{{64}}$'");
                таблица.HasCheckConstraint(
                    $"ck_{ServersTable}_notify", $"cardinality(notify_emails) <= {KerioServer.MaxNotifyEmails}");
                таблица.HasCheckConstraint(
                    $"ck_{ServersTable}_ports", $"cardinality(verify_ports) <= {KerioServer.MaxVerifyPorts}");
                таблица.HasCheckConstraint(
                    $"ck_{ServersTable}_previous", $"cardinality(previous_serials) <= {InstalledHistory.MaxPrevious}");
                таблица.HasCheckConstraint(
                    $"ck_{ServersTable}_secret", "(password IS NULL) = (data_key IS NULL)");
            });

            строка.HasKey(с => с.Id).HasName($"pk_{ServersTable}");
            строка.HasIndex(с => с.Name).IsUnique().HasDatabaseName($"ux_{ServersTable}_name");

            строка.Property(с => с.Id).HasColumnName("id").ValueGeneratedNever();
            строка.Property(с => с.Name).HasColumnName("name").HasColumnType("text").IsRequired();
            строка.Property(с => с.BaseUrl).HasColumnName("base_url").HasColumnType("text").IsRequired();
            строка.Property(с => с.Username).HasColumnName("username").HasColumnType("text").IsRequired();
            строка.Property(с => с.Password).HasColumnName("password").HasColumnType("bytea");
            строка.Property(с => с.DataKey).HasColumnName("data_key").HasColumnType("bytea");
            строка.Property(с => с.TlsMode).HasColumnName("tls_mode").HasColumnType("text").IsRequired();
            строка.Property(с => с.TlsServerName).HasColumnName("tls_server_name").HasColumnType("text");
            строка.Property(с => с.TlsPinSha256).HasColumnName("tls_pin_sha256").HasColumnType("text");
            строка.Property(с => с.CommonName).HasColumnName("common_name").HasColumnType("text");
            строка.Property(с => с.AutoDeploy).HasColumnName("auto_deploy").IsRequired();
            строка.Property(с => с.VerifyPorts).HasColumnName("verify_ports").HasColumnType("integer[]").IsRequired();
            строка.Property(с => с.NotifyEmails).HasColumnName("notify_emails").HasColumnType("text[]").IsRequired();
            строка.Property(с => с.CleanupOld).HasColumnName("cleanup_old").IsRequired();
            строка.Property(с => с.Enabled).HasColumnName("enabled").IsRequired();

            строка.Property(с => с.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");
            строка.Property(с => с.CreatedBy).HasColumnName("created_by");
            строка.Property(с => с.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");
            строка.Property(с => с.UpdatedBy).HasColumnName("updated_by");

            строка.Property(с => с.LastCheckAt).HasColumnName("last_check_at").HasColumnType("timestamptz");
            строка.Property(с => с.LastCheckError).HasColumnName("last_check_error").HasColumnType("text");
            строка.Property(с => с.CheckFailures).HasColumnName("check_failures").IsRequired();
            строка.Property(с => с.AuthRejected).HasColumnName("auth_rejected").IsRequired();
            строка.Property(с => с.KerioVersion).HasColumnName("kerio_version").HasColumnType("text");
            строка.Property(с => с.KerioRole).HasColumnName("kerio_role").HasColumnType("text");
            строка.Property(с => с.KerioCertificateCount).HasColumnName("kerio_certificate_count");

            строка.Property(с => с.ServedSha256).HasColumnName("served_sha256").HasColumnType("text");
            строка.Property(с => с.ServedSubject).HasColumnName("served_subject").HasColumnType("text");
            строка.Property(с => с.ServedIssuer).HasColumnName("served_issuer").HasColumnType("text");
            строка.Property(с => с.ServedSerial).HasColumnName("served_serial").HasColumnType("text");
            строка.Property(с => с.ServedNotBefore).HasColumnName("served_not_before").HasColumnType("timestamptz");
            строка.Property(с => с.ServedNotAfter).HasColumnName("served_not_after").HasColumnType("timestamptz");

            строка.Property(с => с.InstalledSerial).HasColumnName("installed_serial").HasColumnType("text");
            строка.Property(с => с.InstalledSha256).HasColumnName("installed_sha256").HasColumnType("text");
            строка.Property(с => с.InstalledNotBefore).HasColumnName("installed_not_before").HasColumnType("timestamptz");
            строка.Property(с => с.InstalledNotAfter).HasColumnName("installed_not_after").HasColumnType("timestamptz");
            строка.Property(с => с.InstalledAt).HasColumnName("installed_at").HasColumnType("timestamptz");
            строка.Property(с => с.InstalledKerioId).HasColumnName("installed_kerio_id").HasColumnType("text");
            строка.Property(с => с.PreviousSerials).HasColumnName("previous_serials").HasColumnType("text[]").IsRequired()
                .HasDefaultValueSql("'{}'::text[]");

            строка.Property(с => с.LastDeployAt).HasColumnName("last_deploy_at").HasColumnType("timestamptz");
            строка.Property(с => с.LastDeployError).HasColumnName("last_deploy_error").HasColumnType("text");
            строка.Property(с => с.FailedSerial).HasColumnName("failed_serial").HasColumnType("text");
            строка.Property(с => с.DeployFailures).HasColumnName("deploy_failures").IsRequired();
            строка.Property(с => с.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamptz");
            строка.Property(с => с.DryRunSerial).HasColumnName("dry_run_serial").HasColumnType("text");
            строка.Ignore(с => с.Requested);
            строка.Property(с => с.LeaseUntil).HasColumnName("lease_until").HasColumnType("timestamptz");
            строка.Property(с => с.LeaseOwner).HasColumnName("lease_owner");

            строка.Property(с => с.Alerts)
                .HasColumnName("alerts").HasColumnType("jsonb").IsRequired()
                .HasConversion(
                    словарь => JsonSerializer.Serialize(словарь, Json),
                    json => new Dictionary<string, DateTimeOffset>(
                        JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(json, Json) ?? new(), StringComparer.Ordinal),
                    new ValueComparer<Dictionary<string, DateTimeOffset>>(
                        (а, б) => а != null && б != null && а.Count == б.Count && !а.Except(б).Any(),
                        словарь => словарь.Count,
                        словарь => new Dictionary<string, DateTimeOffset>(словарь, StringComparer.Ordinal)));

            строка.Property(с => с.Store)
                .HasColumnName("store").HasColumnType("jsonb").IsRequired().HasDefaultValueSql("'[]'::jsonb")
                .HasConversion(
                    список => JsonSerializer.Serialize(список, Json),
                    json => JsonSerializer.Deserialize<List<StoredCertificate>>(json, Json) ?? new List<StoredCertificate>(),
                    new ValueComparer<List<StoredCertificate>>(
                        (а, б) => а != null && б != null && а.Count == б.Count && JsonSerializer.Serialize(а, Json) == JsonSerializer.Serialize(б, Json),
                        список => список.Count,
                        список => список.ToList()));
            строка.Property(с => с.StoreCheckedAt).HasColumnName("store_checked_at").HasColumnType("timestamptz");

            строка.Property(с => с.Extras)
                .HasColumnName("extras").HasColumnType("jsonb").IsRequired().HasDefaultValueSql("'[]'::jsonb")
                .HasConversion(
                    список => JsonSerializer.Serialize(список, Json),
                    json => JsonSerializer.Deserialize<List<ExtraCertificate>>(json, Json) ?? new List<ExtraCertificate>(),
                    new ValueComparer<List<ExtraCertificate>>(
                        (а, б) => а != null && б != null && JsonSerializer.Serialize(а, Json) == JsonSerializer.Serialize(б, Json),
                        список => список.Count,
                        // ⚠ Глубокая копия: элементы меняются на месте, и снимок трекера не должен меняться вместе с ними.
                        список => JsonSerializer.Deserialize<List<ExtraCertificate>>(JsonSerializer.Serialize(список, Json), Json)!));

            строка.Property(с => с.InstallQueue)
                .HasColumnName("install_queue").HasColumnType("jsonb").IsRequired().HasDefaultValueSql("'[]'::jsonb")
                .HasConversion(
                    список => JsonSerializer.Serialize(список, Json),
                    json => JsonSerializer.Deserialize<List<string>>(json, Json) ?? new List<string>(),
                    new ValueComparer<List<string>>(
                        (а, б) => а != null && б != null && а.SequenceEqual(б),
                        список => список.Count,
                        список => список.ToList()));

            строка.Property(с => с.ServedByName)
                .HasColumnName("served_by_name").HasColumnType("jsonb").IsRequired().HasDefaultValueSql("'{}'::jsonb")
                .HasConversion(
                    словарь => JsonSerializer.Serialize(словарь, Json),
                    json => new Dictionary<string, string>(
                        JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? new(), StringComparer.Ordinal),
                    new ValueComparer<Dictionary<string, string>>(
                        (а, б) => а != null && б != null && а.Count == б.Count && !а.Except(б).Any(),
                        словарь => словарь.Count,
                        словарь => new Dictionary<string, string>(словарь, StringComparer.Ordinal)));
        });

        model.Entity<KerioDeployment>(строка =>
        {
            строка.ToTable(DeploymentsTable, таблица =>
            {
                таблица.HasCheckConstraint(
                    $"ck_{DeploymentsTable}_outcome",
                    $"outcome IN ('{DeployOutcomes.Running}', '{DeployOutcomes.Succeeded}', '{DeployOutcomes.Failed}', "
                    + $"'{DeployOutcomes.RolledBack}', '{DeployOutcomes.DryRun}')");
            });

            строка.HasKey(у => у.Id).HasName($"pk_{DeploymentsTable}");
            строка.HasIndex(у => new { у.ServerId, у.StartedAt }).HasDatabaseName($"ix_{DeploymentsTable}_server_started");
            строка.HasIndex(у => у.StartedAt).HasDatabaseName($"ix_{DeploymentsTable}_started");
            строка.HasIndex(у => new { у.Outcome, у.StartedAt }).HasDatabaseName($"ix_{DeploymentsTable}_outcome_started");
            строка.HasIndex(у => у.ServerId).HasDatabaseName($"ix_{DeploymentsTable}_running").HasFilter("outcome = 'running'");

            строка.Property(у => у.Id).HasColumnName("id").ValueGeneratedNever();
            строка.Property(у => у.ServerId).HasColumnName("server_id").IsRequired();
            строка.Property(у => у.ServerName).HasColumnName("server_name").HasColumnType("text").IsRequired();
            строка.Property(у => у.Trigger).HasColumnName("trigger").HasColumnType("text").IsRequired();
            строка.Property(у => у.Outcome).HasColumnName("outcome").HasColumnType("text").IsRequired();
            строка.Property(у => у.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");
            строка.Property(у => у.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamptz");
            строка.Property(у => у.ActorId).HasColumnName("actor_id");
            строка.Property(у => у.CommonName).HasColumnName("common_name").HasColumnType("text").IsRequired();
            строка.Property(у => у.SerialNumber).HasColumnName("serial_number").HasColumnType("text").IsRequired();
            строка.Property(у => у.Sha256).HasColumnName("sha256").HasColumnType("text");
            строка.Property(у => у.NotAfter).HasColumnName("not_after").HasColumnType("timestamptz");
            строка.Property(у => у.PreviousSha256).HasColumnName("previous_sha256").HasColumnType("text");
            строка.Property(у => у.KerioCertificateId).HasColumnName("kerio_certificate_id").HasColumnType("text");
            строка.Property(у => у.PreviousKerioCertificateId).HasColumnName("previous_kerio_certificate_id").HasColumnType("text");
            строка.Property(у => у.FailedStep).HasColumnName("failed_step").HasColumnType("text");
            строка.Property(у => у.Error).HasColumnName("error").HasColumnType("text");

            строка.Property(у => у.Steps)
                .HasColumnName("steps").HasColumnType("jsonb").IsRequired()
                .HasConversion(
                    шаги => JsonSerializer.Serialize(шаги, Json),
                    json => JsonSerializer.Deserialize<List<DeployStep>>(json, Json) ?? new List<DeployStep>(),
                    new ValueComparer<List<DeployStep>>(
                        (а, б) => а != null && б != null && а.SequenceEqual(б),
                        шаги => шаги.Count,
                        шаги => шаги.ToList()));
        });

        model.Entity<InstallRequest>(строка =>
        {
            строка.ToTable(RequestsTable);

            строка.HasKey(з => з.Id).HasName($"pk_{RequestsTable}");
            строка.HasIndex(з => new { з.ServerId, з.CommonName }).IsUnique().HasDatabaseName($"ux_{RequestsTable}_server_name");
            строка.HasOne<KerioServer>().WithMany().HasForeignKey(з => з.ServerId).OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName($"fk_{RequestsTable}_server");

            строка.Property(з => з.Id).HasColumnName("id").ValueGeneratedNever();
            строка.Property(з => з.ServerId).HasColumnName("server_id").IsRequired();
            строка.Property(з => з.CommonName).HasColumnName("common_name").HasColumnType("text").IsRequired();
            строка.Property(з => з.RequestedAt).HasColumnName("requested_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");
            строка.Property(з => з.RequestedBy).HasColumnName("requested_by");
        });

        model.Entity<InstallSlot>(строка =>
        {
            строка.ToTable(SlotTable, таблица => таблица.HasCheckConstraint($"ck_{SlotTable}_single", $"id = {InstallSlot.Only}"));

            строка.HasKey(с => с.Id).HasName($"pk_{SlotTable}");

            строка.Property(с => с.Id).HasColumnName("id").ValueGeneratedNever();
            строка.Property(с => с.LeaseUntil).HasColumnName("lease_until").HasColumnType("timestamptz");
            строка.Property(с => с.LeaseOwner).HasColumnName("lease_owner");
        });

        model.Entity<KerioNotification>(строка =>
        {
            строка.ToTable(NotificationsTable, таблица =>
            {
                таблица.HasCheckConstraint(
                    $"ck_{NotificationsTable}_recipients", $"cardinality(recipients) <= {KerioNotification.MaxRecipients}");
            });

            строка.HasKey(о => о.Id).HasName($"pk_{NotificationsTable}");
            строка.HasIndex(о => о.At).HasDatabaseName($"ix_{NotificationsTable}_at");

            строка.Property(о => о.Id).HasColumnName("id").ValueGeneratedNever();
            строка.Property(о => о.At).HasColumnName("at").HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
            строка.Property(о => о.ServerId).HasColumnName("server_id");
            строка.Property(о => о.ServerName).HasColumnName("server_name").HasColumnType("text");
            строка.Property(о => о.Event).HasColumnName("event").HasColumnType("text").IsRequired();
            строка.Property(о => о.Level).HasColumnName("level").HasColumnType("text").IsRequired();
            строка.Property(о => о.Title).HasColumnName("title").HasColumnType("text").IsRequired();
            строка.Property(о => о.Recipients).HasColumnName("recipients").HasColumnType("text[]").IsRequired();
            строка.Property(о => о.Queued).HasColumnName("queued").IsRequired();
        });
    }
}
