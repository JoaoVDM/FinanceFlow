namespace FinanceFlow.Api.Concurrency;

public static class IfMatchHeader
{
    // Accept only the canonical strong validator emitted by this API for an eight-byte rowversion.
    public static int? Read(IHeaderDictionary headers, out byte[]? version)
    {
        version = null;
        if (!headers.TryGetValue("If-Match", out var values))
        {
            return StatusCodes.Status428PreconditionRequired;
        }

        if (values.Count != 1 || values[0] is not { Length: 14 } value || value[0] != '"' || value[^1] != '"')
        {
            return StatusCodes.Status400BadRequest;
        }

        var encoded = value.AsSpan(1, 12);
        Span<byte> decoded = stackalloc byte[8];
        if (!Convert.TryFromBase64Chars(encoded, decoded, out var written)
            || written != 8 || !encoded.SequenceEqual(Convert.ToBase64String(decoded)))
        {
            return StatusCodes.Status400BadRequest;
        }

        version = decoded.ToArray();
        return null;
    }
}
