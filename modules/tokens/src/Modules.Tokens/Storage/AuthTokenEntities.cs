using Modules.Core.Contracts.Tokens;
using Modules.Tokens.Contracts;
using Modules.Tokens.Domain;
using Modules.Core.Contracts.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Tokens.Storage;

/// <summary>Схема модуля: две таблицы.</summary>
internal sealed class AuthTokenEntities : IEntityConfigurationProvider
{
    public void Configure(ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Entity<AuthToken>(token =>
        {
            token.ToTable(ApiTokenTables.Tokens, table =>
            {
                table.HasCheckConstraint(
                    "ck_auth_tokens_kind",
                    $"kind IN ({Значения(TokenKinds.All)})");

                // ⚠ Условие двустороннее, и обе стороны нужны.
                table.HasCheckConstraint(
                    "ck_auth_tokens_scopes",
                    $"""
                    (kind = '{TokenKinds.ApiKey}' AND scopes IS NOT NULL AND jsonb_array_length(scopes) > 0)
                    OR (kind = '{TokenKinds.DownloadTicket}' AND (scopes IS NULL OR jsonb_array_length(scopes) = 0))
                    OR (kind NOT IN ('{TokenKinds.ApiKey}', '{TokenKinds.DownloadTicket}') AND scopes IS NULL)
                    """);
            });

            token.HasKey(row => row.Id).HasName("pk_auth_tokens");

            token.Property(row => row.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            token.Property(row => row.UserId).HasColumnName("user_id").IsRequired();
            token.Property(row => row.Kind).HasColumnName("kind").HasMaxLength(32).IsRequired();
            token.Property(row => row.Name).HasColumnName("name").HasMaxLength(512);
            token.Property(row => row.Address).HasColumnName("address").HasColumnType("inet");
            token.Property(row => row.DeviceRef).HasColumnName("device_ref").HasMaxLength(256);
            token.Property(row => row.Scopes).HasColumnName("scopes").HasColumnType("jsonb");
            token.Property(row => row.StepUpAt).HasColumnName("step_up_at");
            token.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            token.Property(row => row.LastSeenAt).HasColumnName("last_seen_at").HasDefaultValueSql("now()");
            token.Property(row => row.ExpiresAt).HasColumnName("expires_at").IsRequired();
            token.Property(row => row.RevokedAt).HasColumnName("revoked_at");
            token.Property(row => row.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(64);

            token.HasIndex(row => new { row.UserId, row.Kind })
                .HasDatabaseName("ix_auth_tokens_user_kind")
                .HasFilter("revoked_at IS NULL");

            // ⚠ Полный — под «знакомо ли устройство» на каждом входе (решение 0118).
            token.HasIndex(row => new { row.UserId, row.Kind, row.Name })
                .HasDatabaseName("ix_auth_tokens_user_kind_name");

            // ⚠ Уникальность имени — только у ключей интеграции и только среди ЖИВЫХ.
            token.HasIndex(row => new { row.UserId, row.Name })
                .HasDatabaseName("ux_auth_tokens_user_name")
                .IsUnique()
                .HasFilter($"kind = '{TokenKinds.ApiKey}' AND revoked_at IS NULL");

            token.HasIndex(row => row.DeviceRef)
                .HasDatabaseName("ix_auth_tokens_device")
                .HasFilter("device_ref IS NOT NULL AND revoked_at IS NULL");

            // ⚠ Индексы под уборку заводятся ЗДЕСЬ, а не когда уборка появится.
            token.HasIndex(row => row.ExpiresAt).HasDatabaseName("ix_auth_tokens_expires");
            token.HasIndex(row => row.RevokedAt).HasDatabaseName("ix_auth_tokens_revoked");
        });

        model.Entity<AuthTokenSecret>(secret =>
        {
            secret.ToTable(ApiTokenTables.Secrets, table => table.HasCheckConstraint(
                "ck_auth_token_secrets_kind", $"kind IN ({Значения(SecretKinds.All)})"));

            secret.HasKey(row => row.Id).HasName("pk_auth_token_secrets");

            secret.Property(row => row.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            secret.Property(row => row.TokenId).HasColumnName("token_id").IsRequired();
            secret.Property(row => row.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
            secret.Property(row => row.Selector).HasColumnName("selector").HasMaxLength(64);
            secret.Property(row => row.Hash).HasColumnName("hash").IsRequired();
            secret.Property(row => row.IssuedAt).HasColumnName("issued_at").HasDefaultValueSql("now()");
            secret.Property(row => row.ExpiresAt).HasColumnName("expires_at").IsRequired();
            secret.Property(row => row.UsedAt).HasColumnName("used_at");

            // ⚠ Ключ СВОЙ — обе таблицы наши, и здесь он законен и нужен: секрет без своей строки — мусор, который никто не уберёт.
            secret.HasOne<AuthToken>()
                .WithMany()
                .HasForeignKey(row => row.TokenId)
                .HasConstraintName("fk_auth_token_secrets_token_id_auth_tokens")
                .OnDelete(DeleteBehavior.Cascade);

            // ⚠ Уникальный, а не просто быстрый.
            secret.HasIndex(row => row.Hash).HasDatabaseName("ux_auth_token_secrets_hash").IsUnique();

            secret.HasIndex(row => row.Selector)
                .HasDatabaseName("ux_auth_token_secrets_selector")
                .IsUnique()
                .HasFilter("selector IS NOT NULL");

            secret.HasIndex(row => row.TokenId).HasDatabaseName("ix_auth_token_secrets_token");

            secret.HasIndex(row => row.ExpiresAt).HasDatabaseName("ix_auth_token_secrets_expires");
        });
    }

    /// <summary>Список значений для <c>CHECK</c>: собирается из констант, а не пишется дважды.</summary>
    private static string Значения(IEnumerable<string> значения) =>
        string.Join(", ", значения.Select(значение => $"'{значение}'"));
}
