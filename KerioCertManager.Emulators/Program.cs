using System.Globalization;

namespace KerioCertManager.Emulators;

/// <summary>Стенд для демо и проверки: эмулятор Key Manager Plus и серверов Kerio Connect.</summary>
internal static class EmulatorProgram
{
    public static async Task Main(string[] args)
    {
        var kerio = Env("EMULATOR_KERIO", "kerio-a:4041:993,kerio-b:4042:995")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(spec => spec.Split(':'))
            .Select(parts => new KerioSpec(parts[0], Int(parts[1]), [.. parts.Skip(2).Select(Int)]))
            .ToList();

        var certificates = Env("EMULATOR_CERTIFICATES", "mail.demo.local=12,*.demo.local=200,webmail.demo.local=45")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('='))
            .Select(parts => (parts[0], Int(parts[1])))
            .ToList();

        var options = new EmulatorOptions
        {
            ControlPort = Int(Env("EMULATOR_CONTROL_PORT", "8080")),
            KmpPort = Int(Env("EMULATOR_KMP_PORT", "6565")),
            KmpApiKey = Env("EMULATOR_KMP_API_KEY", "demo-api-key"),
            KmpHostNames = Env("EMULATOR_KMP_NAMES", "localhost,emulators,kmp.demo.local").Split(','),
            Kerio = kerio,
            KerioUser = Env("EMULATOR_KERIO_USER", "kcm"),
            KerioPassword = Env("EMULATOR_KERIO_PASSWORD", "Demo-Passw0rd!"),
            Certificates = certificates,
            DataDirectory = Env("EMULATOR_DATA", "/data"),
            PublicControlUrl = Env("EMULATOR_PUBLIC_URL", "http://emulators:8080"),
            SeedForeign = Env("EMULATOR_SEED_FOREIGN", "true") == "true",
            SessionResetSeconds = Int(Env("EMULATOR_SESSION_RESET_SECONDS", "25")),
        };

        await using var host = EmulatorHost.Build(options, args);
        await host.App.RunAsync();
    }

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    private static int Int(string text) => int.Parse(text, CultureInfo.InvariantCulture);
}
