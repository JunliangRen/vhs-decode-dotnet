[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $DecodeExecutable,

    [string] $DecodeCommand,

    [Parameter(Mandatory)]
    [string] $InputFile,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [string[]] $AdditionalArguments = @(),

    [ValidateRange(0, 2147483647)]
    [int] $CudaDevice = 0,

    [ValidateRange(100, 60000)]
    [int] $GpuSampleIntervalMs = 500,

    [ValidateNotNullOrEmpty()]
    [string] $OutputBaseName = 'decode'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('VhsDecode.CudaQualification.Sample16FileComparer' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VhsDecode.CudaQualification
{
    public sealed class Sample16FileComparisonResult
    {
        public bool EqualLength { get; set; }
        public bool ValidSampleEncoding { get; set; }
        public long SampleCount { get; set; }
        public long DifferingSampleCount { get; set; }
        public double DifferingSampleRate { get; set; }
        public int MaximumAbsoluteDifferenceLsb { get; set; }
        public bool WithinEngineeringTolerance { get; set; }
    }

    public static class Sample16FileComparer
    {
        private const int BufferSize = 1024 * 1024;

        public static Sample16FileComparisonResult Compare(
            string cpuPath,
            string cudaPath,
            bool signedSamples)
        {
            var cpuInfo = new FileInfo(cpuPath);
            var cudaInfo = new FileInfo(cudaPath);
            var result = new Sample16FileComparisonResult
            {
                EqualLength = cpuInfo.Length == cudaInfo.Length,
                ValidSampleEncoding = (cpuInfo.Length & 1) == 0 &&
                    (cudaInfo.Length & 1) == 0
            };

            if (!result.EqualLength || !result.ValidSampleEncoding)
            {
                return result;
            }

            result.SampleCount = cpuInfo.Length / sizeof(short);
            var cpuBuffer = new byte[BufferSize];
            var cudaBuffer = new byte[BufferSize];
            using (var cpu = new FileStream(
                cpuPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan))
            using (var cuda = new FileStream(
                cudaPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan))
            {
                long remaining = cpuInfo.Length;
                while (remaining > 0)
                {
                    int requested = (int)Math.Min(BufferSize, remaining);
                    ReadExactly(cpu, cpuBuffer, requested);
                    ReadExactly(cuda, cudaBuffer, requested);

                    for (int offset = 0; offset < requested; offset += sizeof(short))
                    {
                        int cpuBits = cpuBuffer[offset] |
                            (cpuBuffer[offset + 1] << 8);
                        int cudaBits = cudaBuffer[offset] |
                            (cudaBuffer[offset + 1] << 8);
                        int cpuValue = signedSamples
                            ? unchecked((short)cpuBits)
                            : cpuBits;
                        int cudaValue = signedSamples
                            ? unchecked((short)cudaBits)
                            : cudaBits;
                        int absoluteDifference = Math.Abs(cpuValue - cudaValue);
                        if (absoluteDifference != 0)
                        {
                            result.DifferingSampleCount++;
                            if (absoluteDifference > result.MaximumAbsoluteDifferenceLsb)
                            {
                                result.MaximumAbsoluteDifferenceLsb = absoluteDifference;
                            }
                        }
                    }

                    remaining -= requested;
                }
            }

            result.DifferingSampleRate = result.SampleCount == 0
                ? 0.0
                : (double)result.DifferingSampleCount / result.SampleCount;
            result.WithinEngineeringTolerance =
                result.MaximumAbsoluteDifferenceLsb <= 1 &&
                result.DifferingSampleRate <= 0.0001;
            return result;
        }

        private static void ReadExactly(FileStream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "Decoder output changed length while it was being compared.");
                }
                offset += read;
            }
        }
    }

    public sealed class Float32FileComparisonResult
    {
        public bool EqualLength { get; set; }
        public bool ValidSampleEncoding { get; set; }
        public bool NonFiniteLayoutEqual { get; set; } = true;
        public long SampleCount { get; set; }
        public long DifferingSampleCount { get; set; }
        public double NormalizedMaximumAbsoluteError { get; set; }
        public double NormalizedRootMeanSquareError { get; set; }
        public bool WithinEngineeringTolerance { get; set; }
    }

    public static class Float32FileComparer
    {
        public static Float32FileComparisonResult Compare(string cpuPath, string cudaPath)
        {
            byte[] cpuBytes = File.ReadAllBytes(cpuPath);
            byte[] cudaBytes = File.ReadAllBytes(cudaPath);
            var result = new Float32FileComparisonResult
            {
                EqualLength = cpuBytes.Length == cudaBytes.Length,
                ValidSampleEncoding = (cpuBytes.Length & 3) == 0 &&
                    (cudaBytes.Length & 3) == 0
            };
            if (!result.EqualLength || !result.ValidSampleEncoding)
            {
                return result;
            }

            result.SampleCount = cpuBytes.Length / sizeof(float);
            double maximumMagnitude = 0.0;
            double maximumDifference = 0.0;
            double referenceSquares = 0.0;
            double differenceSquares = 0.0;
            for (int offset = 0; offset < cpuBytes.Length; offset += sizeof(float))
            {
                float cpu = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(cpuBytes, offset));
                float cuda = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(cudaBytes, offset));
                bool cpuFinite = float.IsFinite(cpu);
                bool cudaFinite = float.IsFinite(cuda);
                if (!cpuFinite || !cudaFinite)
                {
                    bool sameNonFinite = (float.IsNaN(cpu) && float.IsNaN(cuda)) ||
                        (float.IsPositiveInfinity(cpu) && float.IsPositiveInfinity(cuda)) ||
                        (float.IsNegativeInfinity(cpu) && float.IsNegativeInfinity(cuda));
                    if (!sameNonFinite)
                    {
                        result.NonFiniteLayoutEqual = false;
                        result.DifferingSampleCount++;
                    }
                    continue;
                }

                double difference = Math.Abs((double)cpu - cuda);
                if (BitConverter.SingleToInt32Bits(cpu) != BitConverter.SingleToInt32Bits(cuda))
                {
                    result.DifferingSampleCount++;
                }
                maximumDifference = Math.Max(maximumDifference, difference);
                maximumMagnitude = Math.Max(
                    maximumMagnitude,
                    Math.Max(Math.Abs((double)cpu), Math.Abs((double)cuda)));
                referenceSquares += (double)cpu * cpu;
                differenceSquares += difference * difference;
            }

            double peakScale = Math.Max(maximumMagnitude, 1e-300);
            result.NormalizedMaximumAbsoluteError = maximumDifference / peakScale;
            result.NormalizedRootMeanSquareError = referenceSquares > 0.0
                ? Math.Sqrt(differenceSquares / referenceSquares)
                : Math.Sqrt(differenceSquares / Math.Max(1L, result.SampleCount));
            result.WithinEngineeringTolerance = result.NonFiniteLayoutEqual &&
                result.NormalizedMaximumAbsoluteError <= 2e-6 &&
                result.NormalizedRootMeanSquareError <= 2e-7;
            return result;
        }
    }

    public sealed class JsonMetadataComparisonResult
    {
        public bool Parsed { get; set; }
        public bool RootMetadataEqual { get; set; }
        public bool FieldCountEqual { get; set; }
        public bool FileLocSequenceEqual { get; set; }
        public bool FieldMetadataEqual { get; set; }
        public bool Equal { get; set; }
        public int CpuFieldCount { get; set; }
        public int CudaFieldCount { get; set; }
        public string FirstMismatch { get; set; } = string.Empty;
    }

    public static class JsonMetadataComparer
    {
        public static JsonMetadataComparisonResult Compare(string cpuPath, string cudaPath)
        {
            var result = new JsonMetadataComparisonResult();
            try
            {
                using var cpu = JsonDocument.Parse(File.ReadAllBytes(cpuPath));
                using var cuda = JsonDocument.Parse(File.ReadAllBytes(cudaPath));
                if (cpu.RootElement.ValueKind != JsonValueKind.Object ||
                    cuda.RootElement.ValueKind != JsonValueKind.Object)
                {
                    result.FirstMismatch = "Both JSON metadata roots must be objects.";
                    return result;
                }

                result.Parsed = true;
                bool cpuHasFields = cpu.RootElement.TryGetProperty("fields", out JsonElement cpuFields);
                bool cudaHasFields = cuda.RootElement.TryGetProperty("fields", out JsonElement cudaFields);
                result.RootMetadataEqual = string.Equals(
                    CanonicalizeObject(cpu.RootElement, "fields"),
                    CanonicalizeObject(cuda.RootElement, "fields"),
                    StringComparison.Ordinal);
                if (!result.RootMetadataEqual)
                {
                    result.FirstMismatch = "Root metadata outside the fields array differs.";
                }

                if (cpuHasFields != cudaHasFields)
                {
                    result.FirstMismatch = "Only one JSON document contains a fields array.";
                    return result;
                }

                if (!cpuHasFields)
                {
                    result.FieldCountEqual = true;
                    result.FileLocSequenceEqual = true;
                    result.FieldMetadataEqual = true;
                    result.Equal = result.RootMetadataEqual;
                    return result;
                }

                if (cpuFields.ValueKind != JsonValueKind.Array ||
                    cudaFields.ValueKind != JsonValueKind.Array)
                {
                    result.FirstMismatch = "The JSON fields property must be an array.";
                    return result;
                }

                JsonElement.ArrayEnumerator cpuEnumerator = cpuFields.EnumerateArray();
                JsonElement.ArrayEnumerator cudaEnumerator = cudaFields.EnumerateArray();
                var cpuItems = cpuEnumerator.ToArray();
                var cudaItems = cudaEnumerator.ToArray();
                result.CpuFieldCount = cpuItems.Length;
                result.CudaFieldCount = cudaItems.Length;
                result.FieldCountEqual = cpuItems.Length == cudaItems.Length;
                result.FileLocSequenceEqual = result.FieldCountEqual;
                result.FieldMetadataEqual = result.FieldCountEqual;
                if (!result.FieldCountEqual)
                {
                    result.FirstMismatch = $"Field counts differ: CPU={cpuItems.Length}, CUDA={cudaItems.Length}.";
                    return result;
                }

                for (int index = 0; index < cpuItems.Length; index++)
                {
                    if (!TryGetFileLoc(cpuItems[index], out string cpuFileLoc) ||
                        !TryGetFileLoc(cudaItems[index], out string cudaFileLoc))
                    {
                        result.FileLocSequenceEqual = false;
                        result.FieldMetadataEqual = false;
                        result.FirstMismatch = $"Field {index} is missing a scalar fileLoc value.";
                        break;
                    }

                    if (!string.Equals(cpuFileLoc, cudaFileLoc, StringComparison.Ordinal))
                    {
                        result.FileLocSequenceEqual = false;
                        result.FieldMetadataEqual = false;
                        result.FirstMismatch = $"Field {index} fileLoc differs: CPU={cpuFileLoc}, CUDA={cudaFileLoc}.";
                        break;
                    }

                    if (!string.Equals(
                            Canonicalize(cpuItems[index]),
                            Canonicalize(cudaItems[index]),
                            StringComparison.Ordinal))
                    {
                        result.FieldMetadataEqual = false;
                        result.FirstMismatch = $"Metadata differs for field {index} at fileLoc {cpuFileLoc}.";
                        break;
                    }
                }

                result.Equal = result.RootMetadataEqual &&
                    result.FieldCountEqual &&
                    result.FileLocSequenceEqual &&
                    result.FieldMetadataEqual;
                return result;
            }
            catch (Exception exception)
            {
                result.FirstMismatch = exception.GetType().Name + ": " + exception.Message;
                return result;
            }
        }

        private static bool TryGetFileLoc(JsonElement field, out string value)
        {
            value = string.Empty;
            if (field.ValueKind != JsonValueKind.Object ||
                !field.TryGetProperty("fileLoc", out JsonElement fileLoc) ||
                (fileLoc.ValueKind != JsonValueKind.Number &&
                 fileLoc.ValueKind != JsonValueKind.String))
            {
                return false;
            }

            value = Canonicalize(fileLoc);
            return true;
        }

        private static string Canonicalize(JsonElement value)
        {
            var builder = new StringBuilder();
            AppendCanonical(builder, value, null);
            return builder.ToString();
        }

        private static string CanonicalizeObject(JsonElement value, string excludedProperty)
        {
            var builder = new StringBuilder();
            AppendCanonical(builder, value, excludedProperty);
            return builder.ToString();
        }

        private static void AppendCanonical(
            StringBuilder builder,
            JsonElement value,
            string excludedProperty)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    builder.Append('{');
                    bool firstProperty = true;
                    foreach (JsonProperty property in value.EnumerateObject()
                        .Where(property => excludedProperty == null ||
                            !string.Equals(property.Name, excludedProperty, StringComparison.Ordinal))
                        .OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        if (!firstProperty)
                        {
                            builder.Append(',');
                        }
                        firstProperty = false;
                        builder.Append(JsonSerializer.Serialize(property.Name));
                        builder.Append(':');
                        AppendCanonical(builder, property.Value, null);
                    }
                    builder.Append('}');
                    break;
                case JsonValueKind.Array:
                    builder.Append('[');
                    bool firstItem = true;
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        if (!firstItem)
                        {
                            builder.Append(',');
                        }
                        firstItem = false;
                        AppendCanonical(builder, item, null);
                    }
                    builder.Append(']');
                    break;
                case JsonValueKind.String:
                    builder.Append(JsonSerializer.Serialize(value.GetString()));
                    break;
                case JsonValueKind.Number:
                    if (value.TryGetInt64(out long signed))
                    {
                        builder.Append("n:");
                        builder.Append(signed.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (value.TryGetUInt64(out ulong unsigned))
                    {
                        builder.Append("n:");
                        builder.Append(unsigned.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (value.TryGetDecimal(out decimal precise))
                    {
                        builder.Append("n:");
                        builder.Append(precise.ToString("G29", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append("n:");
                        builder.Append(value.GetDouble().ToString("R", CultureInfo.InvariantCulture));
                    }
                    break;
                case JsonValueKind.True:
                    builder.Append("true");
                    break;
                case JsonValueKind.False:
                    builder.Append("false");
                    break;
                case JsonValueKind.Null:
                    builder.Append("null");
                    break;
                default:
                    throw new InvalidDataException($"Unsupported JSON value kind {value.ValueKind}.");
            }
        }
    }

    public sealed class SqliteLogicalComparisonResult
    {
        public bool Opened { get; set; }
        public bool UserVersionEqual { get; set; }
        public bool SchemaEqual { get; set; }
        public bool TableSetEqual { get; set; }
        public bool RowsEqual { get; set; }
        public bool Equal { get; set; }
        public int CpuTableCount { get; set; }
        public int CudaTableCount { get; set; }
        public string FirstMismatch { get; set; } = string.Empty;
    }

    public static class SqliteLogicalComparer
    {
        private const int SqliteOk = 0;
        private const int SqliteRow = 100;
        private const int SqliteDone = 101;
        private const int SqliteOpenReadOnly = 0x00000001;

        public static SqliteLogicalComparisonResult Compare(string cpuPath, string cudaPath)
        {
            var result = new SqliteLogicalComparisonResult();
            try
            {
                DatabaseSnapshot cpu = ReadSnapshot(cpuPath);
                DatabaseSnapshot cuda = ReadSnapshot(cudaPath);
                result.Opened = true;
                result.CpuTableCount = cpu.Tables.Count;
                result.CudaTableCount = cuda.Tables.Count;
                result.UserVersionEqual = string.Equals(
                    cpu.UserVersion,
                    cuda.UserVersion,
                    StringComparison.Ordinal);
                result.SchemaEqual = cpu.Schema.SequenceEqual(cuda.Schema, StringComparer.Ordinal);
                string[] cpuNames = cpu.Tables.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
                string[] cudaNames = cuda.Tables.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
                result.TableSetEqual = cpuNames.SequenceEqual(cudaNames, StringComparer.Ordinal);
                result.RowsEqual = result.TableSetEqual;

                if (!result.UserVersionEqual)
                {
                    result.FirstMismatch = $"PRAGMA user_version differs: CPU={cpu.UserVersion}, CUDA={cuda.UserVersion}.";
                }
                else if (!result.SchemaEqual)
                {
                    result.FirstMismatch = "SQLite logical schema entries differ.";
                }
                else if (!result.TableSetEqual)
                {
                    result.FirstMismatch = "SQLite table sets differ.";
                }
                else
                {
                    foreach (string table in cpuNames)
                    {
                        TableSnapshot cpuTable = cpu.Tables[table];
                        TableSnapshot cudaTable = cuda.Tables[table];
                        if (!cpuTable.Columns.SequenceEqual(cudaTable.Columns, StringComparer.Ordinal))
                        {
                            result.RowsEqual = false;
                            result.FirstMismatch = $"SQLite table '{table}' column metadata differs.";
                            break;
                        }
                        if (!cpuTable.Rows.SequenceEqual(cudaTable.Rows, StringComparer.Ordinal))
                        {
                            result.RowsEqual = false;
                            result.FirstMismatch = $"SQLite table '{table}' logical rows differ.";
                            break;
                        }
                    }
                }

                result.Equal = result.Opened &&
                    result.UserVersionEqual &&
                    result.SchemaEqual &&
                    result.TableSetEqual &&
                    result.RowsEqual;
                return result;
            }
            catch (Exception exception)
            {
                result.FirstMismatch = exception.GetType().Name + ": " + exception.Message;
                return result;
            }
        }

        private static DatabaseSnapshot ReadSnapshot(string path)
        {
            int status = sqlite3_open_v2(path, out IntPtr database, SqliteOpenReadOnly, IntPtr.Zero);
            if (status != SqliteOk)
            {
                string message = database == IntPtr.Zero ? "unknown SQLite open error" : GetError(database);
                if (database != IntPtr.Zero)
                {
                    sqlite3_close(database);
                }
                throw new InvalidDataException($"Could not open SQLite database '{path}': {message}");
            }

            try
            {
                var snapshot = new DatabaseSnapshot
                {
                    UserVersion = QueryRows(database, "PRAGMA user_version").Single(),
                    Schema = QueryRows(
                        database,
                        "SELECT type,name,tbl_name,coalesce(sql,'') FROM sqlite_master " +
                        "WHERE name NOT LIKE 'sqlite_autoindex_%' ORDER BY type,name")
                };
                foreach (string tableName in QueryTextColumn(
                    database,
                    "SELECT name FROM sqlite_master WHERE type='table' " +
                    "AND name NOT LIKE 'sqlite_autoindex_%' ORDER BY name"))
                {
                    var table = new TableSnapshot
                    {
                        Columns = QueryRows(database, "PRAGMA table_info(" + QuoteIdentifier(tableName) + ")"),
                        Rows = QueryRows(database, "SELECT * FROM " + QuoteIdentifier(tableName))
                    };
                    table.Rows.Sort(StringComparer.Ordinal);
                    snapshot.Tables.Add(tableName, table);
                }
                return snapshot;
            }
            finally
            {
                sqlite3_close(database);
            }
        }

        private static List<string> QueryRows(IntPtr database, string sql)
        {
            IntPtr statement = Prepare(database, sql);
            try
            {
                int columnCount = sqlite3_column_count(statement);
                var rows = new List<string>();
                while (true)
                {
                    int status = sqlite3_step(statement);
                    if (status == SqliteDone)
                    {
                        return rows;
                    }
                    if (status != SqliteRow)
                    {
                        throw new InvalidDataException($"SQLite query failed: {GetError(database)} SQL={sql}");
                    }

                    var builder = new StringBuilder();
                    builder.Append(columnCount.ToString(CultureInfo.InvariantCulture));
                    for (int column = 0; column < columnCount; column++)
                    {
                        builder.Append('|');
                        builder.Append(ReadUtf8(sqlite3_column_name(statement, column), -1));
                        builder.Append('=');
                        AppendColumn(builder, statement, column);
                    }
                    rows.Add(builder.ToString());
                }
            }
            finally
            {
                sqlite3_finalize(statement);
            }
        }

        private static List<string> QueryTextColumn(IntPtr database, string sql)
        {
            IntPtr statement = Prepare(database, sql);
            try
            {
                var values = new List<string>();
                while (true)
                {
                    int status = sqlite3_step(statement);
                    if (status == SqliteDone)
                    {
                        return values;
                    }
                    if (status != SqliteRow)
                    {
                        throw new InvalidDataException($"SQLite query failed: {GetError(database)} SQL={sql}");
                    }
                    values.Add(ReadUtf8(
                        sqlite3_column_text(statement, 0),
                        sqlite3_column_bytes(statement, 0)));
                }
            }
            finally
            {
                sqlite3_finalize(statement);
            }
        }

        private static IntPtr Prepare(IntPtr database, string sql)
        {
            int status = sqlite3_prepare_v2(database, sql, -1, out IntPtr statement, IntPtr.Zero);
            if (status != SqliteOk)
            {
                throw new InvalidDataException($"SQLite prepare failed: {GetError(database)} SQL={sql}");
            }
            return statement;
        }

        private static void AppendColumn(StringBuilder builder, IntPtr statement, int column)
        {
            switch (sqlite3_column_type(statement, column))
            {
                case 1:
                    builder.Append("i:");
                    builder.Append(sqlite3_column_int64(statement, column).ToString(CultureInfo.InvariantCulture));
                    break;
                case 2:
                    builder.Append("r:");
                    builder.Append(BitConverter.DoubleToInt64Bits(
                        sqlite3_column_double(statement, column)).ToString("x16", CultureInfo.InvariantCulture));
                    break;
                case 3:
                    builder.Append("t:");
                    builder.Append(Convert.ToBase64String(ReadBytes(
                        sqlite3_column_text(statement, column),
                        sqlite3_column_bytes(statement, column))));
                    break;
                case 4:
                    builder.Append("b:");
                    builder.Append(Convert.ToBase64String(ReadBytes(
                        sqlite3_column_blob(statement, column),
                        sqlite3_column_bytes(statement, column))));
                    break;
                case 5:
                    builder.Append("null");
                    break;
                default:
                    throw new InvalidDataException("SQLite returned an unknown column type.");
            }
        }

        private static byte[] ReadBytes(IntPtr pointer, int byteCount)
        {
            if (byteCount <= 0)
            {
                return Array.Empty<byte>();
            }
            var bytes = new byte[byteCount];
            Marshal.Copy(pointer, bytes, 0, byteCount);
            return bytes;
        }

        private static string ReadUtf8(IntPtr pointer, int byteCount)
        {
            if (pointer == IntPtr.Zero)
            {
                return string.Empty;
            }
            if (byteCount < 0)
            {
                return Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
            }
            return Encoding.UTF8.GetString(ReadBytes(pointer, byteCount));
        }

        private static string QuoteIdentifier(string identifier) =>
            "\"" + identifier.Replace("\"", "\"\"") + "\"";

        private static string GetError(IntPtr database) =>
            ReadUtf8(sqlite3_errmsg(database), -1);

        private sealed class DatabaseSnapshot
        {
            public string UserVersion { get; set; } = string.Empty;
            public List<string> Schema { get; set; } = new List<string>();
            public Dictionary<string, TableSnapshot> Tables { get; } =
                new Dictionary<string, TableSnapshot>(StringComparer.Ordinal);
        }

        private sealed class TableSnapshot
        {
            public List<string> Columns { get; set; } = new List<string>();
            public List<string> Rows { get; set; } = new List<string>();
        }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_open_v2(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            out IntPtr database,
            int flags,
            IntPtr vfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_prepare_v2(
            IntPtr database,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            int byteCount,
            out IntPtr statement,
            IntPtr tail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern double sqlite3_column_double(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr database);
    }
}
'@
}

function Get-FullPath {
    param([Parameter(Mandatory)][string] $Path)

    return [IO.Path]::GetFullPath($Path)
}

function Assert-SafeOutputBaseName {
    param([Parameter(Mandatory)][string] $Name)

    if ($Name -in @('.', '..') -or
        $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $Name.Contains([IO.Path]::DirectorySeparatorChar) -or
        $Name.Contains([IO.Path]::AltDirectorySeparatorChar)) {
        throw "OutputBaseName must be one file-name component; got '$Name'."
    }
}

function Assert-BackendArgumentsAreOwnedByHarness {
    param([Parameter(Mandatory)][string[]] $Arguments)

    foreach ($argument in $Arguments) {
        if ($argument -match '^(?i:--compute-backend)(?:=|$)' -or
            $argument -match '^(?i:--cuda-device)(?:=|$)') {
            throw "AdditionalArguments must not set '$argument'; backend selection is owned by this harness."
        }
    }
}

function Get-NvidiaSmiPath {
    $command = Get-Command 'nvidia-smi.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $systemPath = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
    if (Test-Path -LiteralPath $systemPath -PathType Leaf) {
        return $systemPath
    }

    return $null
}

function Convert-ToInvariantDouble {
    param([AllowNull()][string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $parsed = 0.0
    if ([double]::TryParse(
            $Value.Trim(),
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref] $parsed)) {
        return $parsed
    }

    return $null
}

function Get-GpuSamples {
    param([AllowNull()][string] $NvidiaSmiPath)

    if ([string]::IsNullOrWhiteSpace($NvidiaSmiPath)) {
        return @()
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $NvidiaSmiPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('--query-gpu=index,name,utilization.gpu,memory.used,memory.total')
    $startInfo.ArgumentList.Add('--format=csv,noheader,nounits')

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            return @()
        }
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $process.StandardError.ReadToEnd() | Out-Null
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            return @()
        }
    }
    catch {
        return @()
    }
    finally {
        $process.Dispose()
    }

    $sampledUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $samples = [Collections.Generic.List[object]]::new()
    foreach ($line in $standardOutput -split "`r?`n") {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $columns = $line.Split(',', 5, [StringSplitOptions]::TrimEntries)
        if ($columns.Count -ne 5) {
            continue
        }

        $gpuIndex = 0
        if (-not [int]::TryParse(
                $columns[0],
                [Globalization.NumberStyles]::Integer,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref] $gpuIndex)) {
            continue
        }

        $samples.Add([pscustomobject][ordered]@{
            sampledUtc = $sampledUtc
            gpuIndex = $gpuIndex
            gpuName = $columns[1]
            utilizationPercent = Convert-ToInvariantDouble $columns[2]
            memoryUsedMiB = Convert-ToInvariantDouble $columns[3]
            memoryTotalMiB = Convert-ToInvariantDouble $columns[4]
        })
    }

    return @($samples)
}

function Get-GpuSummary {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Samples)

    $summary = @()
    foreach ($group in $Samples | Group-Object gpuIndex | Sort-Object { [int] $_.Name }) {
        $utilization = @($group.Group.utilizationPercent | Where-Object { $null -ne $_ })
        $memoryUsed = @($group.Group.memoryUsedMiB | Where-Object { $null -ne $_ })
        $first = $group.Group[0]
        $summary += [pscustomobject][ordered]@{
            gpuIndex = [int] $group.Name
            gpuName = $first.gpuName
            sampleCount = $group.Count
            averageUtilizationPercent = if ($utilization.Count) {
                ($utilization | Measure-Object -Average).Average
            } else { $null }
            maximumUtilizationPercent = if ($utilization.Count) {
                ($utilization | Measure-Object -Maximum).Maximum
            } else { $null }
            maximumMemoryUsedMiB = if ($memoryUsed.Count) {
                ($memoryUsed | Measure-Object -Maximum).Maximum
            } else { $null }
            memoryTotalMiB = $first.memoryTotalMiB
        }
    }

    return @($summary)
}

