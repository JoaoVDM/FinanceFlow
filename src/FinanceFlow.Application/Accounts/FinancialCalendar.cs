namespace FinanceFlow.Application.Accounts;

public sealed class FinancialCalendar
{
    private readonly TimeZoneInfo zone;
    public FinancialCalendar()
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById("America/Sao_Paulo", out var resolved)) zone = resolved;
        else if (TimeZoneInfo.TryConvertIanaIdToWindowsId("America/Sao_Paulo", out var windowsId)
            && TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out resolved)) zone = resolved;
        else throw new InvalidOperationException("The financial time zone is unavailable.");
    }

    public DateOnly DateAt(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
