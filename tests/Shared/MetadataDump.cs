using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Urlog.Tests.Shared;

/// <summary>
/// Every row of every table of a metadata file as text, read on a read-only connection. Two dumps that are equal
/// mean nothing was written to the file in between: the schema, the migration history, the jobs, the audit chain.
/// A comparison of the file's bytes would not say that, since any connection that closes may checkpoint the WAL.
/// </summary>
internal static class MetadataDump
{
    public static async Task<string> TakeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return "(no file)";
        }

        var text = new StringBuilder();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var tables = new List<string>();
            using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT type, name, coalesce(sql, '') FROM sqlite_master ORDER BY type, name";
                var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        text.Append(reader.GetString(0)).Append(' ').Append(reader.GetString(1)).Append(": ").Append(reader.GetString(2)).Append('\n');
                        if (reader.GetString(0) == "table")
                        {
                            tables.Add(reader.GetString(1));
                        }
                    }
                }
            }

            foreach (var table in tables)
            {
                text.Append("== ").Append(table).Append('\n');
                using var rows = connection.CreateCommand();
                rows.CommandText = $"SELECT * FROM \"{table}\" ORDER BY 1";
                var reader = await rows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            text.Append(reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).Append('|');
                        }

                        text.Append('\n');
                    }
                }
            }
        }

        return text.ToString();
    }
}
