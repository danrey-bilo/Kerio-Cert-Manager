using System.Data.Common;
using Modules.Audit.Contracts;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Audit.Integrity;

/// <summary>Чем кончилась проверка одного месяца.</summary>
internal sealed record ChainMonth(string Partition, long Chained, long Unchained, string? Broken)
{
    /// <summary>Сошлась ли цепь этого месяца.</summary>
    public bool Ok => Broken is null;
}

/// <summary>Чем кончилась проверка целиком.</summary>
internal sealed record ChainReport(IReadOnlyList<ChainMonth> Months)
{
    /// <summary>Сошлось ли всё.</summary>
    public bool Ok => Months.All(month => month.Ok);

    /// <summary>Сколько звеньев проверено всего.</summary>
    public long Chained => Months.Sum(month => month.Chained);
}

/// <summary>Пересчёт цепочки: сходится ли журнал сам с собой.</summary>
internal sealed class ChainVerifier(IDbContextFactory<AppDbContext> contexts)
{
    /// <summary>Проверить все месяцы, где есть строки.</summary>
    public async Task<ChainReport> VerifyAsync(
        DateOnly? only = null,
        TextWriter? export = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();

        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var months = only is { } single
                ? [AuditSchema.MonthStart(single)]
                : await MonthsAsync(connection, cancellationToken);

            var report = new List<ChainMonth>();

            foreach (var month in months)
            {
                // ⚠ ⚠ Месяц — одним снимком (ревизия 2026-10-26, решение 0118).
                await using var снимок = await connection.BeginTransactionAsync(
                    System.Data.IsolationLevel.RepeatableRead, cancellationToken);

                report.Add(await VerifyMonthAsync(connection, month, export, cancellationToken));

                await снимок.CommitAsync(cancellationToken);
            }

            return new ChainReport(report);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Месяцы, в которых есть хоть одна строка.</summary>
    private static async Task<IReadOnlyList<DateOnly>> MonthsAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        var months = new List<DateOnly>();

        await using var command = connection.CreateCommand();
        command.CommandText = AuditChain.SelectMonths();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            months.Add(reader.GetValue(0) switch
            {
                DateOnly date => date,
                DateTime moment => DateOnly.FromDateTime(moment),
                var other => throw new InvalidOperationException(
                    $"Неожиданный тип даты от базы: {other?.GetType().Name ?? "null"}"),
            });
        }

        return months;
    }

    /// <summary>Проверить один месяц.</summary>
    private static async Task<ChainMonth> VerifyMonthAsync(
        DbConnection connection, DateOnly month, TextWriter? export, CancellationToken cancellationToken)
    {
        var partition = AuditSchema.PartitionName(month);
        var expected = (byte[])AuditChain.Genesis(partition);

        long chained = 0;
        long unchained = 0;
        long lastUnchained = -1;
        long firstChained = long.MaxValue;
        string? broken = null;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = AuditChain.SelectCanonical("@from", "@to");
            Add(command, "from", new DateTimeOffset(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
            Add(command, "to", new DateTimeOffset(
                month.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken) && broken is null)
            {
                if (await reader.IsDBNullAsync(0, cancellationToken))
                {
                    unchained++;
                    lastUnchained = Math.Max(lastUnchained, Number(reader));

                    continue;
                }

                var seq = reader.GetInt64(0);

                firstChained = Math.Min(firstChained, Number(reader));
                var previous = await reader.GetFieldValueAsync<byte[]>(1, cancellationToken);
                var hash = await reader.GetFieldValueAsync<byte[]>(2, cancellationToken);

                chained++;

                if (seq != chained)
                {
                    broken = $"пропуск звена: ожидалось {chained}, пришло {seq}";
                    break;
                }

                if (!previous.AsSpan().SequenceEqual(expected))
                {
                    broken = $"звено {seq} не сцеплено с предыдущим";
                    break;
                }

                var fields = new string?[AuditChain.FieldCount];

                for (var index = 0; index < fields.Length; index++)
                {
                    var column = index + 3;

                    fields[index] = await reader.IsDBNullAsync(column, cancellationToken)
                        ? null
                        : reader.GetString(column);
                }

                if (export is not null)
                {
                    await export.WriteLineAsync(Line(partition, seq, previous, hash, fields));
                }

                if (!AuditChain.Hash(previous, fields).AsSpan().SequenceEqual(hash))
                {
                    broken = $"звено {seq}: отпечаток не сходится с содержимым строки";
                    break;
                }

                expected = hash;
            }
        }

        // ⚠ Строка без отпечатка ПОЗЖЕ первого звена — улика: значит, писали со снятым триггером.
        if (broken is null && unchained > 0 && chained > 0 && lastUnchained > firstChained)
        {
            broken = $"строка без отпечатка после начала цепочки (id={lastUnchained})";
        }

        return broken is not null
            ? new ChainMonth(partition, chained, unchained, broken)
            : new ChainMonth(
                partition, chained, unchained,
                await HeadAsync(connection, partition, chained, expected, cancellationToken));
    }

    /// <summary>Сходится ли голова цепочки с тем, чем кончился пересчёт.</summary>
    private static async Task<string?> HeadAsync(
        DbConnection connection,
        string partition,
        long chained,
        byte[] expected,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT last_seq, last_hash FROM {AuditChain.HeadTable} WHERE partition = @p";
        Add(command, "p", partition);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return chained == 0 ? null : $"голова цепочки {partition} исчезла";
        }

        var seq = reader.GetInt64(0);
        var hash = await reader.GetFieldValueAsync<byte[]>(1, cancellationToken);

        if (seq != chained)
        {
            return $"голова говорит про {seq} звеньев, найдено {chained}";
        }

        return hash.AsSpan().SequenceEqual(expected) ? null : "голова не совпала с последним звеном";
    }

    /// <summary>Одна строка выгрузки — JSON без переводов строк внутри.</summary>
    private static string Line(
        string partition, long seq, byte[] previous, byte[] hash, string?[] fields) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["partition"] = partition,
            ["seq"] = seq,
            ["prev"] = Convert.ToHexStringLower(previous),
            ["hash"] = Convert.ToHexStringLower(hash),
            ["fields"] = fields,
        });

    /// <summary>Номер строки — из канонического вида, где он лежит текстом.</summary>
    private static long Number(DbDataReader reader) =>
        long.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Параметр команды — через фабрику провайдера, а не склейкой.</summary>
    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = name;
        parameter.Value = value;

        command.Parameters.Add(parameter);
    }
}
