namespace Modules.Core.Contracts.Settings;

/// <summary>На каком языке писать вот этому человеку.</summary>
public interface IUserLocale
{
    /// <summary>Язык человека: <c>uk</c>, <c>ru</c> или <c>en</c>.</summary>
    Task<string> ForAsync(Guid userId, CancellationToken cancellationToken = default);
}
