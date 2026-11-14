namespace FinanceFlow.Infrastructure.Persistence.Accounts;

public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public byte[] RequestHash { get; set; } = [];
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string ETag { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
