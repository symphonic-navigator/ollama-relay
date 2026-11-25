using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace OllamaRelay.Relay.Security;

/// <summary>
/// argon2id in PHC string format: <c>$argon2id$v=19$m=65536,t=3,p=1$salt$hash</c>.
/// Parameters are stored with each hash, so they can be raised later without breaking old hashes.
/// </summary>
public sealed class PasswordHasher(RelayOptions options)
{
    public const int MinLength = 12;
    public const int MaxLength = 256;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly Lazy<string> _dummy = new(() => Hash(Guid.NewGuid().ToString(), options));

    public string Hash(string password) => Hash(password, options);

    private static string Hash(string password, RelayOptions o)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, o.Argon2MemoryKib, o.Argon2Iterations, 1);
        return $"$argon2id$v=19$m={o.Argon2MemoryKib},t={o.Argon2Iterations},p=1$" +
               $"{Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(hash).TrimEnd('=')}";
    }

    public bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id")
        {
            return false;
        }

        int m = 0, t = 0, p = 0;
        foreach (var kv in parts[3].Split(','))
        {
            var pair = kv.Split('=');
            if (pair.Length != 2 || !int.TryParse(pair[1], out var value))
            {
                return false;
            }

            switch (pair[0])
            {
                case "m": m = value; break;
                case "t": t = value; break;
                case "p": p = value; break;
            }
        }

        var salt = FromB64(parts[4]);
        var expected = FromB64(parts[5]);
        var actual = Compute(password, salt, m, t, p);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Spends the same work as a real verification, for unknown usernames.</summary>
    public void VerifyDummy(string password) => Verify(password, _dummy.Value);

    public static string? ValidatePolicy(string? password)
    {
        if (password is null || password.Length < MinLength)
        {
            return $"Password must be at least {MinLength} characters.";
        }

        return password.Length > MaxLength ? $"Password must be at most {MaxLength} characters." : null;
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKib, int iterations, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(HashBytes);
    }

    private static byte[] FromB64(string s)
    {
        var padded = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
