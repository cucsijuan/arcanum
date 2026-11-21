// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using Arcanum.Data.Career;
using Microsoft.Data.Sqlite;

namespace Arcanum.CareerService;

/// <summary>
/// The service's database (SQLite, in the service's state folder): each player's checked career, and the syncs it
/// refused (kept for review). One connection per call; SQLite serializes the writes.
/// </summary>
public sealed class CareerStore
{
    private readonly string _connection;

    public CareerStore(IConfiguration configuration)
    {
        var folder = configuration["Career:DataDir"] ?? Environment.GetEnvironmentVariable("STATE_DIRECTORY") ?? "data";
        Directory.CreateDirectory(folder);
        _connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "career.db"), Pooling = false }.ToString();
        using var db = Open();
        Execute(db, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS careers (player TEXT PRIMARY KEY, data BLOB NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS refused (id INTEGER PRIMARY KEY, player TEXT NOT NULL, at TEXT NOT NULL, reason TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cloud (player TEXT NOT NULL, name TEXT NOT NULL, data BLOB NOT NULL, updated TEXT NOT NULL,
                                              PRIMARY KEY (player, name));
            """);
    }

    public SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    public static void Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(db, sql, values);
        command.ExecuteNonQuery();
    }

    public static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public CheckedCareer? Career(SqliteConnection db, string player)
    {
        using var command = Command(db, "SELECT data FROM careers WHERE player = $p", ("$p", player));
        return command.ExecuteScalar() is byte[] data ? CareerChecker.Read(Unzip(data)) : null;
    }

    public void Save(SqliteConnection db, string player, CheckedCareer career) =>
        Execute(db, "INSERT INTO careers (player, data, updated) VALUES ($p, $d, $u) ON CONFLICT(player) DO UPDATE SET data = $d, updated = $u",
            ("$p", player), ("$d", Zip(CareerChecker.Write(career))), ("$u", DateTime.UtcNow.ToString("O")));

    public void Refused(SqliteConnection db, string player, string reason) =>
        Execute(db, "INSERT INTO refused (player, at, reason) VALUES ($p, $a, $r)", ("$p", player), ("$a", DateTime.UtcNow.ToString("O")), ("$r", reason));

    // ------------------------------------------------------------------ the players' cloud files

    /// <summary>Largest cloud file, and how many a player may keep (a career with all its decks is tens of kilobytes).</summary>
    public const int CloudFileLimit = 1 << 20, CloudFilesPerPlayer = 8;

    /// <summary>A cloud file's name: lower case letters, digits, dots, dashes and underscores (career.zip).</summary>
    public static bool CloudName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9._-]{0,63}$");

    public byte[]? CloudFile(SqliteConnection db, string player, string name)
    {
        using var command = Command(db, "SELECT data FROM cloud WHERE player = $p AND name = $n", ("$p", player), ("$n", name));
        return command.ExecuteScalar() as byte[];
    }

    /// <summary>Keeps the file; false when the player already keeps as many other files as allowed.</summary>
    public bool SaveCloudFile(SqliteConnection db, string player, string name, byte[] data)
    {
        using var transaction = db.BeginTransaction();
        using (var count = Command(db, "SELECT COUNT(*) FROM cloud WHERE player = $p AND name <> $n", ("$p", player), ("$n", name)))
        {
            count.Transaction = transaction;
            if (Convert.ToInt32(count.ExecuteScalar()) >= CloudFilesPerPlayer) return false;
        }
        using (var save = Command(db, "INSERT INTO cloud (player, name, data, updated) VALUES ($p, $n, $d, $u) " +
                   "ON CONFLICT(player, name) DO UPDATE SET data = $d, updated = $u",
                   ("$p", player), ("$n", name), ("$d", data), ("$u", DateTime.UtcNow.ToString("O"))))
        {
            save.Transaction = transaction;
            save.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }

    private static byte[] Zip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal)) gzip.Write(data);
        return output.ToArray();
    }

    private static byte[] Unzip(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
