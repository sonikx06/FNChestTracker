using System.Security.Cryptography;

namespace ChestTracker.Protocol;

/// <summary>
/// Invitation codes: 8 characters from an unambiguous 30-letter alphabet (no 0/O, 1/I/L, U),
/// about 39 bits of entropy from a CSPRNG, displayed as XXXX-XXXX. The server additionally
/// expires codes and rate-limits join attempts.
/// </summary>
public static class InviteCodes
{
    public const int Length = 8;
    public const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>Uppercases and strips separators; returns null when the input cannot be a code.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var cleaned = new string(input.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());
        return cleaned.Length == Length && cleaned.All(c => Alphabet.Contains(c)) ? cleaned : null;
    }

    public static string Format(string code) =>
        code.Length == Length ? $"{code[..4]}-{code[4..]}" : code;
}