function Get-FileDigest {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $file = Get-Item -LiteralPath $Path
    return [pscustomobject][ordered]@{
        path = $file.FullName
        sizeBytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-TestedBinaryArtifactEvidence {
    param([Parameter(Mandatory)][string] $Executable)

    $executableDigest = Get-FileDigest $Executable
    if ($null -eq $executableDigest) {
        throw "Decode executable disappeared while qualification evidence was collected: $Executable"
    }
    $directory = Split-Path -Parent $Executable
    $sidecarNames = @(
        'vhsdecode_cuda.dll',
        'cudart64_13.dll',
        'cufft64_12.dll',
        'cuda-component.json'
    )
    $sidecars = @()
    foreach ($name in $sidecarNames) {
        $path = Join-Path $directory $name
        $digest = Get-FileDigest $path
        if ($null -ne $digest) {
            $sidecars += [pscustomobject][ordered]@{
                name = $name
                path = $digest.path
                sizeBytes = $digest.sizeBytes
                sha256 = $digest.sha256
            }
        }
    }
    $requiredBinarySidecars = @('vhsdecode_cuda.dll', 'cudart64_13.dll', 'cufft64_12.dll')
    $requiredSidecars = @($requiredBinarySidecars + 'cuda-component.json')
    $presentSidecarNames = @($sidecars | ForEach-Object { $_.name })
    $requiredSidecarSetPresent = @($requiredSidecars | Where-Object {
            $_ -notin $presentSidecarNames
        }).Count -eq 0
    $manifestValidation = [pscustomobject][ordered]@{
        present = $false
        schemaIdentityValid = $false
        sourceIdentityValid = $false
        binaryFileEntriesMatch = $false
        valid = $false
        error = $null
    }
    $manifestPath = Join-Path $directory 'cuda-component.json'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $schemaIdentityValid =
                $manifest.PSObject.Properties.Name -contains 'schemaVersion' -and
                $manifest.schemaVersion -eq 2 -and
                $manifest.PSObject.Properties.Name -contains 'component' -and
                $manifest.component -eq 'vhsdecode-cuda' -and
                $manifest.PSObject.Properties.Name -contains 'platform' -and
                $manifest.platform -eq 'win-x64' -and
                $manifest.PSObject.Properties.Name -contains 'abiVersion' -and
                $manifest.abiVersion -eq 1
            $sourceProvenanceFieldsPresent =
                $manifest.PSObject.Properties.Name -contains 'repositoryCommit' -and
                $manifest.PSObject.Properties.Name -contains 'repositorySourceTreeSha256' -and
                $manifest.PSObject.Properties.Name -contains 'repositorySourceIdentity' -and
                $manifest.PSObject.Properties.Name -contains 'repositoryDirty' -and
                $manifest.PSObject.Properties.Name -contains 'repositoryDirtyPathCount'
            $sourceIdentityValid = $false
            if ($sourceProvenanceFieldsPresent) {
                $commit = ([string] $manifest.repositoryCommit).ToLowerInvariant()
                $sourceTree = ([string] $manifest.repositorySourceTreeSha256).ToLowerInvariant()
                $sourceIdentity = ([string] $manifest.repositorySourceIdentity).ToLowerInvariant()
                $expectedSourceIdentity = if ($manifest.repositoryDirty -eq $true) {
                    "$commit+dirty.$sourceTree"
                } else {
                    $commit
                }
                $sourceIdentityValid =
                    $commit -match '^[0-9a-f]{40}$' -and
                    $sourceTree -match '^[0-9a-f]{64}$' -and
                    $manifest.repositoryDirty -is [bool] -and
                    [long] $manifest.repositoryDirtyPathCount -ge 0 -and
                    $manifest.repositoryDirty -eq
                        ([long] $manifest.repositoryDirtyPathCount -gt 0) -and
                    $sourceIdentity -ceq $expectedSourceIdentity
            }
            $binaryFileEntriesMatch =
                $manifest.PSObject.Properties.Name -contains 'files'
            if ($binaryFileEntriesMatch) {
                foreach ($name in $requiredBinarySidecars) {
                    $entries = @($manifest.files | Where-Object { $_.path -ieq $name })
                    $digests = @($sidecars | Where-Object { $_.name -ieq $name })
                    if ($entries.Count -ne 1 -or $digests.Count -ne 1 -or
                        [long] $entries[0].size -ne [long] $digests[0].sizeBytes -or
                        ([string] $entries[0].sha256).ToLowerInvariant() -ne
                            ([string] $digests[0].sha256).ToLowerInvariant()) {
                        $binaryFileEntriesMatch = $false
                        break
                    }
                }
            }
            $manifestValidation = [pscustomobject][ordered]@{
                present = $true
                schemaIdentityValid = $schemaIdentityValid
                sourceIdentityValid = $sourceIdentityValid
                binaryFileEntriesMatch = $binaryFileEntriesMatch
                valid = $schemaIdentityValid -and
                    $sourceIdentityValid -and
                    $binaryFileEntriesMatch
                error = $null
            }
        }
        catch {
            $manifestValidation = [pscustomobject][ordered]@{
                present = $true
                schemaIdentityValid = $false
                sourceIdentityValid = $false
                binaryFileEntriesMatch = $false
                valid = $false
                error = $_.Exception.Message
            }
        }
    }
    $fingerprintLines = @(
        "decode.exe|$($executableDigest.path)|$($executableDigest.sizeBytes)|$($executableDigest.sha256)"
    ) + @($sidecars | Sort-Object name | ForEach-Object {
            "$($_.name)|$($_.path)|$($_.sizeBytes)|$($_.sha256)"
        })
    $fingerprint = Get-TextSha256 ($fingerprintLines -join "`n")
    return [pscustomobject][ordered]@{
        executable = $executableDigest
        sidecarDirectory = $directory
        requiredSidecarSetPresent = $requiredSidecarSetPresent
        manifestValidation = $manifestValidation
        sidecars = $sidecars
        fingerprintSha256 = $fingerprint
    }
}

