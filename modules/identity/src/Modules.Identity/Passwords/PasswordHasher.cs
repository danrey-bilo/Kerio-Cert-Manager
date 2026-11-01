using System.Net;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Modules.Core.Errors;

namespace Modules.Identity.Passwords;

/// <summary>argon2id поверх Konscious.</summary>
internal sealed class PasswordHasher : IPasswordHasher, IDisposable
{
    /// <summary>Предел длины пароля в байтах UTF-8.</summary>
    public const int MaxPasswordBytes = 1024;

    private readonly Argon2Profile _profile;
    private readonly SemaphoreSlim _gate;

    /// <summary>Сколько хешей может ждать очереди; <c>null</c> — без предела.</summary>
    private readonly int? _queueLimit;

    /// <summary>Сколько хешей сейчас ждут или проходят ворота.</summary>
    private int _ждут;

    /// <summary>Соль для холостой проверки.</summary>
    private static readonly byte[] DummySalt = Encoding.UTF8.GetBytes("dummy-timing-salt");

    public PasswordHasher(Argon2Profile? profile = null, int? maxConcurrency = null, int? queueLimit = null)
    {
        _profile = profile ?? Argon2Profile.Current;
        _profile.Validate();

        _gate = new SemaphoreSlim(maxConcurrency ?? Math.Max(2, Environment.ProcessorCount));
        _queueLimit = queueLimit;
    }

    /// <summary>Отказ «хешер занят» — по нему вызывающий отличает очередь от неверного пароля.</summary>
    internal static bool Busy(AppException error) => error.Code == ErrorCodes.RateLimited && error.Details?.ContainsKey(BusyMarker) == true;

    /// <summary>Отказ «хешер занят»: <c>429 RATE_LIMITED</c> с меткой в подробностях.</summary>
    internal static AppException BusyError() => new(
        ErrorCodes.RateLimited,
        "Сервер занят проверкой паролей. Повторите через минуту.",
        HttpStatusCode.TooManyRequests,
        new Dictionary<string, object?>(StringComparer.Ordinal) { [BusyMarker] = true });

    /// <summary>Метка отказа в подробностях: «занято» хешера, а не предел частоты пути.</summary>
    private const string BusyMarker = "password_hashing";

    public async Task<string> HashAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);

        // ⚠ Пустой пароль отсекается ЗДЕСЬ, и это не формальность.
        if (password.Length == 0)
        {
            throw new ArgumentException("Пустой пароль хешировать нельзя", nameof(password));
        }

        var bytes = Encoding.UTF8.GetBytes(password);

        if (bytes.Length > MaxPasswordBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);

            throw new ArgumentOutOfRangeException(
                nameof(password), "Пароль длиннее допустимого");
        }

        var salt = RandomNumberGenerator.GetBytes(_profile.SaltBytes);

        try
        {
            var hash = await ComputeAsync(bytes, salt, _profile, cancellationToken);

            return PhcString.Encode(new PasswordHash(_profile, salt, hash));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<bool> VerifyAsync(
        string? encoded, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (!PhcString.TryDecode(encoded, out var stored) || stored is null)
        {
            await WasteVerificationTimeAsync(cancellationToken);

            return false;
        }

        if (password.Length == 0)
        {
            await WasteVerificationTimeAsync(cancellationToken);

            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(password);

        if (bytes.Length > MaxPasswordBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);

            await WasteVerificationTimeAsync(cancellationToken);

            return false;
        }

        try
        {
            // ⚠ Параметры берутся ИЗ СТРОКИ, а не текущие.
            var actual = await ComputeAsync(bytes, stored.Salt, stored.Profile, cancellationToken);

            return CryptographicOperations.FixedTimeEquals(actual, stored.Hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool NeedsRehash(string? encoded)
    {
        if (!PhcString.TryDecode(encoded, out var stored) || stored is null)
        {
            return false;
        }

        return stored.Profile.IsWeakerThan(_profile);
    }

    public async Task WasteVerificationTimeAsync(CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes("этот пароль не подходит никому");

        try
        {
            var burned = await ComputeAsync(bytes, DummySalt, _profile, cancellationToken);

            CryptographicOperations.ZeroMemory(burned);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Встать в очередь ворот — или получить отказ, если она полна.</summary>
    private async Task ВойтиAsync(CancellationToken cancellationToken)
    {
        if (_queueLimit is not { } предел)
        {
            await _gate.WaitAsync(cancellationToken);

            return;
        }

        if (_gate.Wait(0))
        {
            return;
        }

        if (Interlocked.Increment(ref _ждут) > предел)
        {
            Interlocked.Decrement(ref _ждут);

            throw BusyError();
        }

        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _ждут);
        }
    }

    /// <summary>Собственно счёт, под ограничением одновременности.</summary>
    private async Task<byte[]> ComputeAsync(
        byte[] password, byte[] salt, Argon2Profile profile, CancellationToken cancellationToken)
    {
        await ВойтиAsync(cancellationToken);

        try
        {
            using var argon2 = new Argon2id(password)
            {
                Salt = salt,
                MemorySize = profile.MemoryKib,
                Iterations = profile.Iterations,
                DegreeOfParallelism = profile.Parallelism,
            };

            return await argon2.GetBytesAsync(profile.HashBytes);
        }
        finally
        {
            _gate.Release();
        }
    }
}
