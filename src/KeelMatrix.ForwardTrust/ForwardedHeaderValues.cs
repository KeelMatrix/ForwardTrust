using System.Net;
using Microsoft.AspNetCore.Http;

namespace KeelMatrix.ForwardTrust;

// Use the same comma/quote parsing as ForwardedHeadersMiddleware while retaining
// byte-for-byte surroundings when replacing one candidate value.
internal static class ForwardedHeaderValues
{
    internal static IReadOnlyList<Token> Parse(string header)
    {
        var headers = new HeaderDictionary { ["value"] = header };
        var values = headers.GetCommaSeparatedValues("value");
        var tokens = new List<Token>(values.Length);
        var offset = 0;
        foreach (var value in values)
        {
            if (value.Length == 0) { continue; }
            var start = header.IndexOf(value, offset, StringComparison.Ordinal);
            tokens.Add(new Token(value, start, value.Length));
            offset = start + value.Length;
        }
        return tokens;
    }

    internal static bool Matches(ForwardTrustDimension dimension, ForwardedIdentity identity, Token token) => dimension switch
    {
        ForwardTrustDimension.Scheme => string.Equals(identity.Scheme, token.Value, StringComparison.OrdinalIgnoreCase),
        ForwardTrustDimension.Host => HostMatches(identity.Host, token.Value),
        ForwardTrustDimension.ClientAddress => IPEndPoint.TryParse(token.Value, out var endpoint)
            && identity.ClientAddress.MapToIPv6().Equals(endpoint.Address.MapToIPv6()),
        _ => false
    };

    private static bool HostMatches(string? expected, string value)
    {
        try { return string.Equals(expected, HostString.FromUriComponent(value).ToString(), StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }

    internal sealed record Token(string Value, int Offset, int Length)
    {
        internal string Replace(string header, string replacement) => string.Concat(header.AsSpan(0, Offset), replacement, header.AsSpan(Offset + Length));
    }
}
