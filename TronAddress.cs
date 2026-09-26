using System.Security.Cryptography;

namespace TronAutoSweeper;

public static class TronAddress
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static bool IsValidBase58(string address)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(address) || address[0] != 'T') return false;
            var raw = Base58Decode(address);
            if (raw.Length != 25 || raw[0] != 0x41) return false;
            var payload = raw[..21];
            var checksum = raw[21..];
            var hash = SHA256.HashData(SHA256.HashData(payload));
            return checksum.SequenceEqual(hash[..4]);
        }
        catch { return false; }
    }

    private static byte[] Base58Decode(string s)
    {
        var bytes = new List<byte> { 0 };
        foreach (var c in s)
        {
            var carry = Alphabet.IndexOf(c);
            if (carry < 0) throw new FormatException();
            for (int i = 0; i < bytes.Count; i++)
            {
                var value = bytes[i] * 58 + carry;
                bytes[i] = (byte)(value & 0xff);
                carry = value >> 8;
            }
            while (carry > 0) { bytes.Add((byte)(carry & 0xff)); carry >>= 8; }
        }
        var leading = s.TakeWhile(c => c == '1').Count();
        bytes.Reverse();
        return Enumerable.Repeat((byte)0, leading).Concat(bytes.SkipWhile((b, i) => i < 1 && b == 0)).ToArray();
    }
}
