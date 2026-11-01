using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MMOTest.Server;

/// <summary>
/// PBKDF2-HMAC-SHA256 with a random per-password salt. Built into .NET, no extra packages.
/// Stored format: pbkdf2-sha256$iterations$saltBase64$hashBase64
/// The iteration count is stored with each hash so it can be raised later without breaking old accounts.
/// </summary>
public static class PasswordHasher
{
    private const string Algorithm = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 600_000;

    // Caps how many hashes run at once so a burst of logins can't use every core and starve the game loop.
    private static readonly SemaphoreSlim Limiter = new(Math.Max(1, Environment.ProcessorCount / 2));

    /// <summary>Hashes on the thread pool. Use this from gameplay code; it never blocks the main thread.</summary>
    public static async Task<string> HashAsync(string password)
    {
        await Limiter.WaitAsync();
        try { return await Task.Run(() => Hash(password)); }
        finally { Limiter.Release(); }
    }

    /// <summary>Verifies on the thread pool. Use this from gameplay code; it never blocks the main thread.</summary>
    public static async Task<bool> VerifyAsync(string password, string stored)
    {
        await Limiter.WaitAsync();
        try { return await Task.Run(() => Verify(password, stored)); }
        finally { Limiter.Release(); }
    }

    /// <summary>Synchronous. Slow (hundreds of ms); prefer HashAsync except at startup.</summary>
    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Synchronous. Slow (hundreds of ms); prefer VerifyAsync.</summary>
    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;

        string[] parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm || !int.TryParse(parts[1], out int iterations))
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
