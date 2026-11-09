using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using MySqlConnector;

using UmaMusumeLoadSqlData.Encryption;
using UmaMusumeLoadSqlData.Models;
using UmaMusumeLoadSqlData.Utilities;

namespace UmaMusumeLoadSqlData
{
    public class Program
    {
        #region Private Class Variables
        private static readonly SqliteUtility _sqliteUtility = new();
        private static readonly MySqlUtility _mySqlUtility = new();
        private static bool _hadBulkInsertError = false;
        #endregion

        #region Private Const Variables
        private const string SOURCE_MASTER_FILENAME = "master/master.mdb";
        private const string SOURCE_META_FILENAME = "meta";
        #endregion

        #region Command Line Arg Class Variables
        public static string AspNetCoreEnvironment { get; private set; }
        private static string _repoName;
        private static string _branchName;
        private static string _mySqlConnectionString;
        public static bool IsVerbose { get; private set; } = false;
        private static string _metaPath;
        private static string _masterPath;
        private static string _metaKeyHex;
        private static UmaRegion _region = UmaRegion.Japan;
        private static bool _forceDecrypt = false;
        private static bool _decryptOnly = false;
        private static string _keysOutPath = "keys.txt";
        private static bool _skipKeys;
        #endregion

        #region Input Path Environment Variables
        private const string META_PATH_ENV = "UMA_META_PATH";
        private const string MASTER_PATH_ENV = "UMA_MASTER_PATH";
        private const string CONNECTION_STRING_ENV = "MARIA_CONNECTION_STRING";
        #endregion

        static void Main(string[] args)
        {
            List<string> positional = ParseArguments(args);

            if (positional.Count >= 4)
            {
                AspNetCoreEnvironment = positional[0];
                _repoName = positional[1];
                _branchName = positional[2];
                _mySqlConnectionString = positional[3];

                if (positional.Count >= 5 && positional[4].ToLower() == "verbose")
                {
                    IsVerbose = true;
                }
            }
            else
            {
                // Zero-argument run: everything comes from the environment.
                AspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
                _repoName = "SimpleSandman/UmaMusumeLoadSqlData";
                _branchName = "master";
                _mySqlConnectionString = Environment.GetEnvironmentVariable(CONNECTION_STRING_ENV);

                if (string.IsNullOrWhiteSpace(_mySqlConnectionString))
                {
                    Console.WriteLine($"WARNING: no connection string (positional arg 4 or {CONNECTION_STRING_ENV}).");
                    Console.WriteLine("         Only the SQLite side will run; nothing is imported.");
                    _mySqlConnectionString = "N/A";
                }
            }

            MainAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Parses the original positional arguments (environment, repo, branch, connection string, verbose)
        /// plus the newer options used to feed in a raw game directory.
        /// </summary>
        private static List<string> ParseArguments(string[] args)
        {
            List<string> positional = [];

            if (args == null) return positional;

            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i].ToLowerInvariant();
                string value = i + 1 < args.Length ? args[i + 1] : null;

                switch (option)
                {
                    case "--meta":
                        _metaPath = RequireValue(args[i], value);
                        i++;
                        break;
                    case "--master":
                        _masterPath = RequireValue(args[i], value);
                        i++;
                        break;
                    case "--meta-key":
                        _metaKeyHex = RequireValue(args[i], value);
                        i++;
                        break;
                    case "--region":
                        _region = (RequireValue(args[i], value)).StartsWith("g", StringComparison.OrdinalIgnoreCase)
                            ? UmaRegion.Global
                            : UmaRegion.Japan;
                        i++;
                        break;
                    case "--force-decrypt":
                        _forceDecrypt = true;
                        break;
                    case "--decrypt-only":
                        _decryptOnly = true;
                        break;
                    case "--keys-out":
                        _keysOutPath = RequireValue(args[i], value);
                        i++;
                        break;
                    case "--no-keys":
                        _skipKeys = true;
                        break;
                    case "--verbose":
                        IsVerbose = true;
                        break;
                    default:
                        positional.Add(args[i]);
                        break;
                }
            }

