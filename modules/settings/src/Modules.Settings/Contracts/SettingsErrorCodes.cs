namespace Modules.Settings.Contracts;

/// <summary>Машинные коды ошибок модуля настроек.</summary>
public static class SettingsErrorCodes
{
    /// <summary>Настройки с таким именем не объявлял ни один модуль.</summary>
    public const string UnknownSetting = "UNKNOWN_SETTING";

    /// <summary>Значение не подходит по типу, границам или списку допустимых.</summary>
    public const string InvalidSettingValue = "INVALID_SETTING_VALUE";

    /// <summary>Настройку правят не на том уровне: личную — как системную или наоборот.</summary>
    public const string SettingLevelForbidden = "SETTING_LEVEL_FORBIDDEN";
}
