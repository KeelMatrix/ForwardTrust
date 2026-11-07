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
            var start = header.IndexOf(value, offset, StringComparison.Ordinal);
            tokens.Add(new Token(value, start, value.Length));
            offset = start + value.Length;
        }
        return tokens;
    }

    // Compare logical tokens using framework quote/serialization semantics, but
    // never normalize endpoints or discard entries to infer a consumed count.
    internal static Token? ConsumedBoundary(string original, ForwardedIdentity observed)
    {
        if (!observed.HasForwardedForObservation) { return null; }
        var originalTokens = Parse(original);
        var remainder = observed.RemainingForwardedFor;
        if (remainder is not null && (string.IsNullOrWhiteSpace(remainder) || remainder.Any(char.IsControl))) { return null; }
        var remainingTokens = remainder is null ? [] : Parse(remainder);
        var boundary = remainingTokens.Count;
        if (boundary >= originalTokens.Count) { return null; }
        for (var index = 0; index < boundary; index++)
        {
            if (!string.Equals(originalTokens[index].Value, remainingTokens[index].Value, StringComparison.Ordinal)) { return null; }
        }
        for (var index = boundary; index < originalTokens.Count; index++)
        {
            if (!IPEndPoint.TryParse(originalTokens[index].Value, out _)) { return null; }
        }
        return Matches(ForwardTrustDimension.ClientAddress, observed, originalTokens[boundary]) ? originalTokens[boundary] : null;
    }

    internal static bool Matches(ForwardTrustDimension dimension, ForwardedIdentity identity, Token token) => dimension switch
    {
        ForwardTrustDimension.Scheme => string.Equals(identity.Scheme, token.Value, StringComparison.OrdinalIgnoreCase),
        ForwardTrustDimension.Host => TryNormalizeHost(token.Value, out var host)
            && string.Equals(identity.Host, host, StringComparison.OrdinalIgnoreCase),
        ForwardTrustDimension.ClientAddress => IPEndPoint.TryParse(token.Value, out var endpoint)
            && identity.ClientAddress.MapToIPv6().Equals(endpoint.Address.MapToIPv6()),
        _ => false
    };

    private static bool TryNormalizeHost(string value, out string? host)
    {
        try
        {
            host = HostString.FromUriComponent(value).ToString();
            return true;
        }
        // Framework IDN decoding and encoding can both reject input. Exclude only
        // this token so malformed siblings cannot hide a valid effective-hop witness.
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            host = null;
            return false;
        }
    }

    internal sealed record Token(string Value, int Offset, int Length)
    {
        internal string Replace(string header, string replacement) => string.Concat(header.AsSpan(0, Offset), replacement, header.AsSpan(Offset + Length));
    }
}
