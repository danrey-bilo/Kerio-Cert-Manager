namespace Modules.Core.Contracts.Accounts;

/// <summary>Учётные записи глазами соседних модулей: кто есть, работает ли и есть ли у него наибольшие права.</summary>
public interface IAccounts
{
    /// <summary>Неудалённые учётные записи — запросом на общем контексте данных области.</summary>
    IQueryable<AccountRow> Query();

    /// <summary>Признаки одной учётной записи; <c>null</c> — её нет или она удалена.</summary>
    Task<AccountFlags?> FlagsAsync(Guid accountId, CancellationToken cancellationToken = default);
}

/// <summary>Строка учётной записи для соседних модулей.</summary>
public sealed class AccountRow
{
    /// <summary>Идентификатор учётной записи.</summary>
    public Guid Id { get; init; }

    /// <summary>Адрес почты.</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>Отображаемое имя, если задано.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Работает ли учётная запись.</summary>
    public bool IsActive { get; init; }

    /// <summary>Есть ли наибольшие права.</summary>
    public bool IsSuperuser { get; init; }
}

/// <summary>Признаки учётной записи, по которым решает движок прав.</summary>
public sealed record AccountFlags(bool IsActive, bool IsSuperuser);