function Get-TextSha256 {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)

    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    $hash = [Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function ConvertTo-NormalizedDiagnosticText {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)

    # Normalize line endings first so platform-specific capture details do not
    # masquerade as a decoder difference.
    $normalized = $Text.Replace("`r`n", "`n").Replace("`r", "`n")

    # Decoder file logs use Python-style local timestamps. Also accept ISO-8601
    # prefixes and an optional pair of brackets for console logging adapters.
    $timestampPattern = '(?m)^\s*\[?\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[,.]\d+)?(?:\s*(?:Z|[+-]\d{2}:?\d{2}))?\]?\s*'
    $normalized = [Text.RegularExpressions.Regex]::Replace(
        $normalized,
        $timestampPattern,
        '<timestamp> ')

    # The backend-selection diagnostic is the one intentional content change
    # between the two invocations. Preserve its prefix/level while replacing
    # only the selected backend detail.
    $backendPattern = '(?im)^(?<prefix>.*?RF compute backend selected:)\s*.*$'
    $normalized = [Text.RegularExpressions.Regex]::Replace(
        $normalized,
        $backendPattern,
        '${prefix} <backend>')

    # Timing normalization is intentionally limited to the decoder's known
    # end-of-run statistics. A generic "duration" or FPS replacement could
    # hide semantic values such as a dropout duration or source frame rate.
    $tookPattern = '(?im)^(?<prefix>.*?\bTook\s+)\d+(?:[.,]\d+)?(?<middle>\s+seconds?\s+to\s+decode\s+\d+\s+frames?\s+\()\d+(?:[.,]\d+)?(?<suffix>\s+FPS\s+post-setup\).*)$'
    $normalized = [Text.RegularExpressions.Regex]::Replace(
        $normalized,
        $tookPattern,
        '${prefix}<elapsed>${middle}<rate>${suffix}')
    $completedPattern = '(?im)^(?<prefix>.*?\bCompleted\s+in\s+)\d+(?:[.,]\d+)?(?<middle>\s+seconds?\s+\()\d+(?:[.,]\d+)?(?<suffix>\s+FPS\).*)$'
    $normalized = [Text.RegularExpressions.Regex]::Replace(
        $normalized,
        $completedPattern,
        '${prefix}<elapsed>${middle}<rate>${suffix}')
    $elapsedLinePattern = '(?im)^(?<prefix>\s*Elapsed\s+time:\s*)\d+(?:[.,]\d+)?(?<suffix>\s+seconds?\s*)$'
    $normalized = [Text.RegularExpressions.Regex]::Replace(
        $normalized,
        $elapsedLinePattern,
        '${prefix}<elapsed>${suffix}')

    return $normalized
}