            return positional;
        }

        private static string RequireValue(string option, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Console.WriteLine($"FATAL ERROR: {option} needs a value.");
                CloseProgram(1);
            }

            return value;
        }

        #region Actual Main Method
        static async Task MainAsync()
        {
            Console.WriteLine($">>> Now running in \"{AspNetCoreEnvironment}\" <<<\n");

            try
            {
                string gameDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "Cygames", "umamusume");

                string metaDbDestinationFilepath = ResolveInputPath(_metaPath, META_PATH_ENV, "meta",
                    Path.Combine(gameDataFolder, "meta"));

                string masterDbDestinationFilepath = ResolveInputPath(_masterPath, MASTER_PATH_ENV, "master.mdb",
                    Path.Combine(gameDataFolder, "master", "master.mdb"));

                /* Verify master.mdb exists */
                if (File.Exists(masterDbDestinationFilepath) && File.Exists(metaDbDestinationFilepath))
                {
                    Console.WriteLine("SUCCESS: Found meta and master.mdb files");
                }
                else
                {
                    Console.WriteLine("FATAL ERROR: Cannot find meta and master.mdb files");
                    Console.WriteLine($"   meta  : {metaDbDestinationFilepath}");
                    Console.WriteLine($"   master: {masterDbDestinationFilepath}");
                    Console.WriteLine($"   Pass --meta <path> / --master <path>, or set {META_PATH_ENV} / {MASTER_PATH_ENV}.");
                    CloseProgram(1);
                    return;
                }

                /* The game ships meta encrypted (SQLite3MC / SQLCipher-compatible); decrypt it first */
                metaDbDestinationFilepath = MetaDecryptor.EnsurePlaintext(
                    metaDbDestinationFilepath, _region, _metaKeyHex, reuseExisting: !_forceDecrypt, verbose: true);

                /* Also dump the per-resource key table: a rebuilt meta leaves the "e" column at 0
                   and can only be filled from this export. */
                if (!_skipKeys)
                {
                    try
                    {
                        int exported = MetaDecryptor.ExportKeys(metaDbDestinationFilepath, Path.GetFullPath(_keysOutPath), verbose: true);

                        if (exported == 0)
                        {
                            Console.WriteLine("   (no resource keys exported; pass --keys-out <path> to change the target)");
                        }
                    }
                    catch (Exception keyError)
                    {
                        Console.WriteLine($"   WARNING: exporting keys.txt failed ({keyError.Message})");
                    }
                }

                if (_decryptOnly)
                {
                    Console.WriteLine($"\nDecrypted meta ready: {metaDbDestinationFilepath}");
                    CloseProgram(0);
                    return;
                }

                await LoadDatabaseData(masterDbDestinationFilepath, SOURCE_MASTER_FILENAME);
                await LoadDatabaseData(metaDbDestinationFilepath, SOURCE_META_FILENAME);

                // Provide special output per error
                if (_hadBulkInsertError)
                {
                    Console.WriteLine("\nWARNING: Table reload successful, but has bulk insert errors.");
                }
                else
                {
                    Console.WriteLine("\nSUCCESS: Table reload successful!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                CloseProgram();
            }
        }
        #endregion

        #region Private Methods
        /// <summary>
        /// Resolves an input file: explicit option &gt; environment variable &gt; live game folder (Development)
        /// &gt; current working directory &gt; live game folder.
        /// </summary>
        private static string ResolveInputPath(string explicitPath, string environmentVariable,
            string localFileName, string gamePath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return Path.GetFullPath(explicitPath);
            }

            string fromEnvironment = Environment.GetEnvironmentVariable(environmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return Path.GetFullPath(fromEnvironment);
            }

            bool development = AspNetCoreEnvironment == "Development" && RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            if (development && File.Exists(gamePath))
            {
                return gamePath;
            }

            string local = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, localFileName));
            if (File.Exists(local))
            {
                return local;
            }

            return gamePath;
        }

        private static async Task LoadDatabaseData(string dbFilepath, string sourceFilename)
        {
            List<SqliteMasterRecord> sqliteMasterTableNames = [];
            List<SqliteMasterRecord> sqliteMasterIndexNames = [];
            List<DataTable> sqliteMasterDataTables = [];

            /* Pull table info from the SQLite master.mdb */
            using (SQLiteConnection connection = new($"Data Source={dbFilepath}"))
            {
                connection.Open();

                // Get raw data table names needed to loop
                SqliteUtility.SqliteTableNames(connection, sqliteMasterTableNames);

                // Get raw data index names needed to loop
                SqliteUtility.SqliteIndexScript(connection, sqliteMasterIndexNames);

                // Loop through each table and pull all available data
                SqliteUtility.LoadSqliteDataTables(connection, sqliteMasterTableNames, sqliteMasterDataTables);
            }

            /* Import data from SQLite into the provided database(s) below */
            if (!string.IsNullOrEmpty(_mySqlConnectionString) && _mySqlConnectionString != "N/A")
            {
                await SqlDestinationAsync<MySqlConnection, MySqlCommand>(sourceFilename, _mySqlConnectionString,
                    sqliteMasterTableNames, sqliteMasterIndexNames, sqliteMasterDataTables).ConfigureAwait(false);
            }

            // Memory clean up
            sqliteMasterTableNames.Clear();
            sqliteMasterIndexNames.Clear();
            sqliteMasterDataTables.Clear();
        }

        private static async Task SqlDestinationAsync<T, U>(string sourceFilename, string connectionString, 
            List<SqliteMasterRecord> sqliteMasterTableNames, List<SqliteMasterRecord> sqliteMasterIndexNames, List<DataTable> sqliteMasterDataTables) 
            where T : IDbConnection, new()
            where U : IDbCommand, new()
        {
            try
            {
                string tableSchema = ""; // TODO: Save for possible future database that requires a schema
                Console.WriteLine($"\nAttempting to load \"{sourceFilename}\" table data into a MySQL/MariaDB database...\n");

                /* Push new info into destination database */
                using T destinationConnection = new();
                destinationConnection.ConnectionString = connectionString;
                destinationConnection.Open();

                // Check if there are any new SQLite tables that need to be created
                string tablePrefix = sourceFilename == SOURCE_META_FILENAME 
                    ? "meta_" 
                    : "";

                List<string> sqlTableNames = SelectTableNames(destinationConnection);
                foreach (string name in sqlTableNames)
                {
                    sqliteMasterTableNames.RemoveAll(t => tablePrefix + t.TableName == name);
                }

                // Alert the owner of any new tables with their indexes
                if (sqliteMasterTableNames.Count > 0)
                {
                    Console.WriteLine($"WARNING: {sqliteMasterTableNames.Count} new table(s) found from the \"{sourceFilename}\" file...");
                    Console.WriteLine("-----------------------------------------------------------");

                    // NOTE: Local dev strings used and displayed
                    string scaffoldContent = "Scaffold-DbContext 'User Id=root;Password=sa;Host=localhost;Database=umamusume;Character Set=utf8mb4' "
                            + $"Pomelo.EntityFrameworkCore.MySql -OutputDir Models -ContextDir Context -T ";

                    foreach (SqliteMasterRecord table in sqliteMasterTableNames)
                    {
                        Console.WriteLine();

                        Console.WriteLine($"{table.SqlScript};");
                        foreach (SqliteMasterRecord index in sqliteMasterIndexNames.Where(i => i.TableName == table.TableName))
                        {
                            Console.WriteLine($"{index.SqlScript};");
                        }

                        scaffoldContent += $"{table.TableName},";
                    }

                    // Script the model creator needed for the API using Scaffold-DbContext
                    Console.WriteLine("\n-----------------------------------------------------------\n");
                    Console.WriteLine(scaffoldContent[0..^1]);
                    Console.WriteLine("\n-----------------------------------------------------------\n");

                    // Remove sqliteDataTables that don't exist in the destination table yet
                    // (they are created below, then loaded like every other table)
                    CreateMissingTables<T, U>(destinationConnection, tablePrefix,
                        sqliteMasterTableNames, sqliteMasterIndexNames);
                }

                foreach (DataTable sqliteDataTable in sqliteMasterDataTables.OrderBy(t => t.TableName))
                {
                    string tableName = $"{tableSchema}{tablePrefix}{sqliteDataTable.TableName}";

                    // Start with a clean slate
                    using (U truncateCommand = new())
                    {
                        truncateCommand.CommandText = $"TRUNCATE TABLE {tableName}";
                        truncateCommand.Connection = destinationConnection;
                        truncateCommand.ExecuteNonQuery();
                    }

                    // Load DataTable into the destination database
                    if (!await TryBulkInsertDataTableAsync(destinationConnection, tableName, sqliteDataTable))
                    {
                        // Look at the SQLite table and find the missing columns for the destination table
                        _hadBulkInsertError = AreMissingColumnsResolved<T, U>(sourceFilename, destinationConnection, sqliteDataTable, tableSchema, tablePrefix);

                        // Try to insert again with the newly added columns
                        if (!await TryBulkInsertDataTableAsync(destinationConnection, tableName, sqliteDataTable, false))
                        {
                            _hadBulkInsertError = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }


        private static string QuoteIdentifier(string name)
        {
            return "`" + (name ?? string.Empty).Replace("`", "``") + "`";
        }

        private static string ShortenIdentifier(string name, int max = 64)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return name.Length <= max ? name : name.Substring(0, max);
        }

        private static readonly Regex CreateTablePattern = new Regex(
            @"^CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:'([^']+)'|\[([^\]]+)\]|`([^`]+)`|([^\s(]+))",
            RegexOptions.IgnoreCase);

        private static readonly Regex CreateIndexPattern = new Regex(
            @"^CREATE\s+(UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:'([^']+)'|\[([^\]]+)\]|`([^`]+)`|([^\s]+))\s+on\s+(?:'([^']+)'|\[([^\]]+)\]|`([^`]+)`|([^\s(]+))\s*\((.*)\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>
        /// Translates the SQLite DDL the tool prints into MariaDB DDL and executes it.
        /// The upstream tool only printed these statements (in SQL Server syntax), so every
        /// schema bump silently dropped the new tables from the import.
        /// </summary>
        private static string ToMySqlCreateTable(string sqliteSql, string newTableName)
        {
            if (string.IsNullOrWhiteSpace(sqliteSql)) return null;

            string sql = sqliteSql.Trim().TrimEnd(';');
            Match match = CreateTablePattern.Match(sql);

            if (!match.Success) return null;

            string rest = sql.Substring(match.Length);

            rest = Regex.Replace(rest, @"'([^']*)'", "`$1`");
            rest = Regex.Replace(rest, @"\[([^\]]*)\]", "`$1`");
            rest = Regex.Replace(rest, @"\bINTEGER\b", "BIGINT", RegexOptions.IgnoreCase);
            rest = Regex.Replace(rest, @"\bREAL\b", "DOUBLE", RegexOptions.IgnoreCase);

            // MariaDB cannot index a TEXT column without a key length, so every column that
            // takes part in PRIMARY KEY / UNIQUE becomes VARCHAR(<budget>) instead.
            List<string> keyColumns = ExtractKeyColumns(rest);

            if (keyColumns.Count > 0)
            {
                int keyLength = Math.Clamp(3072 / (4 * keyColumns.Count), 1, 191);

                rest = Regex.Replace(rest, @"`([^`]+)`(\s+)TEXT\b", column =>
                {
                    string name = column.Groups[1].Value;
                    bool isKeyColumn = keyColumns.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

                    return isKeyColumn
                        ? $"`{name}`{column.Groups[2].Value}VARCHAR({keyLength})"
                        : column.Value;
                }, RegexOptions.IgnoreCase);
            }

            return "CREATE TABLE " + QuoteIdentifier(newTableName) + rest;
        }

        private static List<string> ExtractKeyColumns(string ddl)
        {
            List<string> columns = [];

            foreach (Match match in Regex.Matches(ddl, @"(?:PRIMARY\s+KEY|UNIQUE)\s*\(([^)]*)\)", RegexOptions.IgnoreCase))
            {
                foreach (string raw in match.Groups[1].Value.Split(','))
                {
                    string name = Regex.Split(Regex.Replace(raw.Trim(), @"[`'\[\]]", string.Empty), @"\s+")[0];
                    if (!string.IsNullOrWhiteSpace(name)) columns.Add(name);
                }
            }

            // column-level "`x` TEXT PRIMARY KEY" / "`x` TEXT UNIQUE"
            foreach (Match match in Regex.Matches(ddl, @"`([^`]+)`\s+[A-Za-z]+[^,]*\b(?:PRIMARY\s+KEY|UNIQUE)\b", RegexOptions.IgnoreCase))
            {
                columns.Add(match.Groups[1].Value);
            }

            return columns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ToMySqlCreateIndex(string sqliteSql, string newTableName, string newIndexName,
            Dictionary<string, string> columnTypes)
        {
            if (string.IsNullOrWhiteSpace(sqliteSql)) return null;

            Match match = CreateIndexPattern.Match(sqliteSql.Trim().TrimEnd(';'));

            if (!match.Success) return null;

            bool unique = match.Groups[1].Success;
            string[] rawColumns = SplitTopLevel(match.Groups[10].Value);

            // utf8mb4 = 4 bytes per character and InnoDB allows 3072 bytes per index key.
            int budget = Math.Clamp(3072 / (4 * Math.Max(1, rawColumns.Length)), 1, 191);

            List<string> columns = [];

            foreach (string rawColumn in rawColumns)
            {
                string column = Regex.Replace(rawColumn.Trim(), @"[`'\[\]]", string.Empty);
                column = Regex.Split(column.Trim(), @"\s+")[0];

                if (string.IsNullOrWhiteSpace(column)) continue;

                string type = null;
                columnTypes?.TryGetValue(column, out type);

                // Unknown types are treated as text: a prefix on an integer column is an error,
                // while a missing prefix on a text column is as well. Unknown usually means text.
                bool isText = type == null ||
                              type.StartsWith("TEXT") || type.StartsWith("CHAR") || type.StartsWith("CLOB") ||
                              type.StartsWith("VARCHAR") || type.StartsWith("NVARCHAR");

                columns.Add(isText ? $"`{column}`({budget})" : $"`{column}`");
            }

            if (columns.Count == 0) return null;

            return (unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ") +
                   QuoteIdentifier(newIndexName) + " ON " + QuoteIdentifier(newTableName) +
                   " (" + string.Join(", ", columns) + ")";
        }

        /// <summary>Splits a comma separated list while respecting parentheses (e.g. DECIMAL(10, 2)).</summary>
        private static string[] SplitTopLevel(string value)
        {
            List<string> parts = [];

            if (string.IsNullOrWhiteSpace(value)) return parts.ToArray();

            int depth = 0;
            int start = 0;

            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];

                if (current == '(') depth++;
                else if (current == ')') depth--;
                else if (current == ',' && depth == 0)
                {
                    parts.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }

            parts.Add(value.Substring(start));
            return parts.ToArray();
        }

        /// <summary>Maps column name -> declared SQLite type for a table, used when indexes are created.</summary>
        private static Dictionary<string, string> ParseColumnTypes(string createTableSql)
        {
            Dictionary<string, string> types = new(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(createTableSql)) return types;

            Match body = Regex.Match(createTableSql, @"\((.*)\)\s*$", RegexOptions.Singleline);
            if (!body.Success) return types;

            foreach (string rawPart in SplitTopLevel(body.Groups[1].Value))
            {
                string part = rawPart.Trim();
                if (part.Length == 0) continue;

                string upper = part.ToUpperInvariant();
                if (upper.StartsWith("PRIMARY") || upper.StartsWith("UNIQUE") || upper.StartsWith("CONSTRAINT") ||
                    upper.StartsWith("FOREIGN") || upper.StartsWith("CHECK"))
                {
                    continue;
                }

                Match column = Regex.Match(part, @"^[`'\[\]]?([^`'\[\]\s]+)[`'\[\]]?\s+([A-Za-z]+)");
                if (!column.Success) continue;

                types[column.Groups[1].Value] = column.Groups[2].Value.ToUpperInvariant();
            }

            return types;
        }

        private static string ExtractIndexName(string sqliteSql)
        {
            Match match = new Regex(
                @"^CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:'([^']+)'|\[([^\]]+)\]|`([^`]+)`|([^\s]+))",
                RegexOptions.IgnoreCase).Match(sqliteSql ?? string.Empty);

            if (!match.Success) return "idx";

            for (int i = 1; i <= 4; i++)
            {
                if (match.Groups[i].Success) return match.Groups[i].Value;
            }

            return "idx";
        }

        private static bool ExecuteCommand<U>(IDbConnection connection, string sql, bool quiet = false)
            where U : IDbCommand, new()
        {
            try
            {
                using U command = new();
                command.CommandText = sql;
                command.Connection = connection;
                command.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {(quiet ? "WARNING" : "ERROR")}: {ex.Message}");
                if (!quiet) Console.WriteLine($"  SQL  : {sql}");
                return false;
            }
        }

        private static void CreateMissingTables<T, U>(T destinationConnection, string tablePrefix,
            List<SqliteMasterRecord> tables, List<SqliteMasterRecord> indexes)
            where T : IDbConnection
            where U : IDbCommand, new()
        {
            Console.WriteLine("\nCreating the missing tables in the destination database...\n");

            Dictionary<string, Dictionary<string, string>> columnTypes = tables
                .GroupBy(t => t.TableName)
                .ToDictionary(g => g.Key, g => ParseColumnTypes(g.First().SqlScript));

            foreach (SqliteMasterRecord table in tables)
            {
                string target = tablePrefix + table.TableName;
                string createSql = ToMySqlCreateTable(table.SqlScript, target);

                if (createSql == null)
                {
                    Console.WriteLine($"  WARNING: could not translate the CREATE TABLE for \"{target}\"");
                    _hadBulkInsertError = true;
                    continue;
                }

                if (!ExecuteCommand<U>(destinationConnection, createSql))
                {
                    _hadBulkInsertError = true;
                    continue;
                }

                Console.WriteLine($"  created \"{target}\"");

                foreach (SqliteMasterRecord index in indexes.Where(i => i.TableName == table.TableName))
                {
                    string indexName = ShortenIdentifier(tablePrefix + ExtractIndexName(index.SqlScript));
                    Dictionary<string, string> types = null;
                    columnTypes.TryGetValue(table.TableName, out types);

                    string indexSql = ToMySqlCreateIndex(index.SqlScript, target, indexName, types);
                    if (indexSql == null) continue;

                    ExecuteCommand<U>(destinationConnection, indexSql, quiet: true);
                }
            }

            Console.WriteLine("Missing tables handled.\n");
        }

        private static bool AreMissingColumnsResolved<T, U>(string dbFilepath, T destinationConnection, DataTable sqliteDataTable, string tableSchema, string tablePrefix)
            where T : IDbConnection, new()
            where U : IDbCommand, new()
        {
            try
            {
                using SQLiteConnection sqliteDebugConnection = new($"Data Source={dbFilepath}");
                sqliteDebugConnection.Open();

                List<ColumnMetadata> sqliteColumns = SelectColumnMetadata(sqliteDebugConnection, sqliteDataTable.TableName);
                List<ColumnMetadata> destinationColumns = SelectColumnMetadata(destinationConnection, tablePrefix + sqliteDataTable.TableName);

                if (destinationColumns == null)
                {
                    Console.WriteLine($"WARNING: Missing destination columns for table, \"{sqliteDataTable.TableName}\"");
                    return false;
                }

                // Add missing columns
                foreach (ColumnMetadata missingColumn in sqliteColumns.Where(lite => !destinationColumns.Exists(s => s.ColumnName == lite.ColumnName)))
                {
                    string isNullable = "NOT NULL";
                    if (missingColumn.IsNullable)
                    {
                        isNullable = "NULL";
                    }

                    string addColumn = "";
                    if (missingColumn.ColumnDataType == "INTEGER")
                    {
                        // Reference: https://stackoverflow.com/a/7337945/2113548 (similar issue for SQLite's TEXT)
                        addColumn = $"ALTER TABLE {tableSchema}{tablePrefix}{sqliteDataTable.TableName} ADD {missingColumn.ColumnName} INT {isNullable}";
                    }
                    else if (missingColumn.ColumnDataType == "TEXT")
                    {
                        if (typeof(U) == typeof(MySqlCommand))
                        {
                            // NOTE: Make sure MySQL database is set to "utf8mb4" character set
                            addColumn = $"ALTER TABLE {tableSchema}{tablePrefix}{sqliteDataTable.TableName} ADD {missingColumn.ColumnName} TEXT {isNullable}";
                        }
                        else
                        {
                            addColumn = $"ALTER TABLE {tableSchema}{tablePrefix}{sqliteDataTable.TableName} ADD {missingColumn.ColumnName} NVARCHAR(4000) {isNullable}";
                        }
                    }
                    else
                    {
                        Console.WriteLine($"ERROR: Unable to handle SQLite datatype: {missingColumn.ColumnDataType}");
                        CloseProgram();
                    }

                    // Try to add the missing columns
                    try
                    {
                        using (U addColumnCommand = new())
                        {
                            addColumnCommand.CommandText = addColumn;
                            addColumnCommand.Connection = destinationConnection;
                            addColumnCommand.ExecuteNonQuery();
                        }

                        Console.WriteLine($"\nSuccessfully added missing column with script: \"{addColumn}\"");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\nERROR: {ex.Message}");

                        if (string.IsNullOrEmpty(addColumn))
                        {
                            Console.WriteLine($"SQL Command: {addColumn}");
                        }

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            return false;
        }

        private static List<string> SelectTableNames<T>(T connection) where T : IDbConnection
        {
            try
            {
                if (typeof(T) == typeof(MySqlConnection))
                {
                    return _mySqlUtility.SelectTableNames(connection as MySqlConnection);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            return null;
        }

        private static List<ColumnMetadata> SelectColumnMetadata<T>(T connection, string tableName) where T : IDbConnection
        {
            try
            {
                if (typeof(T) == typeof(MySqlConnection))
                {
                    return _mySqlUtility.SelectColumnMetadata(connection as MySqlConnection, tableName);
                }
                else if (typeof(T) == typeof(SQLiteConnection))
                {
                    return _sqliteUtility.SelectColumnMetadata(connection as SQLiteConnection, tableName);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            return null;
        }

        private static async Task<bool> TryBulkInsertDataTableAsync<T>(T connection, string tableName, DataTable sourceDataTable, bool isFirstAttempt = true)
            where T : IDbConnection
        {
            try
            {
                if (typeof(T) == typeof(MySqlConnection))
                {
                    return await _mySqlUtility.TryBulkInsertDataTableAsync(connection as MySqlConnection, tableName, sourceDataTable, isFirstAttempt);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            return false;
        }
        #endregion

        #region Public Methods
        public static void CloseProgram(int exitCode = 0)
        {
            bool interactive = Environment.UserInteractive && !Console.IsInputRedirected;

            if (AspNetCoreEnvironment == "Development" && RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && interactive)
            {
                Console.WriteLine("\nPress any key to close this program...");

                try
                {
                    Console.ReadKey();
                }
                catch (InvalidOperationException)
                {
                    // no console attached (scheduled task / redirected output)
                }
            }
            else
            {
                Console.WriteLine("\nProgram finished executing.");
                Console.WriteLine("Exiting now...");
            }
            
            Environment.Exit(exitCode);
        }
        #endregion
    }
}
