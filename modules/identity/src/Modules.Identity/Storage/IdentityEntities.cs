using Modules.Identity.Domain;
using Modules.Core.Contracts.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Storage;

/// <summary>Таблицы модуля в общей модели.</summary>
internal sealed class IdentityEntities : IEntityConfigurationProvider
{
    /// <summary>Имя таблицы пользователей.</summary>
    public const string UsersTable = "users";

    /// <summary>Имя таблицы одноразовых ссылок.</summary>
    public const string TokensTable = "identity_tokens";

    /// <summary>Имя таблицы второго фактора.</summary>
    public const string TwoFactorTable = "user_two_factor";

    /// <summary>Частичный уникальный индекс по адресу.</summary>
    public const int DepartmentLength = 128;

    public const string EmailIndex = "ix_users_email_live";

    public void Configure(ModelBuilder model)
    {
        model.Entity<User>(user =>
        {
            user.ToTable(UsersTable);

            user.HasKey(row => row.Id).HasName("pk_users");

            user.Property(row => row.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");

            user.Property(row => row.Email).HasColumnName("email").HasColumnType("citext").IsRequired();

            user.Property(row => row.DisplayName).HasColumnName("display_name").HasMaxLength(200);
            user.Property(row => row.PasswordHash).HasColumnName("password_hash").IsRequired();
            user.Property(row => row.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            user.Property(row => row.IsSuperuser).HasColumnName("is_superuser").HasDefaultValue(false);
            user.Property(row => row.MustChangePassword).HasColumnName("must_change_password").HasDefaultValue(false);
            user.Property(row => row.PasswordChangedAt).HasColumnName("password_changed_at");
            user.Property(row => row.FailedLoginCount).HasColumnName("failed_login_count").HasDefaultValue(0);
            user.Property(row => row.LockedUntil).HasColumnName("locked_until");
            user.Property(row => row.LockoutLevel).HasColumnName("lockout_level").HasDefaultValue(0);
            user.Property(row => row.Department).HasColumnName("department").HasMaxLength(DepartmentLength);
            user.Property(row => row.ActivatedAt).HasColumnName("activated_at");
            user.Property(row => row.TwoFactorConfiguredAt).HasColumnName("two_factor_configured_at");
            user.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            user.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            user.Property(row => row.DeletedAt).HasColumnName("deleted_at");

            // ⚠ Обнуляемая колонка, и `null` — не «не заполнено», а третье состояние «как в системе» (этап 17.7).
            user.Property(row => row.SecondFactorMethod)
                .HasColumnName("second_factor_method").HasColumnType("text");

            user.HasIndex(row => row.Email)
                .HasDatabaseName(EmailIndex)
                .IsUnique()
                .HasFilter("deleted_at IS NULL");
        });

        model.Entity<IdentityToken>(token =>
        {
            token.ToTable(TokensTable, table => table.HasCheckConstraint(
                // ⚠ Список назначений в БАЗЕ, а не только в коде: строка с чужим назначением никогда не найдётся запросом и будет жить незамеченной.
                "ck_identity_tokens_purpose",
                "purpose IN ('activation', 'reset', 'two_factor', 'login_code', 'step_up_code', 'trusted_device', 'superuser_setup')"));

            token.HasKey(row => row.Id).HasName("pk_identity_tokens");

            token.HasOne<User>()
                .WithMany()
                .HasForeignKey(row => row.UserId)
                .HasConstraintName("fk_identity_tokens_user_id_users")
                .OnDelete(DeleteBehavior.Cascade);

            token.Property(row => row.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            token.Property(row => row.UserId).HasColumnName("user_id").IsRequired();
            token.Property(row => row.TokenHash).HasColumnName("token_hash").IsRequired();
            token.Property(row => row.Purpose).HasColumnName("purpose").IsRequired();
            token.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            token.Property(row => row.ExpiresAt).HasColumnName("expires_at").IsRequired();
            token.Property(row => row.UsedAt).HasColumnName("used_at");
            token.Property(row => row.Binding).HasColumnName("binding").HasMaxLength(128);
            token.Property(row => row.ClientName).HasColumnName("client_name").HasMaxLength(300);
            token.Property(row => row.ClientKind).HasColumnName("client_kind").HasMaxLength(32);
            token.Property(row => row.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
            token.Property(row => row.Address).HasColumnName("address").HasColumnType("inet");
            token.Property(row => row.LastSeenAt).HasColumnName("last_seen_at");
            token.Property(row => row.DeviceId).HasColumnName("device_id");

            // ⚠ Уникальный, и по одному хешу, а не по паре с назначением.
            token.HasIndex(row => row.TokenHash).HasDatabaseName("ix_identity_tokens_hash").IsUnique();

            token.HasIndex(row => row.UserId).HasDatabaseName("ix_identity_tokens_user");

            token.HasIndex(row => row.ExpiresAt).HasDatabaseName("ix_identity_tokens_expires");
        });

        model.Entity<UserTwoFactor>(factor =>
        {
            factor.ToTable(TwoFactorTable);

            factor.HasKey(row => row.UserId).HasName("pk_user_two_factor");

            factor.HasOne<User>()
                .WithOne()
                .HasForeignKey<UserTwoFactor>(row => row.UserId)
                .HasConstraintName("fk_user_two_factor_user_id_users")
                .OnDelete(DeleteBehavior.Cascade);

            factor.Property(row => row.UserId).HasColumnName("user_id");
            factor.Property(row => row.DataKey).HasColumnName("data_key").IsRequired();
            factor.Property(row => row.Secret).HasColumnName("secret").IsRequired();
            factor.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            factor.Property(row => row.ConfirmedAt).HasColumnName("confirmed_at");
            factor.Property(row => row.LastStep).HasColumnName("last_step").HasDefaultValue(-1L);
        });

    }
}
