namespace Urlog.Core.Ingest.Native;

/// <summary>
/// The insert path failed: a server exception (with the server's <see cref="Code"/> and <see cref="Name"/>), a
/// protocol disagreement, or a lost connection. The job that owns the file fails with it; recovery is
/// <c>DELETE WHERE file_id</c> and a full re-parse (design record, "Consistency").
/// </summary>
public sealed class ClickHouseInsertException : Exception
{
    public ClickHouseInsertException()
    {
    }

    public ClickHouseInsertException(string message)
        : base(message)
    {
    }

    public ClickHouseInsertException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ClickHouseInsertException(int code, string name, string serverMessage, Exception? innerException)
        : base($"ClickHouse error {code} {name}: {serverMessage}", innerException)
    {
        Code = code;
        Name = name;
        ServerMessage = serverMessage;
    }

    /// <summary>The server's error code (<c>system.errors</c>), or 0 for a client-side failure.</summary>
    public int Code { get; }

    /// <summary>The exception class the server reports (<c>DB::Exception</c>); the error's identity is <see cref="Code"/> (<c>system.errors</c>: 60 UNKNOWN_TABLE, 164 READONLY, 469 VIOLATED_CONSTRAINT, ...).</summary>
    public string Name { get; } = "";

    public string ServerMessage { get; } = "";

    public bool IsServerError => Code != 0;
}
