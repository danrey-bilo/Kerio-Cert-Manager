namespace Modules.Identity.Contracts;

/// <summary>Поля человека в справочнике сотрудников сверх имени.</summary>
[Flags]
public enum DirectoryFields
{
    /// <summary>Только имя.</summary>
    None = 0,

    /// <summary>Адрес почты.</summary>
    Email = 1,

    /// <summary>Отдел, если задан.</summary>
    Department = 2,
}