function Get-BackendSelectionEvidence {
    param(
        [Parameter(Mandatory)][ValidateSet('cpu', 'cuda')][string] $ExpectedBackend,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]] $Texts
    )

    $details = [Collections.Generic.List[string]]::new()
    $selections = [Collections.Generic.List[string]]::new()
    $pattern = '(?im)RF compute backend selected:\s*(?<detail>[^\r\n]+)'
    foreach ($text in $Texts) {
        if ([string]::IsNullOrEmpty($text)) {
            continue
        }
        foreach ($match in [Text.RegularExpressions.Regex]::Matches($text, $pattern)) {
            $detail = $match.Groups['detail'].Value.Trim()
            $details.Add($detail)
            if ($detail -match '^(?i:cpu)(?:\b|\s|\()') {
                $selections.Add('cpu')
            } elseif ($detail -match '^(?i:cuda)(?:\b|\s|\()') {
                $selections.Add('cuda')
            } else {
                $selections.Add('unknown')
            }
        }
    }

    $unexpected = @($selections | Where-Object { $_ -ne $ExpectedBackend })
    return [pscustomobject][ordered]@{
        expectedBackend = $ExpectedBackend
        evidenceCount = $selections.Count
        observedSelections = @($selections | Sort-Object -Unique)
        details = @($details | Sort-Object -Unique)
        matchesExpectedBackend = $selections.Count -gt 0 -and $unexpected.Count -eq 0
    }
}

