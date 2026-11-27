using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence;

internal static class SnapshotReadFailure
{
    public static bool IsUnavailable(Exception exception) => UnavailableSqlException(exception) is not null;

    public static SqlException? UnavailableSqlException(Exception exception)
    {
        if (exception is InvalidOperationException { InnerException: { } strategyFailure })
        {
            exception = strategyFailure;
        }
        if (exception is DbUpdateException { InnerException: { } updateFailure })
        {
            exception = updateFailure;
        }
        // 3952: snapshot isolation is not enabled. Never downgrade to an inconsistent read.
        return exception is SqlException { Number: 1205 or -2 or 3952 } sql ? sql : null;
    }
}
