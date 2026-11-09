// Sqlite3MC.cs
// Minimal P/Invoke wrapper around sqlite3mc_x64.dll (SQLite3 Multiple Ciphers),
// which is what the game's encrypted meta database needs. Read-only usage.
//
// The native library ships in lib\sqlite3mc_x64.dll and is copied next to the
// executable by the csproj, so the default DllImport probing finds it.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace UmaMusumeLoadSqlData.Encryption
{
    public static class Sqlite3MC
    {
        private const string DLL = "sqlite3mc_x64";

        public const int SQLITE_OK = 0;
        public const int SQLITE_ROW = 100;
        public const int SQLITE_DONE = 101;

        public const int SQLITE_OPEN_READONLY = 0x00000001;
        public const int SQLITE_OPEN_READWRITE = 0x00000002;
        public const int SQLITE_OPEN_CREATE = 0x00000004;

        public const int SQLITE_INTEGER = 1;
        public const int SQLITE_FLOAT = 2;
        public const int SQLITE_TEXT = 3;
        public const int SQLITE_BLOB = 4;
        public const int SQLITE_NULL = 5;

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_libversion")]
        private static extern IntPtr sqlite3_libversion_ptr();

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
        private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPStr)] string filename,
            out IntPtr ppDb, int flags, IntPtr zVfs);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close")]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errmsg")]
        private static extern IntPtr sqlite3_errmsg_ptr(IntPtr db);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_exec")]
        private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string sql,
            IntPtr callback, IntPtr arg, out IntPtr errMsg);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_free")]
        private static extern void sqlite3_free(IntPtr ptr);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3mc_config")]
        private static extern int sqlite3mc_config(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string paramName, int newValue);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_key")]
        private static extern int sqlite3_key_bytes(IntPtr db, byte[] pKey, int nKey);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
        private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string zSql,
            int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_blob")]
        private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int iCol);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_bytes")]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_double")]
        private static extern double sqlite3_column_double(IntPtr stmt, int iCol);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int64")]
        private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_count")]
        private static extern int sqlite3_column_count(IntPtr stmt);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_name")]
        private static extern IntPtr sqlite3_column_name(IntPtr stmt, int iCol);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_type")]
        private static extern int sqlite3_column_type(IntPtr stmt, int iCol);

        private static string PtrToStringUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;

            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return string.Empty;

            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return Encoding.UTF8.GetString(buffer);
        }

        public static string LibraryVersion()
        {
            try { return PtrToStringUtf8(sqlite3_libversion_ptr()); }
            catch (DllNotFoundException) { return null; }
        }

        /// <summary>Opens an SQLite (optionally encrypted) database, read-only.</summary>
        public static IntPtr OpenReadOnly(string path)
        {
            int rc = sqlite3_open_v2(path, out IntPtr db, SQLITE_OPEN_READONLY, IntPtr.Zero);
            if (rc != SQLITE_OK || db == IntPtr.Zero)
            {
                string message = db != IntPtr.Zero ? GetErrMsg(db) : "(no db handle)";
                throw new InvalidOperationException($"sqlite3_open_v2('{path}') failed rc={rc} errmsg={message}");
            }
            return db;
        }

        public static int Close(IntPtr db) => db == IntPtr.Zero ? SQLITE_OK : sqlite3_close(db);

        public static string GetErrMsg(IntPtr db) => PtrToStringUtf8(sqlite3_errmsg_ptr(db)) ?? "(null errmsg)";

        public static int Exec(IntPtr db, string sql, out string errorMessage)
        {
            errorMessage = null;
            int rc = sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, out IntPtr errPtr);
            if (errPtr != IntPtr.Zero)
            {
                errorMessage = PtrToStringUtf8(errPtr);
                sqlite3_free(errPtr);
            }
            return rc;
        }

        /// <summary>Selects a cipher scheme in sqlite3mc (3 = SQLCipher-compatible).</summary>
        public static int SetCipher(IntPtr db, int cipher) => sqlite3mc_config(db, "cipher", cipher);

        public static int SetKey(IntPtr db, byte[] key) => sqlite3_key_bytes(db, key, key.Length);

        public static bool ValidateReadable(IntPtr db, out string errorMessage)
            => Exec(db, "SELECT name FROM sqlite_master LIMIT 1;", out errorMessage) == SQLITE_OK;

        public delegate void RowCallback(IntPtr stmt);

        public static void ForEachRow(IntPtr db, string sql, RowCallback onRow)
        {
            int rc = sqlite3_prepare_v2(db, sql, -1, out IntPtr stmt, IntPtr.Zero);
            if (rc != SQLITE_OK)
                throw new InvalidOperationException($"prepare failed rc={rc} errmsg={GetErrMsg(db)} sql={sql}");

            try
            {
                while (true)
                {
                    rc = sqlite3_step(stmt);
                    if (rc == SQLITE_ROW)
                    {
                        onRow(stmt);
                    }
                    else if (rc == SQLITE_DONE)
                    {
                        break;
                    }
                    else
                    {
                        throw new InvalidOperationException($"step failed rc={rc} errmsg={GetErrMsg(db)} sql={sql}");
                    }
                }
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }

        public static int ColumnCount(IntPtr stmt) => sqlite3_column_count(stmt);

        public static string ColumnName(IntPtr stmt, int column) => PtrToStringUtf8(sqlite3_column_name(stmt, column));

        public static int ColumnType(IntPtr stmt, int column) => sqlite3_column_type(stmt, column);

        public static string ColumnText(IntPtr stmt, int column) => PtrToStringUtf8(sqlite3_column_text(stmt, column));

        public static long ColumnInt64(IntPtr stmt, int column) => sqlite3_column_int64(stmt, column);

        public static double ColumnDouble(IntPtr stmt, int column) => sqlite3_column_double(stmt, column);

        public static byte[] ColumnBlob(IntPtr stmt, int column)
        {
            IntPtr ptr = sqlite3_column_blob(stmt, column);
            if (ptr == IntPtr.Zero) return null;

            int len = sqlite3_column_bytes(stmt, column);
            if (len <= 0) return [];

            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return buffer;
        }
    }

    public class EncryptionKey
    {
        // The game XORs a per-region key with a fixed base key (first 13 bytes).
        private static readonly byte[] BaseKey =
        [
            0xF1, 0x70, 0xCE, 0xA4, 0xDF, 0xCE, 0xA3, 0xE1, 0xA5, 0xD8, 0xC7, 0x0B, 0xD1
        ];

        private static readonly byte[] JapanKey =
        [
            0x6D, 0x5B, 0x65, 0x33, 0x63, 0x36, 0x63, 0x25, 0x54, 0x71, 0x2D, 0x73, 0x50, 0x53, 0x63, 0x38,
            0x6D, 0x34, 0x37, 0x7B, 0x35, 0x63, 0x70, 0x23, 0x37, 0x34, 0x53, 0x29, 0x73, 0x43, 0x36, 0x33
        ];

        private static readonly byte[] GlobalKey =
        [
            0x36, 0x23, 0x6B, 0x4C, 0x2A, 0x39, 0x21, 0x75, 0x52, 0x26, 0x32, 0x76, 0x25, 0x50, 0x3F, 0x35,
            0x5D, 0x77, 0x58, 0x6D, 0x40, 0x71, 0x38, 0x5E, 0x4C, 0x31, 0x28, 0x74, 0x29, 0x59, 0x37, 0x24,
            0x53
        ];

        public static byte[] ForRegion(UmaRegion region)
        {
            byte[] source = region == UmaRegion.Global ? GlobalKey : JapanKey;
            byte[] finalKey = new byte[source.Length];

            for (int i = 0; i < source.Length; i++)
            {
                finalKey[i] = (byte)(source[i] ^ BaseKey[i % BaseKey.Length]);
            }

            return finalKey;
        }

        /// <summary>Allows overriding the built-in keys (meta key rotation / other regions).</summary>
        public static byte[] FromHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;

            hex = hex.Replace(" ", string.Empty).Replace("-", string.Empty).Trim();
            if (hex.Length % 2 != 0) throw new ArgumentException("key hex must have an even number of characters");

            byte[] raw = new byte[hex.Length / 2];
            for (int i = 0; i < raw.Length; i++)
            {
                raw[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return raw;
        }
    }

    public enum UmaRegion
    {
        Japan = 0,
        Global = 1
    }
}