function Get-DiagnosticDigest {
    param(
        [Parameter(Mandatory)][string] $Path,
        [string] $NormalizedPath
    )

    $raw = Get-FileDigest $Path
    if ($null -eq $raw) {
        return $null
    }

    $normalizedText = ConvertTo-NormalizedDiagnosticText(
        [IO.File]::ReadAllText($Path))
    if (-not [string]::IsNullOrWhiteSpace($NormalizedPath)) {
        [IO.File]::WriteAllText(
            $NormalizedPath,
            $normalizedText,
            [Text.UTF8Encoding]::new($false))
    }

    return [pscustomobject][ordered]@{
        path = $raw.path
        sizeBytes = $raw.sizeBytes
        sha256 = $raw.sha256
        normalizedPath = if ([string]::IsNullOrWhiteSpace($NormalizedPath)) {
            $null
        } else {
            $NormalizedPath
        }
        normalizedSizeBytes = [Text.UTF8Encoding]::new($false).GetByteCount($normalizedText)
        normalizedSha256 = Get-TextSha256 $normalizedText
    }
}

function Compare-DiagnosticDigests {
    param(
        [Parameter(Mandatory)][object] $Cpu,
        [Parameter(Mandatory)][object] $Cuda
    )

    $rawEqual = $Cpu.sizeBytes -eq $Cuda.sizeBytes -and
        $Cpu.sha256 -eq $Cuda.sha256
    $normalizedEqual = $Cpu.normalizedSizeBytes -eq $Cuda.normalizedSizeBytes -and
        $Cpu.normalizedSha256 -eq $Cuda.normalizedSha256
    return [pscustomobject][ordered]@{
        rawEqual = $rawEqual
        normalizedEqual = $normalizedEqual
        qualificationMatch = $rawEqual -or $normalizedEqual
        cpuSha256 = $Cpu.sha256
        cudaSha256 = $Cuda.sha256
        cpuNormalizedSha256 = $Cpu.normalizedSha256
        cudaNormalizedSha256 = $Cuda.normalizedSha256
    }
}

