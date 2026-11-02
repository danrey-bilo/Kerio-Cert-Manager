using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using KerioCertManager.Modules.Kerio.Localization;
using Microsoft.Net.Http.Headers;

namespace KerioCertManager.Api.Infrastructure;

/// <summary>Ответы API на языке панели: русские строки JSON — через <see cref="ServerTexts"/>.</summary>
internal sealed class ServerTextTranslation(RequestDelegate next, ServerTexts texts)
{
    private const int Потолок = 4 * 1024 * 1024;

    private const int ПамятьОтветов = 64;

    /// <summary>Большие ответы не запоминаются: 64 по 4 МиБ — четверть памяти контейнера.</summary>
    private const int ПотолокПамяти = 256 * 1024;

    /// <summary>Сколько перевод может занять на один ответ; дальше строки остаются как есть.</summary>
    private static readonly TimeSpan Бюджет = TimeSpan.FromMilliseconds(200);

    /// <summary>Поля с данными людей: имя сервера «Почта: Киев» не должно стать переводом шаблона.</summary>
    private static readonly HashSet<string> Данные = new(StringComparer.Ordinal)
    {
        "name", "email", "display_name", "server_name", "common_name", "subject", "issuer", "username", "base_url",
        "user_agent", "address", "resource_name", "actor_email", "to", "to_address", "from_address", "from_name",
    };

    private static readonly JsonSerializerOptions Запись = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    private readonly ConcurrentDictionary<string, byte[]> _переведённые = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        var язык = ServerTexts.FromAcceptLanguage(context.Request.Headers.AcceptLanguage);

        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || !texts.Translates(язык))
        {
            await next(context);
            return;
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptLanguage);
            return Task.CompletedTask;
        });

        var исходный = context.Response.Body;
        await using var буфер = new JsonBuffer(исходный, context.Response, Потолок);
        context.Response.Body = буфер;

        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = исходный;
        }

        if (!буфер.Buffered)
        {
            return;
        }

        var json = буфер.Content;
        var ответ = json.Length > 0 && !context.Response.HasStarted ? Перевести(json, язык) ?? json : json;

        if (!context.Response.HasStarted)
        {
            context.Response.ContentLength = ответ.Length;
        }

        await исходный.WriteAsync(ответ, context.RequestAborted);
    }

    /// <summary>Переведённый JSON; <c>null</c> — переводить нечего.</summary>
    private ReadOnlyMemory<byte>? Перевести(ReadOnlyMemory<byte> json, string язык)
    {
        if (!json.Span.ContainsAnyInRange((byte)0xD0, (byte)0xD3))
        {
            return null;
        }

        var ключ = язык + ":" + Convert.ToHexString(SHA256.HashData(json.Span));

        if (_переведённые.TryGetValue(ключ, out var готовый))
        {
            return готовый;
        }

        JsonNode? узел;

        try
        {
            узел = JsonNode.Parse(json.Span);
        }
        catch (JsonException)
        {
            return null;
        }

        var часы = System.Diagnostics.Stopwatch.StartNew();
        byte[] итог;

        try
        {
            итог = узел is not null && Обойти(узел, null, язык, часы) ? JsonSerializer.SerializeToUtf8Bytes(узел, Запись) : json.ToArray();
        }
        catch (Exception ошибка) when (ошибка is RegexMatchTimeoutException or InvalidOperationException or JsonException)
        {
            // ⚠ Перевод — удобство: сломался он — ответ уходит как есть.
            return null;
        }

        // ⚠ Вышли за бюджет — перевод частичный: помнить его нельзя.
        if (часы.Elapsed > Бюджет || итог.Length > ПотолокПамяти)
        {
            return итог;
        }

        if (_переведённые.Count >= ПамятьОтветов)
        {
            _переведённые.Clear();
        }

        _переведённые[ключ] = итог;
        return итог;
    }

    /// <summary>Перевести строки на месте; <c>true</c> — что-то изменилось.</summary>
    private bool Обойти(JsonNode узел, string? поле, string язык, System.Diagnostics.Stopwatch часы)
    {
        var изменено = false;

        switch (узел)
        {
            case JsonObject объект:
                foreach (var (имя, значение) in объект.ToList())
                {
                    if (значение is JsonValue v && v.TryGetValue<string>(out var строка))
                    {
                        if (!Данные.Contains(имя) && Перевод(строка, язык, часы) is { } новый)
                        {
                            объект[имя] = новый;
                            изменено = true;
                        }
                    }
                    else if (значение is not null)
                    {
                        изменено |= Обойти(значение, имя, язык, часы);
                    }
                }

                break;

            case JsonArray массив:
                for (var i = 0; i < массив.Count; i++)
                {
                    if (массив[i] is JsonValue v && v.TryGetValue<string>(out var строка))
                    {
                        if ((поле is null || !Данные.Contains(поле)) && Перевод(строка, язык, часы) is { } новый)
                        {
                            массив[i] = новый;
                            изменено = true;
                        }
                    }
                    else if (массив[i] is { } вложенный)
                    {
                        изменено |= Обойти(вложенный, поле, язык, часы);
                    }
                }

                break;
        }

        return изменено;
    }

    private string? Перевод(string строка, string язык, System.Diagnostics.Stopwatch часы)
    {
        if (часы.Elapsed > Бюджет)
        {
            return null;
        }

        var перевод = texts.Translate(строка, язык);
        return перевод == строка ? null : перевод;
    }

    /// <summary>Тело ответа: JSON — в память до потолка, остальное и переросшее — сразу клиенту.</summary>
    private sealed class JsonBuffer(Stream inner, HttpResponse response, int limit) : Stream
    {
        private MemoryStream? _память;

        private bool _решено;

        private bool _насквозь;

        public bool Buffered => _память is not null && !_насквозь;

        public ReadOnlyMemory<byte> Content => _память is null ? ReadOnlyMemory<byte>.Empty : _память.GetBuffer().AsMemory(0, (int)_память.Length);

        public override bool CanRead => false;

        /// <summary>⚠ <c>Response.Clear()</c> сбрасывает тело только у потока с перемоткой: иначе ошибка допишется к обрывку ответа.</summary>
        public override bool CanSeek => Buffered;

        public override bool CanWrite => true;

        public override long Length => _память?.Length ?? 0;

        public override long Position { get => Length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Решить();

            if (!_насквозь && _память!.Length + buffer.Length > limit)
            {
                _насквозь = true;
                await inner.WriteAsync(_память.GetBuffer().AsMemory(0, (int)_память.Length), cancellationToken);
            }

            if (_насквозь)
            {
                await inner.WriteAsync(buffer, cancellationToken);
            }
            else
            {
                _память!.Write(buffer.Span);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _насквозь ? inner.FlushAsync(cancellationToken) : Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value)
        {
            if (value != 0 || !Buffered)
            {
                throw new NotSupportedException();
            }

            // ⚠ Ответ начинается заново — и его тип может быть другим.
            _память!.Dispose();
            _память = null;
            _решено = false;
        }

        public override ValueTask DisposeAsync()
        {
            _память?.Dispose();
            return ValueTask.CompletedTask;
        }

        private void Решить()
        {
            if (_решено)
            {
                return;
            }

            _решено = true;
            var тип = response.ContentType;
            var json = тип is not null && (тип.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                || тип.StartsWith("application/problem+json", StringComparison.OrdinalIgnoreCase));
            var вложение = response.Headers.ContentDisposition.ToString().Contains("attachment", StringComparison.OrdinalIgnoreCase);

            if (json && !вложение)
            {
                _память = new MemoryStream();
            }
            else
            {
                _насквозь = true;
            }
        }
    }
}
