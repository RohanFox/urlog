namespace Urlog.Core;

/// <summary>
/// Which event times count toward the span of a file and of a case (design record, "Query language", Time). A
/// span is the default range of a search, so one row with an impossible time would stretch it over decades.
/// <para>
/// Below <see cref="Floor"/> a time is no time: the column default of a rejected or undatable record is the
/// epoch, and a timestamp without a year that was parsed with no year hint lands in 1970. Above the arrival of the
/// file plus <see cref="FutureMargin"/> it is a wrong guess (a year hint one too high) or a wrong clock: nothing
/// is logged after it was collected. The margin covers a collecting machine whose zone was taken for UTC.
/// </para>
/// Rows outside the two bounds are stored and searchable like any other; they are counted, not hidden.
/// </summary>
public static class EventTime
{
    /// <summary>1980-01-01 UTC.</summary>
    public static readonly DateTime Floor = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static readonly TimeSpan FutureMargin = TimeSpan.FromDays(1);

    /// <summary><see cref="Floor"/> as the milliseconds the timestamp column holds.</summary>
    public static long FloorMilliseconds { get; } = new DateTimeOffset(Floor).ToUnixTimeMilliseconds();

    /// <summary>The greatest time, in milliseconds, that counts for a file that arrived at <paramref name="receivedAtUtc"/>.</summary>
    public static long CeilingMilliseconds(DateTime receivedAtUtc) =>
        new DateTimeOffset(DateTime.SpecifyKind(receivedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() + (long)FutureMargin.TotalMilliseconds;
}