function Get-OutputInventory {
    param([Parameter(Mandatory)][string] $Root)

    $items = @()
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName) {
        $relativePath = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $items += [pscustomobject][ordered]@{
            relativePath = $relativePath
            sizeBytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    return @($items)
}

function Get-IntegerOutputKind {
    param(
        [Parameter(Mandatory)][string] $RelativePath,
        [Parameter(Mandatory)][bool] $RawTbcFloat32
    )

    if ($RelativePath.EndsWith(
            '.tbc.ldf',
            [StringComparison]::OrdinalIgnoreCase)) {
        return 'ld-rf-tbc'
    }

    $extension = [IO.Path]::GetExtension($RelativePath)
    if ($extension -ieq '.tbc') {
        if ($RelativePath.EndsWith(
                '_chroma.tbc',
                [StringComparison]::OrdinalIgnoreCase)) {
            return 'chroma-tbc'
        }
        if ($RawTbcFloat32) {
            return $null
        }
        return 'tbc'
    }
    if ($extension -ieq '.pcm') {
        return 'pcm'
    }
    if ($extension -ieq '.efm') {
        return 'efm'
    }

    return $null
}

function Get-FloatOutputKind {
    param(
        [Parameter(Mandatory)][string] $RelativePath,
        [Parameter(Mandatory)][bool] $RawTbcFloat32
    )

    if ($RawTbcFloat32 -and
        [IO.Path]::GetExtension($RelativePath) -ieq '.tbc' -and
        -not $RelativePath.EndsWith(
            '_chroma.tbc',
            [StringComparison]::OrdinalIgnoreCase)) {
        return 'raw-tbc-float32'
    }
    return $null
}

function Compare-IntegerOutput {
    param(
        [Parameter(Mandatory)][string] $CpuPath,
        [Parameter(Mandatory)][string] $CudaPath,
        [Parameter(Mandatory)][string] $OutputKind
    )

    $signedSamples = $OutputKind -in @('pcm', 'efm', 'ld-rf-tbc')
    $comparison = [VhsDecode.CudaQualification.Sample16FileComparer]::Compare(
        $CpuPath,
        $CudaPath,
        $signedSamples)
    return [pscustomobject][ordered]@{
        outputKind = $OutputKind
        sampleFormat = if ($signedSamples) {
            'int16-little-endian'
        } else {
            'uint16-little-endian'
        }
        equalLength = $comparison.EqualLength
        validSampleEncoding = $comparison.ValidSampleEncoding
        sampleCount = $comparison.SampleCount
        differingSampleCount = $comparison.DifferingSampleCount
        differingSampleRate = $comparison.DifferingSampleRate
        maximumAbsoluteDifferenceLsb = $comparison.MaximumAbsoluteDifferenceLsb
        maximumAllowedAbsoluteDifferenceLsb = 1
        maximumAllowedDifferingSampleRate = 0.0001
        withinEngineeringTolerance = $comparison.WithinEngineeringTolerance
    }
}

function Compare-Float32Output {
    param(
        [Parameter(Mandatory)][string] $CpuPath,
        [Parameter(Mandatory)][string] $CudaPath,
        [Parameter(Mandatory)][string] $OutputKind
    )

    $comparison = [VhsDecode.CudaQualification.Float32FileComparer]::Compare(
        $CpuPath,
        $CudaPath)
    return [pscustomobject][ordered]@{
        outputKind = $OutputKind
        sampleFormat = 'float32-little-endian'
        equalLength = $comparison.EqualLength
        validSampleEncoding = $comparison.ValidSampleEncoding
        sampleCount = $comparison.SampleCount
        differingSampleCount = $comparison.DifferingSampleCount
        nonFiniteLayoutEqual = $comparison.NonFiniteLayoutEqual
        normalizedMaximumAbsoluteError = $comparison.NormalizedMaximumAbsoluteError
        normalizedRootMeanSquareError = $comparison.NormalizedRootMeanSquareError
        maximumAllowedNormalizedAbsoluteError = 2e-6
        maximumAllowedNormalizedRootMeanSquareError = 2e-7
        withinEngineeringTolerance = $comparison.WithinEngineeringTolerance
    }
}

function Compare-LogicalOutput {
    param(
        [Parameter(Mandatory)][string] $RelativePath,
        [AllowNull()][string] $CpuPath,
        [AllowNull()][string] $CudaPath
    )

    $extension = [IO.Path]::GetExtension($RelativePath)
    if ($extension -notin @('.json', '.db')) {
        return $null
    }
    if ([string]::IsNullOrWhiteSpace($CpuPath) -or
        [string]::IsNullOrWhiteSpace($CudaPath)) {
        return [pscustomobject][ordered]@{
            comparison = if ($extension -ieq '.json') {
                'fileLoc-aligned JSON metadata'
            } else {
                'SQLite logical rows'
            }
            implemented = $true
            qualificationMatch = $false
            firstMismatch = 'The logical output is missing from one run.'
        }
    }

    if ($extension -ieq '.json') {
        $comparison = [VhsDecode.CudaQualification.JsonMetadataComparer]::Compare(
            $CpuPath,
            $CudaPath)
        return [pscustomobject][ordered]@{
            comparison = 'fileLoc-aligned JSON metadata'
            implemented = $true
            parsed = $comparison.Parsed
            rootMetadataEqual = $comparison.RootMetadataEqual
            fieldCountEqual = $comparison.FieldCountEqual
            fileLocSequenceEqual = $comparison.FileLocSequenceEqual
            fieldMetadataEqual = $comparison.FieldMetadataEqual
            cpuFieldCount = $comparison.CpuFieldCount
            cudaFieldCount = $comparison.CudaFieldCount
            qualificationMatch = $comparison.Equal
            firstMismatch = $comparison.FirstMismatch
        }
    }

    $comparison = [VhsDecode.CudaQualification.SqliteLogicalComparer]::Compare(
        $CpuPath,
        $CudaPath)
    return [pscustomobject][ordered]@{
        comparison = 'SQLite logical rows'
        implemented = $true
        opened = $comparison.Opened
        userVersionEqual = $comparison.UserVersionEqual
        schemaEqual = $comparison.SchemaEqual
        tableSetEqual = $comparison.TableSetEqual
        rowsEqual = $comparison.RowsEqual
        cpuTableCount = $comparison.CpuTableCount
        cudaTableCount = $comparison.CudaTableCount
        qualificationMatch = $comparison.Equal
        firstMismatch = $comparison.FirstMismatch
    }
}

function Compare-OutputInventories {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $CpuFiles,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $CudaFiles,
        [Parameter(Mandatory)][string] $CpuRoot,
        [Parameter(Mandatory)][string] $CudaRoot,
        [Parameter(Mandatory)][bool] $RawTbcFloat32
    )

    $cpuByPath = @{}
    $cudaByPath = @{}
    foreach ($file in $CpuFiles) { $cpuByPath[$file.relativePath] = $file }
    foreach ($file in $CudaFiles) { $cudaByPath[$file.relativePath] = $file }

    $allPaths = @($cpuByPath.Keys + $cudaByPath.Keys | Sort-Object -Unique)
    $comparisons = @()
    foreach ($relativePath in $allPaths) {
        $cpu = $cpuByPath[$relativePath]
        $cuda = $cudaByPath[$relativePath]
        $status = if ($null -eq $cpu) {
            'missing-on-cpu'
        } elseif ($null -eq $cuda) {
            'missing-on-cuda'
        } elseif ($cpu.sha256 -eq $cuda.sha256 -and $cpu.sizeBytes -eq $cuda.sizeBytes) {
            'match'
        } else {
            'mismatch'
        }

        $extension = [IO.Path]::GetExtension($relativePath)
        $isDiagnosticLog = $extension -ieq '.log'
        $integerOutputKind = Get-IntegerOutputKind `
            -RelativePath $relativePath `
            -RawTbcFloat32 $RawTbcFloat32
        $isIntegerOutput = -not [string]::IsNullOrWhiteSpace($integerOutputKind)
        $floatOutputKind = Get-FloatOutputKind `
            -RelativePath $relativePath `
            -RawTbcFloat32 $RawTbcFloat32
        $isFloatOutput = -not [string]::IsNullOrWhiteSpace($floatOutputKind)
        $cpuNormalizedSha256 = $null
        $cudaNormalizedSha256 = $null
        $normalizedEqual = $null
        if ($isDiagnosticLog -and $null -ne $cpu -and $null -ne $cuda) {
            $platformPath = $relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $cpuDiagnostic = Get-DiagnosticDigest (Join-Path $CpuRoot $platformPath)
            $cudaDiagnostic = Get-DiagnosticDigest (Join-Path $CudaRoot $platformPath)
            $cpuNormalizedSha256 = $cpuDiagnostic.normalizedSha256
            $cudaNormalizedSha256 = $cudaDiagnostic.normalizedSha256
            $normalizedEqual = $cpuDiagnostic.normalizedSizeBytes -eq $cudaDiagnostic.normalizedSizeBytes -and
                $cpuNormalizedSha256 -eq $cudaNormalizedSha256
        }

        $integerEngineeringTolerance = $null
        if ($isIntegerOutput -and $null -ne $cpu -and $null -ne $cuda) {
            $platformPath = $relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $integerEngineeringTolerance = Compare-IntegerOutput `
                -CpuPath (Join-Path $CpuRoot $platformPath) `
                -CudaPath (Join-Path $CudaRoot $platformPath) `
                -OutputKind $integerOutputKind
        }

        $floatEngineeringTolerance = $null
        if ($isFloatOutput -and $null -ne $cpu -and $null -ne $cuda) {
            $platformPath = $relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $floatEngineeringTolerance = Compare-Float32Output `
                -CpuPath (Join-Path $CpuRoot $platformPath) `
                -CudaPath (Join-Path $CudaRoot $platformPath) `
                -OutputKind $floatOutputKind
        }

        $platformPath = $relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $cpuLogicalPath = if ($null -ne $cpu) { Join-Path $CpuRoot $platformPath } else { $null }
        $cudaLogicalPath = if ($null -ne $cuda) { Join-Path $CudaRoot $platformPath } else { $null }
        $logicalComparison = Compare-LogicalOutput `
            -RelativePath $relativePath `
            -CpuPath $cpuLogicalPath `
            -CudaPath $cudaLogicalPath
        $isLogicalOutput = $null -ne $logicalComparison
        $logicalQualificationMatch = -not $isLogicalOutput -or
            $logicalComparison.qualificationMatch -eq $true

        $rawQualificationMatch = $status -eq 'match' -and
            (-not $isIntegerOutput -or
                ($null -ne $integerEngineeringTolerance -and
                    $integerEngineeringTolerance.withinEngineeringTolerance -eq $true)) -and
            (-not $isFloatOutput -or
                ($null -ne $floatEngineeringTolerance -and
                    $floatEngineeringTolerance.withinEngineeringTolerance -eq $true)) -and
            $logicalQualificationMatch
        $qualificationMatch = $rawQualificationMatch -or
            ($isDiagnosticLog -and $normalizedEqual -eq $true) -or
            ($isIntegerOutput -and
                $null -ne $integerEngineeringTolerance -and
                $integerEngineeringTolerance.withinEngineeringTolerance -eq $true) -or
            ($isFloatOutput -and
                $null -ne $floatEngineeringTolerance -and
                $floatEngineeringTolerance.withinEngineeringTolerance -eq $true) -or
            ($isLogicalOutput -and
                $logicalComparison.qualificationMatch -eq $true)
        $qualificationBasis = if ($rawQualificationMatch) {
            'exact-size-and-sha256'
        } elseif ($isDiagnosticLog -and $normalizedEqual -eq $true) {
            'normalized-diagnostic'
        } elseif ($isIntegerOutput -and
            $null -ne $integerEngineeringTolerance -and
            $integerEngineeringTolerance.withinEngineeringTolerance -eq $true) {
            'integer16-engineering-tolerance'
        } elseif ($isFloatOutput -and
            $null -ne $floatEngineeringTolerance -and
            $floatEngineeringTolerance.withinEngineeringTolerance -eq $true) {
            'float32-engineering-tolerance'
        } elseif ($isLogicalOutput -and
            $logicalComparison.qualificationMatch -eq $true) {
            if ($extension -ieq '.json') {
                'fileloc-aligned-json-metadata'
            } else {
                'sqlite-logical-rows'
            }
        } else {
            'no-accepted-comparison'
        }

        $comparisons += [pscustomobject][ordered]@{
            relativePath = $relativePath
            status = $status
            isDiagnosticLog = $isDiagnosticLog
            isIntegerOutput = $isIntegerOutput
            isFloatOutput = $isFloatOutput
            normalizedEqual = $normalizedEqual
            integerEngineeringTolerance = $integerEngineeringTolerance
            floatEngineeringTolerance = $floatEngineeringTolerance
            logicalComparison = $logicalComparison
            qualificationMatch = $qualificationMatch
            qualificationBasis = $qualificationBasis
            cpuSizeBytes = if ($null -ne $cpu) { $cpu.sizeBytes } else { $null }
            cudaSizeBytes = if ($null -ne $cuda) { $cuda.sizeBytes } else { $null }
            cpuSha256 = if ($null -ne $cpu) { $cpu.sha256 } else { $null }
            cudaSha256 = if ($null -ne $cuda) { $cuda.sha256 } else { $null }
            cpuNormalizedSha256 = $cpuNormalizedSha256
            cudaNormalizedSha256 = $cudaNormalizedSha256
        }
    }

    return @($comparisons)
}

function Invoke-DecodeRun {
    param(
        [Parameter(Mandatory)][ValidateSet('cpu', 'cuda')][string] $Backend,
        [Parameter(Mandatory)][string] $Executable,
        [AllowEmptyString()][string] $Command,
        [Parameter(Mandatory)][string] $InputPath,
        [Parameter(Mandatory)][string] $OutputRoot,
        [Parameter(Mandatory)][string] $DiagnosticsRoot,
        [Parameter(Mandatory)][string] $BaseName,
        [Parameter(Mandatory)][string[]] $ExtraArguments,
        [Parameter(Mandatory)][int] $Device,
        [AllowNull()][string] $NvidiaSmiPath,
        [Parameter(Mandatory)][int] $SampleIntervalMs
    )

    New-Item -ItemType Directory -Path $OutputRoot | Out-Null
    New-Item -ItemType Directory -Path $DiagnosticsRoot | Out-Null
    $outputBase = Join-Path $OutputRoot $BaseName
    $stdoutPath = Join-Path $DiagnosticsRoot 'stdout.txt'
    $stderrPath = Join-Path $DiagnosticsRoot 'stderr.txt'
    $gpuSamplesPath = Join-Path $DiagnosticsRoot 'gpu-samples.csv'

    $arguments = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($Command)) {
        $arguments.Add($Command)
    }
    foreach ($argument in $ExtraArguments) {
        $arguments.Add($argument)
    }
    $arguments.Add('--compute-backend')
    $arguments.Add($Backend)
    $arguments.Add('--cuda-device')
    $arguments.Add($Device.ToString([Globalization.CultureInfo]::InvariantCulture))
    $arguments.Add($InputPath)
    $arguments.Add($outputBase)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.WorkingDirectory = Split-Path -Parent $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $gpuSamples = [Collections.Generic.List[object]]::new()
    [long] $peakWorkingSetBytes = 0
    $startedUtc = [DateTimeOffset]::UtcNow
    $completedUtc = $null
    $wallSeconds = $null
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) {
            throw "Failed to start decoder '$Executable'."
        }

        try {
            $startedUtc = [DateTimeOffset] $process.StartTime.ToUniversalTime()
            $process.Refresh()
            $peakWorkingSetBytes = [Math]::Max(
                $peakWorkingSetBytes,
                [long] $process.PeakWorkingSet64)
        }
        catch {
            # Stopwatch timing and later samples remain available if the
            # process exits before its properties can be refreshed.
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        while (-not $process.WaitForExit($SampleIntervalMs)) {
            try {
                $process.Refresh()
                $peakWorkingSetBytes = [Math]::Max(
                    $peakWorkingSetBytes,
                    [long] $process.PeakWorkingSet64)
            }
            catch {
                # The process can exit between WaitForExit and Refresh.
            }
            foreach ($sample in Get-GpuSamples $NvidiaSmiPath) {
                $gpuSamples.Add($sample)
            }
        }
        $process.WaitForExit()
        $stopwatch.Stop()
        try {
            $process.Refresh()
            $peakWorkingSetBytes = [Math]::Max(
                $peakWorkingSetBytes,
                [long] $process.PeakWorkingSet64)
        }
        catch {
            # The sampled maximum remains valid after the process is reaped.
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($stdoutPath, $stdout, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($stderrPath, $stderr, [Text.UTF8Encoding]::new($false))
        if ($gpuSamples.Count) {
            $gpuSamples | Export-Csv -LiteralPath $gpuSamplesPath -NoTypeInformation -Encoding utf8NoBOM
        }

        $exitCode = $process.ExitCode
        $cpuSeconds = $process.TotalProcessorTime.TotalSeconds
        try {
            $completedUtc = [DateTimeOffset] $process.ExitTime.ToUniversalTime()
            $wallSeconds = ($completedUtc - $startedUtc).TotalSeconds
        }
        catch {
            $completedUtc = [DateTimeOffset]::UtcNow
            $wallSeconds = $stopwatch.Elapsed.TotalSeconds
        }
    }
    finally {
        if ($stopwatch.IsRunning) {
            $stopwatch.Stop()
        }
        $process.Dispose()
    }

    $files = @(Get-OutputInventory $OutputRoot)
    $backendEvidenceTexts = [Collections.Generic.List[string]]::new()
    $backendEvidenceTexts.Add($stdout)
    $backendEvidenceTexts.Add($stderr)
    foreach ($logFile in $files | Where-Object {
            [IO.Path]::GetExtension($_.relativePath) -ieq '.log'
        }) {
        $platformPath = $logFile.relativePath.Replace(
            '/',
            [IO.Path]::DirectorySeparatorChar)
        $backendEvidenceTexts.Add(
            [IO.File]::ReadAllText((Join-Path $OutputRoot $platformPath)))
    }
    $backendSelectionEvidence = Get-BackendSelectionEvidence `
        -ExpectedBackend $Backend `
        -Texts @($backendEvidenceTexts)
    return [pscustomobject][ordered]@{
        backend = $Backend
        commandArguments = @($arguments)
        startedUtc = $startedUtc.ToString('O')
        completedUtc = $completedUtc.ToString('O')
        exitCode = $exitCode
        wallSeconds = $wallSeconds
        processCpuSeconds = $cpuSeconds
        peakWorkingSetBytes = $peakWorkingSetBytes
        metricScope = 'The CPU-time and peak-working-set metrics cover the main decoder process only; wall time includes waits for child processes.'
        outputDirectory = $OutputRoot
        outputBase = $outputBase
        outputFiles = $files
        backendSelectionEvidence = $backendSelectionEvidence
        stdout = Get-DiagnosticDigest `
            -Path $stdoutPath `
            -NormalizedPath (Join-Path $DiagnosticsRoot 'stdout.normalized.txt')
        stderr = Get-DiagnosticDigest `
            -Path $stderrPath `
            -NormalizedPath (Join-Path $DiagnosticsRoot 'stderr.normalized.txt')
        gpuSampling = [pscustomobject][ordered]@{
            available = -not [string]::IsNullOrWhiteSpace($NvidiaSmiPath)
            nvidiaSmiPath = $NvidiaSmiPath
            intervalMilliseconds = $SampleIntervalMs
            sampleFile = if ($gpuSamples.Count) { $gpuSamplesPath } else { $null }
            sampleCount = $gpuSamples.Count
            devices = @(Get-GpuSummary @($gpuSamples))
        }
    }
}

$executableFull = Get-FullPath $DecodeExecutable
$inputFull = Get-FullPath $InputFile
$outputRootFull = Get-FullPath $OutputDirectory
if (-not (Test-Path -LiteralPath $executableFull -PathType Leaf)) {
    throw "Decode executable does not exist: $executableFull"
}
if (-not (Test-Path -LiteralPath $inputFull -PathType Leaf)) {
    throw "Input file does not exist: $inputFull"
}
Assert-SafeOutputBaseName $OutputBaseName
Assert-BackendArgumentsAreOwnedByHarness $AdditionalArguments
$binaryArtifactEvidenceBefore = Get-TestedBinaryArtifactEvidence $executableFull
$inputEvidenceBefore = Get-FileDigest $inputFull
$harnessSelfTestRequested = $env:VHSDECODE_CUDA_HARNESS_SELF_TEST -eq '1'
$harnessSelfTestMode = $false
if ($harnessSelfTestRequested) {
    $expectedSelfTestDecoder = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot 'tests\Fake-CudaQualificationDecoder.ps1'))
    $currentPowerShell = [IO.Path]::GetFullPath((Get-Process -Id $PID).Path)
    $candidateSelfTestDecoder = if ($AdditionalArguments.Count -gt 0) {
        [IO.Path]::GetFullPath($AdditionalArguments[0])
    } else {
        $null
    }
    $harnessSelfTestMode =
        $executableFull -ieq $currentPowerShell -and
        $DecodeCommand -ieq '-File' -and
        $candidateSelfTestDecoder -ieq $expectedSelfTestDecoder
    if (-not $harnessSelfTestMode) {
        throw 'VHSDECODE_CUDA_HARNESS_SELF_TEST is restricted to the repository fake-decoder regression.'
    }
}
$rawTbcFloat32 = @($AdditionalArguments | Where-Object {
        $_ -ieq '--export_raw_tbc'
    }).Count -gt 0

New-Item -ItemType Directory -Path $outputRootFull -Force | Out-Null
$runName = 'qualification-{0}-{1}' -f @(
    [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'),
    [Guid]::NewGuid().ToString('N').Substring(0, 8)
)
$runRoot = Join-Path $outputRootFull $runName
New-Item -ItemType Directory -Path $runRoot | Out-Null

$outputsRoot = Join-Path $runRoot 'outputs'
$diagnosticsRoot = Join-Path $runRoot 'diagnostics'
New-Item -ItemType Directory -Path $outputsRoot | Out-Null
New-Item -ItemType Directory -Path $diagnosticsRoot | Out-Null

$nvidiaSmi = Get-NvidiaSmiPath
$cpuRun = Invoke-DecodeRun `
    -Backend cpu `
    -Executable $executableFull `
    -Command $DecodeCommand `
    -InputPath $inputFull `
    -OutputRoot (Join-Path $outputsRoot 'cpu') `
    -DiagnosticsRoot (Join-Path $diagnosticsRoot 'cpu') `
    -BaseName $OutputBaseName `
    -ExtraArguments $AdditionalArguments `
    -Device $CudaDevice `
    -NvidiaSmiPath $nvidiaSmi `
    -SampleIntervalMs $GpuSampleIntervalMs
$cudaRun = Invoke-DecodeRun `
    -Backend cuda `
    -Executable $executableFull `
    -Command $DecodeCommand `
    -InputPath $inputFull `
    -OutputRoot (Join-Path $outputsRoot 'cuda') `
    -DiagnosticsRoot (Join-Path $diagnosticsRoot 'cuda') `
    -BaseName $OutputBaseName `
    -ExtraArguments $AdditionalArguments `
    -Device $CudaDevice `
    -NvidiaSmiPath $nvidiaSmi `
    -SampleIntervalMs $GpuSampleIntervalMs
$binaryArtifactEvidenceAfter = Get-TestedBinaryArtifactEvidence $executableFull
$inputEvidenceAfter = Get-FileDigest $inputFull
$binaryArtifactsUnchanged =
    $binaryArtifactEvidenceBefore.fingerprintSha256 -eq
    $binaryArtifactEvidenceAfter.fingerprintSha256
$binaryArtifactEvidenceQualificationMatch = $binaryArtifactsUnchanged -and
    ($harnessSelfTestMode -or
        ($binaryArtifactEvidenceBefore.requiredSidecarSetPresent -and
            $binaryArtifactEvidenceBefore.manifestValidation.valid))
$inputUnchanged = $null -ne $inputEvidenceBefore -and
    $null -ne $inputEvidenceAfter -and
    $inputEvidenceBefore.sizeBytes -eq $inputEvidenceAfter.sizeBytes -and
    $inputEvidenceBefore.sha256 -eq $inputEvidenceAfter.sha256

$fileComparisons = @(Compare-OutputInventories `
    -CpuFiles $cpuRun.outputFiles `
    -CudaFiles $cudaRun.outputFiles `
    -CpuRoot $cpuRun.outputDirectory `
    -CudaRoot $cudaRun.outputDirectory `
    -RawTbcFloat32 $rawTbcFloat32)
$rawMismatches = @($fileComparisons | Where-Object status -ne 'match')
$disallowedMismatches = @($fileComparisons | Where-Object qualificationMatch -ne $true)
$stdoutComparison = Compare-DiagnosticDigests $cpuRun.stdout $cudaRun.stdout
$stderrComparison = Compare-DiagnosticDigests $cpuRun.stderr $cudaRun.stderr
$diagnosticsQualificationMatch = $stdoutComparison.qualificationMatch -and
    $stderrComparison.qualificationMatch
$backendSelectionQualificationMatch =
    $cpuRun.backendSelectionEvidence.matchesExpectedBackend -eq $true -and
    $cudaRun.backendSelectionEvidence.matchesExpectedBackend -eq $true
$dataOutputComparisons = @($fileComparisons | Where-Object {
        [IO.Path]::GetExtension($_.relativePath) -ine '.log'
    })
$commonDataOutputs = @($dataOutputComparisons | Where-Object {
        $null -ne $_.cpuSizeBytes -and $null -ne $_.cudaSizeBytes
    })
$commonNonEmptyDataOutputs = @($commonDataOutputs | Where-Object {
        $_.cpuSizeBytes -gt 0 -and $_.cudaSizeBytes -gt 0
    })
$standardDecodeCommand = if ([string]::IsNullOrWhiteSpace($DecodeCommand)) {
    $null
} else {
    $DecodeCommand.Trim().ToLowerInvariant()
}
$requiredPrimaryPath = if ($standardDecodeCommand -in @('vhs', 'cvbs', 'ld')) {
    "$OutputBaseName.tbc"
} else {
    $null
}
$requiredPrimaryOutput = if ($null -eq $requiredPrimaryPath) {
    $null
} else {
    @($commonNonEmptyDataOutputs | Where-Object {
            $_.relativePath -ieq $requiredPrimaryPath
        } | Select-Object -First 1)
}
$requiredPrimaryOutputPresent = $null -eq $requiredPrimaryPath -or
    $requiredPrimaryOutput.Count -eq 1
$requiredOutputEvidence = [pscustomobject][ordered]@{
    requireAtLeastOneNonLogOutput = $true
    requireNonEmptyOutput = $true
    requiredPrimaryPath = $requiredPrimaryPath
    comparedNonLogPathCount = $dataOutputComparisons.Count
    commonNonLogPathCount = $commonDataOutputs.Count
    commonNonEmptyNonLogPathCount = $commonNonEmptyDataOutputs.Count
    requiredPrimaryOutputPresent = $requiredPrimaryOutputPresent
    qualificationMatch = $commonNonEmptyDataOutputs.Count -gt 0 -and
        $requiredPrimaryOutputPresent
}
$speedup = if ($cudaRun.wallSeconds -gt 0) {
    $cpuRun.wallSeconds / $cudaRun.wallSeconds
} else {
    $null
}
$meetsNumericSpeedupThreshold = $null -ne $speedup -and $speedup -ge 1.25
$releaseGateBlockers = @(
    [pscustomobject][ordered]@{
        id = 'cpu-threshold-guard-band-not-enabled'
        comparison = 'CPU recomputation for compatibility-sensitive near-threshold decisions'
        currentRequirement = 'explicit CUDA qualification only'
    },
    [pscustomobject][ordered]@{
        id = 'representative-vhs-ld-suite-not-aggregated'
        comparison = 'Representative real-capture VHS and LD compatibility/performance suite'
        currentRequirement = 'both families must independently meet every compatibility check and >=1.25x speedup'
    }
)
$qualificationPassed = $cpuRun.exitCode -eq 0 -and
    $cudaRun.exitCode -eq 0 -and
    $binaryArtifactEvidenceQualificationMatch -and
    $inputUnchanged -and
    $backendSelectionQualificationMatch -and
    $requiredOutputEvidence.qualificationMatch -and
    $disallowedMismatches.Count -eq 0 -and
    $diagnosticsQualificationMatch
$report = [pscustomobject][ordered]@{
    schemaVersion = 4
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    qualificationDirectory = $runRoot
    decodeExecutable = $executableFull
    binaryArtifactEvidence = [pscustomobject][ordered]@{
        before = $binaryArtifactEvidenceBefore
        after = $binaryArtifactEvidenceAfter
        unchangedDuringQualification = $binaryArtifactsUnchanged
        harnessSelfTestMode = $harnessSelfTestMode
        qualificationMatch = $binaryArtifactEvidenceQualificationMatch
    }
    decodeCommand = $DecodeCommand
    inputFile = [pscustomobject][ordered]@{
        before = $inputEvidenceBefore
        after = $inputEvidenceAfter
        unchangedDuringQualification = $inputUnchanged
        qualificationMatch = $inputUnchanged
    }
    additionalArguments = $AdditionalArguments
    cudaDevice = $CudaDevice
    cudaVisibleDevices = $env:CUDA_VISIBLE_DEVICES
    comparisonPolicy = [pscustomobject][ordered]@{
        integerOutputs = [pscustomobject][ordered]@{
            paths = @('*.tbc', '*_chroma.tbc', '*.pcm', '*.efm', '*.tbc.ldf')
            sampleFormats = [pscustomobject][ordered]@{
                tbcAndChroma = 'uint16-little-endian'
                pcmEfmAndLdRfTbc = 'int16-little-endian'
            }
            requireEqualLength = $true
            maximumAbsoluteDifferenceLsb = 1
            maximumDifferingSampleRate = 0.0001
            rawTbcFloat32Excluded = $rawTbcFloat32
        }
        rawTbcFloat32 = [pscustomobject][ordered]@{
            enabledByArguments = $rawTbcFloat32
            sampleFormat = 'float32-little-endian'
            maximumNormalizedAbsoluteError = 2e-6
            maximumNormalizedRootMeanSquareError = 2e-7
            requireMatchingNaNAndInfinityLayout = $true
        }
        json = [pscustomobject][ordered]@{
            qualificationRequirement = 'root metadata plus ordered fileLoc-aligned field metadata'
            fileLocAlignedLogicalComparisonImplemented = $true
        }
        sqlite = [pscustomobject][ordered]@{
            qualificationRequirement = 'user_version, logical schema, table metadata, and unordered logical row multisets'
            logicalRowComparisonImplemented = $true
        }
    }
    runs = @($cpuRun, $cudaRun)
    performance = [pscustomobject][ordered]@{
        cpuWallSeconds = $cpuRun.wallSeconds
        cudaWallSeconds = $cudaRun.wallSeconds
        cudaSpeedup = $speedup
        releaseThreshold = 1.25
        meetsNumericThreshold = $meetsNumericSpeedupThreshold
        measurementClass = 'single-sequential-run-informational'
        eligibleForReleaseDecision = $false
        meetsReleaseThreshold = $false
        releaseDecisionReason = 'The release gate requires repeated cold/warm representative VHS and LD runs, not one sequential sample.'
    }
    consoleComparison = [pscustomobject][ordered]@{
        stdout = $stdoutComparison
        stderr = $stderrComparison
        qualificationMatch = $diagnosticsQualificationMatch
    }
    backendSelectionComparison = [pscustomobject][ordered]@{
        cpu = $cpuRun.backendSelectionEvidence
        cuda = $cudaRun.backendSelectionEvidence
        qualificationMatch = $backendSelectionQualificationMatch
    }
    outputComparison = [pscustomobject][ordered]@{
        allFilesRawMatch = $rawMismatches.Count -eq 0
        allFilesQualificationMatch = $disallowedMismatches.Count -eq 0
        comparedPathCount = $fileComparisons.Count
        rawMismatchCount = $rawMismatches.Count
        disallowedMismatchCount = $disallowedMismatches.Count
        files = $fileComparisons
    }
    requiredOutputEvidence = $requiredOutputEvidence
    qualificationPassed = $qualificationPassed
    releaseGateCoverage = [pscustomobject][ordered]@{
        complete = $false
        implementedComparisonCoverageComplete = $true
        blockers = $releaseGateBlockers
    }
    fullReleaseGatePassed = $false
}

$reportPath = Join-Path $runRoot 'qualification.json'
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
Write-Host "CUDA qualification report: $reportPath"
Write-Output $reportPath
