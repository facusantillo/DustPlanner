using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DustPlanner.Core.Json;

namespace DustPlanner.Kick;

/// <summary>What the service keeps about its pairing with the relay. Never a Kick token: those live in the relay only (PRD §4, decision 5).</summary>
public sealed record KickPairing(string DeviceToken, RelayChannel Channel, string RelayUrl, DateTimeOffset PairedAt);

/// <summary>Where the device token sleeps between runs.</summary>
public interface IDeviceTokenStore
{
    KickPairing? Load();

    void Save(KickPairing pairing);

    void Delete();
}

/// <summary>
/// <c>%LocalAppData%\DustPlanner\kick.json</c>. On Windows the token is protected with DPAPI for the current user, so a
/// copy of the file is useless elsewhere. On other systems (development, CI) it is stored as is, with a flag that says so.
/// </summary>
public sealed class FileDeviceTokenStore : IDeviceTokenStore
{
    public const string FileName = "kick.json";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DustPlanner.Kick.device-token.v1");
    private readonly string _path;
    private readonly object _gate = new();

    public FileDeviceTokenStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(directory, FileName);
    }

    public string FilePath => _path;

    public KickPairing? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            try
            {
                var file = JsonSerializer.Deserialize<StoredPairing>(File.ReadAllText(_path), CoreJson.Options);
                if (file?.Channel is null || string.IsNullOrEmpty(file.Token))
                {
                    return null;
                }

                var token = file.Protection == "dpapi" ? Unprotect(file.Token) : file.Token;
                return token is null ? null : new KickPairing(token, file.Channel, file.RelayUrl ?? string.Empty, file.PairedAt);
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is CryptographicException || ex is FormatException)
            {
                return null;
            }
        }
    }

    public void Save(KickPairing pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var protectedToken = Protect(pairing.DeviceToken);
            var file = new StoredPairing
            {
                Protection = protectedToken is null ? "none" : "dpapi",
                Token = protectedToken ?? pairing.DeviceToken,
                Channel = pairing.Channel,
                RelayUrl = pairing.RelayUrl,
                PairedAt = pairing.PairedAt,
            };
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, CoreJson.Options));
            File.Move(temp, _path, overwrite: true);
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // nothing to do: the token is useless once /disconnect ran
            }
        }
    }

    /// <summary>Base64 of the DPAPI blob on Windows; null elsewhere (the caller stores the token in clear and says so).</summary>
    private static string? Protect(string token)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
    }

    private static string? Unprotect(string blob)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
    }

    private sealed class StoredPairing
    {
        public string Protection { get; set; } = "none";

        public string Token { get; set; } = string.Empty;

        public RelayChannel? Channel { get; set; }

        public string? RelayUrl { get; set; }

        public DateTimeOffset PairedAt { get; set; }
    }
}
