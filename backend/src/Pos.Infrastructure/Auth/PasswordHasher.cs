using System.Security.Cryptography;

namespace Pos.Infrastructure.Auth;

public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 210_000;
    private const string Prefix = "pbkdf2$sha256$";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(hash))
            return false;

        if (!hash.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var parts = hash[Prefix.Length..].Split('$');
        if (parts.Length != 3)
            return false;

        // The iteration count comes out of the stored hash, so a corrupted or tampered row
        // could otherwise ask us to burn arbitrary CPU on every verify attempt. Bound it.
        if (!int.TryParse(parts[0], out var iterations)
            || iterations < 1
            || iterations > 1_000_000)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var computed = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }
}
