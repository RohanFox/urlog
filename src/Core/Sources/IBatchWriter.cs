namespace Urlog.Core.Sources;

/// <summary>
/// Columnar row sink a plugin writes into. One row at a time: <c>Append*</c> sets a column of the pending row (the
/// last write wins), <see cref="CommitRow"/> finishes it. Columns not written get their default: 0, empty string,
/// <c>::</c> for addresses, <c>host</c> = <see cref="ParseContext.DefaultHost"/>. Core fills <c>case_id</c>,
/// <c>host_id</c>, <c>file_id</c> and <c>parse_generation</c>; a plugin writes <c>file_offset</c>, <c>ts</c>,
/// <c>raw</c> (byte-for-byte), the message as a slice of it (<see cref="AppendMessage"/>) and whatever its
/// <see cref="FieldSchema"/> declares.
/// Every method is allocation-free; the real writer flushes a full batch to the insert path by itself.
/// Wrong column kinds throw: that is a plugin bug, not bad input.
/// </summary>
public interface IBatchWriter
{
    /// <summary>Rows committed into the current batch (resets at flush).</summary>
    int RowCount { get; }

    void AppendUInt64(EventColumn column, ulong value);

    void AppendUInt32(EventColumn column, uint value);

    void AppendUInt16(EventColumn column, ushort value);

    /// <summary>UTC timestamp in milliseconds since the Unix epoch (<c>DateTime64(3)</c>).</summary>
    void AppendTimestamp(long unixMilliseconds);

    /// <summary>
    /// UTF-8 bytes for a string column. LowCardinality columns (<c>host</c>, <c>program</c>, <c>severity</c>,
    /// <c>event_action</c>, <c>event_outcome</c>, <c>process_name</c>) are ASCII-lowercased on the way in, so
    /// <c>field=value</c> is a direct equality at query time.
    /// </summary>
    void AppendString(EventColumn column, ReadOnlySpan<byte> utf8);

    /// <summary>
    /// The message of the pending row as the byte range <c>[offset, offset + length)</c> of its <c>raw</c>: the table
    /// stores the two numbers and exposes <c>message</c> as an alias over <c>raw</c>. A source whose message is
    /// not a contiguous slice of the record (a rendered EVTX event, JSON with escapes) writes
    /// <c>AppendString(EventColumn.MessageText, ...)</c> instead; writing both, or a slice outside the row's raw, is a
    /// plugin bug and throws at <see cref="CommitRow"/>.
    /// </summary>
    void AppendMessage(int offset, int length);

    /// <summary>4 bytes (IPv4, stored as <c>::ffff:a.b.c.d</c>) or 16 bytes (IPv6), network order.</summary>
    void AppendIp(EventColumn column, ReadOnlySpan<byte> address);

    /// <summary>One <c>extras</c> entry; keys the field catalog does not know go here, never to new columns.</summary>
    void AppendExtra(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);

    void CommitRow();
}
