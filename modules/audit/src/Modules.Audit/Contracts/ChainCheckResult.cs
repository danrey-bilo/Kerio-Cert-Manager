namespace Modules.Audit.Contracts;

/// <summary>Итог проверки цепочки одного месяца.</summary>
public sealed record ChainCheckLine(string Partition, long Chained, long Unchained, string? Broken)
{
    /// <summary>Сошлась ли цепь этого месяца.</summary>
    public bool Ok => Broken is null;
}

/// <summary>Итог проверки целиком — то, что команда печатает человеку.</summary>
public sealed record ChainCheckResult(IReadOnlyList<ChainCheckLine> Lines)
{
    /// <summary>Сошлось ли всё.</summary>
    public bool Ok => Lines.All(line => line.Ok);
}
