// MetaDecryptor.cs
// The game ships its meta database encrypted with SQLite3 Multiple Ciphers
// (SQLCipher-compatible, cipher 3). The importer needs a plain SQLite file,
// so this step turns "<...>/Persistent/meta" into "<...>/meta.decrypted".
//
// Behaviour:
//   * already plaintext -> returned unchanged, nothing copied
//   * encrypted         -> decrypted once into "<meta>.decrypted", reused on the
//                          next run while it is newer than the source

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UmaMusumeLoadSqlData.Encryption
{
    public static class MetaDecryptor
    {
        private const string PlainSqliteHeader = "SQLite format 3\0";
        private const int FlushEveryRows = 5000;

        /// <summary>True when the file starts with the SQLite header (i.e. not encrypted).</summary>
        public static bool IsPlaintext(string filepath)
        {
            using FileStream stream = File.OpenRead(filepath);

            byte[] header = new byte[16];
            int read = stream.Read(header, 0, header.Length);
            if (read < header.Length) return false;

            return Encoding.ASCII.GetString(header) == PlainSqliteHeader;
        }

        /// <summary>
        /// Returns a plaintext SQLite path for the given meta file, decrypting it first if needed.
        /// </summary>
        public static string EnsurePlaintext(string metaPath, UmaRegion region, string customKeyHex = null,
            bool reuseExisting = true, bool verbose = false)
        {
            if (IsPlaintext(metaPath))
            {
                if (verbose) Console.WriteLine($"The \"{Path.GetFileName(metaPath)}\" file is already plaintext SQLite; decryption skipped.");
                return metaPath;
            }

            if (Sqlite3MC.LibraryVersion() == null)
            {
                throw new InvalidOperationException(
                    "sqlite3mc_x64.dll was not found next to the executable, so the encrypted meta cannot be read. " +
                    "Rebuild the project (the native library is copied from lib\\ by the csproj).");
            }

            string outputPath = Path.GetFullPath(metaPath) + ".decrypted";

            if (reuseExisting && File.Exists(outputPath) &&
                File.GetLastWriteTimeUtc(outputPath) >= File.GetLastWriteTimeUtc(metaPath))
            {
                if (verbose) Console.WriteLine($"Reusing the existing decrypted copy: {outputPath}");
                return outputPath;
            }

            byte[] key = string.IsNullOrWhiteSpace(customKeyHex)
                ? EncryptionKey.ForRegion(region)
                : EncryptionKey.FromHex(customKeyHex);

            Decrypt(metaPath, outputPath, key, region, verbose);
            return outputPath;
        }

        /// <summary>
        /// Exports the per-resource key table as "&lt;hash&gt;\t&lt;key&gt;" lines.
        ///
        /// The game's meta has an "e" column holding the key used to decrypt each asset bundle.
        /// A database rebuilt by UmaViewer has that column but leaves it at 0, and upstream
        /// UmaViewer has no way to fill it back in -- hence this export, which feeds exactly the
        /// format UmaViewer's key sync expects.
        ///
        /// Returns the number of rows written; 0 means the column is absent or empty and no file
        /// is produced.
        /// </summary>
        public static int ExportKeys(string plaintextMetaPath, string outputPath, bool verbose = false)
        {
            if (!File.Exists(plaintextMetaPath))
            {
                throw new FileNotFoundException($"meta not found: {plaintextMetaPath}");
            }

            using SQLiteConnection connection = new($"Data Source={plaintextMetaPath};Version=3;Read Only=True;Pooling=False;");
            connection.Open();

            using (SQLiteCommand probe = new("SELECT COUNT(*) FROM pragma_table_info('a') WHERE name='e'", connection))
            {
                if (Convert.ToInt64(probe.ExecuteScalar() ?? 0L) == 0)
                {
                    if (verbose)
                    {
                        Console.WriteLine("This meta has no 'e' column (it is a rebuilt database), so no keys were exported.");
                    }

                    return 0;
                }
            }

            int written = 0;

            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using StreamWriter writer = new(outputPath, false, new UTF8Encoding(false)) { NewLine = "\n" };
            using SQLiteCommand command = new("SELECT h, e FROM a WHERE e IS NOT NULL AND e <> 0", connection);
            using SQLiteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;

                string hash = reader.GetString(0);
                long key = reader.GetInt64(1);

                if (string.IsNullOrEmpty(hash) || key == 0) continue;

                writer.Write(hash);
                writer.Write('\t');
                writer.Write(key);
                writer.Write('\n');
                written++;
            }

            if (verbose)
            {
                Console.WriteLine($"Exported {written} resource key(s) -> {Path.GetFullPath(outputPath)}");
            }

            return written;
        }

        public static void Decrypt(string encryptedPath, string outputPath, byte[] key, UmaRegion region, bool verbose = false)
        {
            if (!File.Exists(encryptedPath))
            {
                throw new FileNotFoundException($"encrypted meta not found: {encryptedPath}");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            Console.WriteLine($"Decrypting \"{Path.GetFileName(encryptedPath)}\" ({new FileInfo(encryptedPath).Length / 1048576.0:F1} MB, region {region})...");

            if (Sqlite3MC.LibraryVersion() == null)
            {
                throw new InvalidOperationException("sqlite3mc_x64.dll could not be loaded.");
            }

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            IntPtr encryptedDb = IntPtr.Zero;

            try
            {
                encryptedDb = Sqlite3MC.OpenReadOnly(encryptedPath);
                Sqlite3MC.SetCipher(encryptedDb, 3);

                int rc = Sqlite3MC.SetKey(encryptedDb, key);
                if (rc != Sqlite3MC.SQLITE_OK)
                {
                    throw new InvalidOperationException($"sqlite3_key failed rc={rc}: {Sqlite3MC.GetErrMsg(encryptedDb)}");
                }

                if (!Sqlite3MC.ValidateReadable(encryptedDb, out string validationError))
                {
                    throw new InvalidOperationException(
                        $"the meta key did not open the database ({validationError}). " +
                        "The key probably changed with a game update; pass --meta-key <hex> to override it.");
                }

                List<SchemaEntry> tables = [];
                List<SchemaEntry> indexes = [];

                Sqlite3MC.ForEachRow(encryptedDb,
                    "SELECT type, name, sql FROM sqlite_master " +
                    "WHERE type IN ('table', 'index') AND sql IS NOT NULL AND name NOT LIKE 'sqlite_%' " +
                    "ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END, name",
                    stmt =>
                    {
                        string type = Sqlite3MC.ColumnText(stmt, 0);
                        string name = Sqlite3MC.ColumnText(stmt, 1);
                        string sql = Sqlite3MC.ColumnText(stmt, 2);

                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sql)) return;

                        SchemaEntry entry = new() { Name = name, Sql = sql };
                        if (type == "table") tables.Add(entry); else indexes.Add(entry);
                    });

                Console.WriteLine($"Found {tables.Count} table(s) and {indexes.Count} index(es).");

                using (SQLiteConnection plain = new($"Data Source={outputPath};Version=3;Pooling=False;"))
                {
                    plain.Open();

                    ExecutePlain(plain, "PRAGMA journal_mode = OFF;");
                    ExecutePlain(plain, "PRAGMA synchronous = OFF;");
                    ExecutePlain(plain, "PRAGMA cache_size = -200000;");

                    using (SQLiteTransaction transaction = plain.BeginTransaction())
                    {
                        foreach (SchemaEntry table in tables)
                        {
                            ExecutePlain(plain, table.Sql, transaction, quiet: true);
                        }

                        long totalRows = 0;

                        foreach (SchemaEntry table in tables)
                        {
                            int rows = CopyRows(encryptedDb, plain, transaction, table.Name, verbose);
                            totalRows += rows;

                            if (verbose)
                            {
                                Console.WriteLine($"  {table.Name}: {rows} row(s)");
                            }
                        }

                        foreach (SchemaEntry index in indexes)
                        {
                            ExecutePlain(plain, index.Sql, transaction, quiet: true);
                        }

                        transaction.Commit();
                        Console.WriteLine($"Copied {totalRows} row(s) into \"{Path.GetFileName(outputPath)}\".");
                    }
                }
            }
            finally
            {
                Sqlite3MC.Close(encryptedDb);
            }

            stopwatch.Stop();
            Console.WriteLine($"Decryption finished in {stopwatch.Elapsed.TotalSeconds:F1}s -> {outputPath}");
        }

        private static int CopyRows(IntPtr encryptedDb, SQLiteConnection plain, SQLiteTransaction transaction,
            string tableName, bool verbose)
        {
            string quotedTable = Quote(tableName);
            int rowCount = 0;
            SQLiteCommand insert = null;

            try
            {
                Sqlite3MC.ForEachRow(encryptedDb, $"SELECT * FROM {quotedTable}", stmt =>
                {
                    int columnCount = Sqlite3MC.ColumnCount(stmt);

                    if (insert == null)
                    {
                        List<string> columnNames = [];
                        List<string> parameterNames = [];

                        for (int i = 0; i < columnCount; i++)
                        {
                            columnNames.Add(Quote(Sqlite3MC.ColumnName(stmt, i) ?? $"column_{i}"));
                            parameterNames.Add($"@p{i}");
                        }

                        string insertSql =
                            $"INSERT INTO {quotedTable} ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", parameterNames)});";

                        insert = new SQLiteCommand(insertSql, plain, transaction);

                        for (int i = 0; i < columnCount; i++)
                        {
                            insert.Parameters.Add(new SQLiteParameter($"@p{i}"));
                        }
                    }

                    for (int i = 0; i < columnCount; i++)
                    {
                        insert.Parameters[i].Value = ReadValue(stmt, i);
                    }

                    insert.ExecuteNonQuery();
                    rowCount++;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WARNING: failed to copy table \"{tableName}\" ({ex.Message})");
            }
            finally
            {
                insert?.Dispose();
            }

            return rowCount;
        }

        private static object ReadValue(IntPtr stmt, int column)
        {
            return Sqlite3MC.ColumnType(stmt, column) switch
            {
                Sqlite3MC.SQLITE_INTEGER => Sqlite3MC.ColumnInt64(stmt, column),
                Sqlite3MC.SQLITE_FLOAT => Sqlite3MC.ColumnDouble(stmt, column),
                Sqlite3MC.SQLITE_TEXT => Sqlite3MC.ColumnText(stmt, column) ?? (object)DBNull.Value,
                Sqlite3MC.SQLITE_BLOB => Sqlite3MC.ColumnBlob(stmt, column) ?? (object)DBNull.Value,
                _ => DBNull.Value
            };
        }

        private static void ExecutePlain(SQLiteConnection connection, string sql,
            SQLiteTransaction transaction = null, bool quiet = false)
        {
            try
            {
                using SQLiteCommand command = new(sql, connection, transaction);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    Console.WriteLine($"  WARNING: {ex.Message} SQL: {sql}");
                }
            }
        }

        private static string Quote(string identifier) => "\"" + (identifier ?? string.Empty).Replace("\"", "\"\"") + "\"";

        private sealed class SchemaEntry
        {
            public string Name { get; set; }
            public string Sql { get; set; }
        }
    }
}
