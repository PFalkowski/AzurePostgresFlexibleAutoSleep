using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzurePostgresFlexibleAutoSleep.Activity;

public sealed class ActivityCommandInterceptor : DbCommandInterceptor
{
    private readonly IDbActivityTracker _tracker;

    public ActivityCommandInterceptor(IDbActivityTracker tracker) => _tracker = tracker;

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        _tracker.RecordActivity();
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        _tracker.RecordActivity();
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        _tracker.RecordActivity();
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        _tracker.RecordActivity();
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        _tracker.RecordActivity();
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        _tracker.RecordActivity();
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }
}
