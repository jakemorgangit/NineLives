using System.Data;
using Microsoft.Data.SqlClient;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

public class SqlServerService
{
    private readonly CredentialStore _credentialStore;

    public SqlServerService(CredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    public string BuildConnectionString(ServerConnection server)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server.ServerName,
            ConnectTimeout = server.ConnectionTimeoutSeconds,
            TrustServerCertificate = server.TrustServerCertificate,
            Encrypt = server.Encrypt switch
            {
                EncryptMode.Yes => SqlConnectionEncryptOption.Mandatory,
                EncryptMode.Strict => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Optional
            },
            ApplicationName = "Nine Lives",
            MultipleActiveResultSets = false
        };

        if (server.AuthMode == AuthMode.WindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = server.Username;
            builder.Password = _credentialStore.GetSqlPassword(server);
        }

        return builder.ConnectionString;
    }

    public async Task<bool> TestConnectionAsync(ServerConnection server, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(BuildConnectionString(server));
        await conn.OpenAsync(ct);
        return conn.State == ConnectionState.Open;
    }

    public async Task<string> GetServerVersionAsync(ServerConnection server, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(BuildConnectionString(server));
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @@VERSION";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString() ?? "Unknown";
    }

    public async Task<List<string>> GetDatabaseListAsync(ServerConnection server, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(BuildConnectionString(server));
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.databases ORDER BY name";
        var databases = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            databases.Add(reader.GetString(0));
        return databases;
    }

    public async Task<List<BackupFileInfo>> RestoreHeaderOnlyAsync(
        ServerConnection server, string blobUrl, string credentialName, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(BuildConnectionString(server));
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = $"RESTORE HEADERONLY FROM URL = N'{blobUrl.Replace("'", "''")}' WITH CREDENTIAL = N'{credentialName.Replace("'", "''")}'";

        var results = new List<BackupFileInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int? backupTypeCode = GetIntFromReader(reader, "BackupType");
            results.Add(new BackupFileInfo
            {
                DatabaseName = GetStringFromReader(reader, "DatabaseName"),
                BackupStartDate = GetDateTimeFromReader(reader, "BackupStartDate"),
                BackupFinishDate = GetDateTimeFromReader(reader, "BackupFinishDate"),
                BackupTypeCode = backupTypeCode,
                Type = (backupTypeCode ?? 0) switch
                {
                    1 => BackupType.Full,
                    5 => BackupType.Differential,
                    2 => BackupType.TransactionLog,
                    _ => BackupType.Unknown
                },
                FirstLsn = GetDecimalFromReader(reader, "FirstLSN"),
                LastLsn = GetDecimalFromReader(reader, "LastLSN"),
                DatabaseBackupLsn = GetDecimalFromReader(reader, "DatabaseBackupLSN")
            });
        }
        return results;
    }

    public async Task<List<FileMoveOption>> RestoreFileListOnlyAsync(
        ServerConnection server, string blobUrl, string credentialName, CancellationToken ct = default)
    {
        return await RestoreFileListOnlyAsync(server, [blobUrl], credentialName, urlsContainSas: false, ct);
    }

    /// <summary>RESTORE FILELISTONLY for striped backup. When urlsContainSas is true, omit WITH CREDENTIAL (SQL Server does not allow WITH CREDENTIAL for SAS credentials).</summary>
    public async Task<List<FileMoveOption>> RestoreFileListOnlyAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, string? credentialName, bool urlsContainSas = false, CancellationToken ct = default)
    {
        if (blobUrls.Count == 0) return [];

        await using var conn = new SqlConnection(BuildConnectionString(server));
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;

        var urlClauses = string.Join(", ", blobUrls.Select(u => $"URL = N'{u.Replace("'", "''")}'"));
        var withCredential = !urlsContainSas && !string.IsNullOrEmpty(credentialName)
            ? $" WITH CREDENTIAL = N'{credentialName!.Replace("'", "''")}'"
            : "";
        cmd.CommandText = $"RESTORE FILELISTONLY FROM {urlClauses}{withCredential}";

        var files = new List<FileMoveOption>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            files.Add(new FileMoveOption
            {
                LogicalName = reader.GetString(reader.GetOrdinal("LogicalName")),
                PhysicalName = reader.GetString(reader.GetOrdinal("PhysicalName")),
                Type = reader.GetString(reader.GetOrdinal("Type")),
                NewPhysicalName = reader.GetString(reader.GetOrdinal("PhysicalName"))
            });
        }
        return files;
    }

    /// <summary>RESTORE HEADERONLY for striped backup. When urlsContainSas is true, omit WITH CREDENTIAL (SQL Server does not allow WITH CREDENTIAL for SAS credentials).</summary>
    public async Task<BackupFileInfo?> RestoreHeaderOnlyMultiAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, string? credentialName, bool urlsContainSas = false, CancellationToken ct = default)
    {
        if (blobUrls.Count == 0) return null;

        await using var conn = new SqlConnection(BuildConnectionString(server));
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;

        var urlClauses = string.Join(", ", blobUrls.Select(u => $"URL = N'{u.Replace("'", "''")}'"));
        var withCredential = !urlsContainSas && !string.IsNullOrEmpty(credentialName)
            ? $" WITH CREDENTIAL = N'{credentialName!.Replace("'", "''")}'"
            : "";
        cmd.CommandText = $"RESTORE HEADERONLY FROM {urlClauses}{withCredential}";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        int? backupTypeCode = GetIntFromReader(reader, "BackupType");
        return new BackupFileInfo
        {
            DatabaseName = GetStringFromReader(reader, "DatabaseName"),
            BackupStartDate = GetDateTimeFromReader(reader, "BackupStartDate"),
            BackupFinishDate = GetDateTimeFromReader(reader, "BackupFinishDate"),
            BackupTypeCode = backupTypeCode,
            Type = (backupTypeCode ?? 0) switch
            {
                1 => BackupType.Full,
                5 => BackupType.Differential,
                2 => BackupType.TransactionLog,
                _ => BackupType.Unknown
            },
            FirstLsn = GetDecimalFromReader(reader, "FirstLSN"),
            LastLsn = GetDecimalFromReader(reader, "LastLSN"),
            DatabaseBackupLsn = GetDecimalFromReader(reader, "DatabaseBackupLSN")
        };
    }

    private static string? GetStringFromReader(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime? GetDateTimeFromReader(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.DateTime,
            _ when value is IConvertible c => c.ToDateTime(null),
            _ => null
        };
    }

    private static decimal? GetDecimalFromReader(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            decimal d => d,
            long l => l,
            double db => (decimal)db,
            _ when value is IConvertible c => c.ToDecimal(null),
            _ => null
        };
    }

    /// <summary>RESTORE HEADERONLY returns BackupType as tinyint/smallint; avoid invalid cast.</summary>
    private static int? GetIntFromReader(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            byte b => b,
            short s => s,
            int i => i,
            long l => (int)l,
            _ when value is IConvertible c => c.ToInt32(null),
            _ => null
        };
    }

    public async Task ExecuteNonQueryAsync(
        ServerConnection server, string sql,
        Action<string>? messageCallback = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(BuildConnectionString(server));
        if (messageCallback != null)
        {
            conn.InfoMessage += (_, e) => messageCallback(e.Message);
        }
        conn.FireInfoMessageEventOnUserErrors = true;
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ExecuteRestoreWithProgressAsync(
        ServerConnection server, string sql,
        Action<string>? messageCallback = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(BuildConnectionString(server));
        if (messageCallback != null)
        {
            conn.InfoMessage += (_, e) => messageCallback(e.Message);
        }
        conn.FireInfoMessageEventOnUserErrors = true;
        await conn.OpenAsync(ct);

        var statements = SplitGoStatements(sql);
        foreach (var statement in statements)
        {
            if (string.IsNullOrWhiteSpace(statement)) continue;
            ct.ThrowIfCancellationRequested();

            messageCallback?.Invoke($"Executing: {statement[..Math.Min(80, statement.Length)].Trim()}...");

            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Checks if a credential with the given name exists on the server and has identity SHARED ACCESS SIGNATURE (for blob URL restores).</summary>
    public async Task<(bool Exists, bool IsSharedAccessSignature)> CredentialExistsAsync(
        ServerConnection server, string credentialName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credentialName))
            return (false, false);

        await using var conn = new SqlConnection(BuildConnectionString(server));
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT credential_identity
            FROM sys.credentials
            WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", credentialName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (false, false);

        var identity = reader["credential_identity"]?.ToString() ?? "";
        var isSas = identity.Equals("SHARED ACCESS SIGNATURE", StringComparison.OrdinalIgnoreCase);
        return (true, isSas);
    }

    public async Task EnsureCredentialExistsAsync(
        ServerConnection server, string credentialName, string storageAccountUrl, string sasToken,
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(BuildConnectionString(server));
        await conn.OpenAsync(ct);

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM sys.credentials WHERE name = @name";
        checkCmd.Parameters.AddWithValue("@name", credentialName);
        var exists = (int)(await checkCmd.ExecuteScalarAsync(ct))! > 0;

        if (exists)
        {
            await using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText = $"DROP CREDENTIAL [{credentialName}]";
            await dropCmd.ExecuteNonQueryAsync(ct);
        }

        var cleanSas = sasToken.TrimStart('?');
        await using var createCmd = conn.CreateCommand();
        createCmd.CommandText = $@"
            CREATE CREDENTIAL [{credentialName}]
            WITH IDENTITY = 'SHARED ACCESS SIGNATURE',
            SECRET = '{cleanSas.Replace("'", "''")}'";
        await createCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string DataPath, string LogPath)> GetDefaultPathsAsync(
        ServerConnection server, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(BuildConnectionString(server));
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT
            SERVERPROPERTY('InstanceDefaultDataPath') AS DefaultDataPath,
            SERVERPROPERTY('InstanceDefaultLogPath') AS DefaultLogPath";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var dataPath = reader["DefaultDataPath"]?.ToString() ?? string.Empty;
            var logPath = reader["DefaultLogPath"]?.ToString() ?? string.Empty;
            return (dataPath.TrimEnd('\\'), logPath.TrimEnd('\\'));
        }
        return (string.Empty, string.Empty);
    }

    private static List<string> SplitGoStatements(string sql)
    {
        var statements = new List<string>();
        var lines = sql.Split('\n');
        var current = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (current.Length > 0)
                {
                    statements.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.AppendLine(line);
            }
        }

        if (current.Length > 0)
            statements.Add(current.ToString());

        return statements;
    }
}
