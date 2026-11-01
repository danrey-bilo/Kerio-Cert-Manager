using System.Net;
using Modules.Core.Errors;

namespace Modules.Core.Api;

/// <summary>Размер страницы: одно правило на всю систему.</summary>
public static class PageLimit
{
    /// <summary>Проверить запрошенный размер страницы.</summary>
    public static int Require(int? requested, int fallback, int max)
    {
        if (fallback < 1 || fallback > max)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fallback), fallback, $"Умолчание вне допустимого: от 1 до {max}");
        }

        var сколько = requested ?? fallback;

        return сколько >= 1 && сколько <= max
            ? сколько
            : throw new AppException(
                ErrorCodes.Validation,
                $"Размер страницы вне допустимого: от 1 до {max}",
                HttpStatusCode.UnprocessableContent);
    }
}
