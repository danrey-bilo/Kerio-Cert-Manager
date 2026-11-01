namespace Modules.Core.Composition;

/// <summary>Пространство ключей advisory-замков PostgreSQL: модуль берёт ключ своим номером, пространство задаёт проект.</summary>
public sealed record AdvisoryLockSpace(long Space)
{
    /// <summary>Пространство по умолчанию — нейтральное, без имени продукта («MODS»).</summary>
    public static AdvisoryLockSpace Default { get; } = new(0x4D4F4453);

    /// <summary>Ключ замка с номером <paramref name="number"/>.</summary>
    public long Key(ushort number) => (Space << 16) | number;

    /// <summary>Широкий ключ: <c>(пространство &lt;&lt; 32) | номер</c> — так был устроен прежний ключ переноса в дереве хранилищ.</summary>
    public long WideKey(uint number) => (Space << 32) | number;
}
