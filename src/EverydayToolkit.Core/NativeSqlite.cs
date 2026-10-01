using System.Runtime.InteropServices;
using System.Text;

namespace EverydayToolkit.Core;

// Only fixed SQL reaches this wrapper. All content values are bound with SQLITE_TRANSIENT.
internal sealed class NativeSqlite : IDisposable
{
    private IntPtr database;
    internal NativeSqlite(string path)
    {
        var result = Native.sqlite3_open_v2(path, out database, 0x00000002 | 0x00000004 | 0x00010000, IntPtr.Zero);
        if (result != 0) { Dispose(); throw Error(result); }
        Check(Native.sqlite3_busy_timeout(database, 5000));
    }
    internal int Execute(string sql, params object?[] values)
    {
        using var statement = Prepare(sql, values);
        var result = Native.sqlite3_step(statement.Handle);
        if (result != 101) throw Error(result);
        return Native.sqlite3_changes(database);
    }
    internal List<object?[]> Query(string sql, params object?[] values)
    {
        using var statement = Prepare(sql, values); var rows = new List<object?[]>();
        while (true)
        {
            var result = Native.sqlite3_step(statement.Handle);
            if (result == 101) return rows;
            if (result != 100) throw Error(result);
            var row = new object?[Native.sqlite3_column_count(statement.Handle)];
            for (var i = 0; i < row.Length; i++)
            {
                var type = Native.sqlite3_column_type(statement.Handle, i);
                if (type == 5) continue;
                if (type == 1) { row[i] = Native.sqlite3_column_int64(statement.Handle, i); continue; }
                var length = Native.sqlite3_column_bytes(statement.Handle, i); var bytes = new byte[length];
                if (length > 0) Marshal.Copy(Native.sqlite3_column_blob(statement.Handle, i), bytes, 0, length);
                row[i] = type == 3 ? Encoding.UTF8.GetString(bytes) : bytes;
            }
            rows.Add(row);
        }
    }
    internal long Scalar(string sql, params object?[] values) => (long)Query(sql, values)[0][0]!;
    private Statement Prepare(string sql, object?[] values)
    {
        Check(Native.sqlite3_prepare_v2(database, sql, -1, out var handle, IntPtr.Zero));
        var statement = new Statement(handle);
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i]; var index = i + 1;
                var result = value switch
                {
                    null => Native.sqlite3_bind_null(handle, index),
                    byte[] bytes => bytes.Length == 0 ? Native.sqlite3_bind_zeroblob(handle, index, 0) : Native.sqlite3_bind_blob(handle, index, bytes, bytes.Length, new IntPtr(-1)),
                    string text => BindText(handle, index, text),
                    Guid guid => BindText(handle, index, guid.ToString("D")),
                    bool flag => Native.sqlite3_bind_int64(handle, index, flag ? 1 : 0),
                    int number => Native.sqlite3_bind_int64(handle, index, number),
                    long number => Native.sqlite3_bind_int64(handle, index, number),
                    _ => throw new ArgumentException("不支持的数据库参数类型。")
                };
                Check(result);
            }
            return statement;
        }
        catch { statement.Dispose(); throw; }
    }
    private static int BindText(IntPtr handle, int index, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        return Native.sqlite3_bind_text(handle, index, bytes, bytes.Length - 1, new IntPtr(-1));
    }
    private static void Check(int result) { if (result != 0) throw Error(result); }
    private static IOException Error(int result) => new($"本地数据库操作失败（SQLite 状态 {result}），请检查存储位置和文件。数据库未被重建。");
    public void Dispose() { if (database != IntPtr.Zero) { Native.sqlite3_close_v2(database); database = IntPtr.Zero; } }
    private sealed class Statement(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public void Dispose() => Native.sqlite3_finalize(Handle);
    }
    private static class Native
    {
        private const string Library = "winsqlite3.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_changes(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_blob(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_zeroblob(IntPtr statement, int index, int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_type(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int index);
    }
}
