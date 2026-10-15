using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace DustPlanner.Store;

/// <summary>
/// SQLite database (WAL) holding everything DustPlanner remembers. One writer at a time; readers (MCP server, overlay)
/// can open their own instances. Schema migrations are embedded SQL scripts applied in order.
/// </summary>
public sealed class DustStore : IDisposable
{
    private readonly SqliteConnection _connection;

    private DustStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static DustStore Open(string path, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 10,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        var store = new DustStore(connection);
        if (!readOnly)
        {
            store.Execute("PRAGMA journal_mode=WAL;");
            store.Execute("PRAGMA foreign_keys=ON;");
            store.Migrate();
        }
        else
        {
            store.Execute("PRAGMA foreign_keys=ON;");
        }

        return store;
    }

    /// <summary>In-memory database for tests.</summary>
    public static DustStore OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var store = new DustStore(connection);
        store.Execute("PRAGMA foreign_keys=ON;");
        store.Migrate();
        return store;
    }

    public SqliteConnection Connection => _connection;

    public int SchemaVersion
    {
        get
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migration";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public void Dispose() => _connection.Dispose();

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        return cmd.ExecuteScalar();
    }

    public SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    public SqliteTransaction BeginTransaction() => _connection.BeginTransaction();

    private void Migrate()
    {
        Execute("CREATE TABLE IF NOT EXISTS schema_migration (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);");
        var current = SchemaVersion;
        var assembly = typeof(DustStore).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Name: n, Version: ParseVersion(n)))
            .Where(s => s.Version > current)
            .OrderBy(s => s.Version)
            .ToList();

        foreach (var (name, version) in scripts)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            using var tx = _connection.BeginTransaction();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO schema_migration(version, applied_at) VALUES ($v, $t)";
                cmd.Parameters.AddWithValue("$v", version);
                cmd.Parameters.AddWithValue("$t", Iso(DateTimeOffset.UtcNow));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private static int ParseVersion(string resourceName)
    {
        // DustPlanner.Store.Migrations.001_initial.sql → 1
        var file = resourceName.Split('.').Reverse().Skip(1).First(); // "001_initial"
        return int.Parse(file.Split('_')[0], CultureInfo.InvariantCulture);
    }

    public static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseIso(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
