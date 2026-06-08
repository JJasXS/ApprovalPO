using System.Data.Common;
using System.Globalization;

namespace ApprovalPO.Helpers;

/// <summary>Case-insensitive reads from Firebird <see cref="DbDataReader"/> result sets.</summary>
public static class FirebirdDataReaderHelper
{
    public static int GetInt32(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0)
                continue;
            if (reader.IsDBNull(ord))
                return 0;
            var v = reader.GetValue(ord);
            if (v is int i)
                return i;
            if (v is long l)
                return l > int.MaxValue ? int.MaxValue : (int)l;
            if (v is short s)
                return s;
            if (int.TryParse(v?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n;
        }

        return 0;
    }

    public static string? GetString(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0)
                continue;
            if (reader.IsDBNull(ord))
                return null;
            return reader.GetValue(ord)?.ToString();
        }

        return null;
    }

    public static decimal GetDecimal(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0)
                continue;
            if (reader.IsDBNull(ord))
                return 0m;
            var v = reader.GetValue(ord);
            if (v is decimal d)
                return d;
            return Convert.ToDecimal(v, CultureInfo.InvariantCulture);
        }

        return 0m;
    }

    public static DateTime? GetDateTime(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0)
                continue;
            if (reader.IsDBNull(ord))
                return null;
            var v = reader.GetValue(ord);
            if (v is DateTime dt)
                return dt.Date;
            if (DateTime.TryParse(v?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                return parsed.Date;
        }

        return null;
    }

    public static bool? GetBoolNullable(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0)
                continue;
            if (reader.IsDBNull(ord))
                return null;
            var v = reader.GetValue(ord);
            if (v is bool b)
                return b;
            if (v is short s)
                return s != 0;
            if (v is int i)
                return i != 0;
            if (v is string str)
            {
                var u = str.Trim().ToUpperInvariant();
                if (u is "T" or "Y" or "1" or "TRUE" or "YES")
                    return true;
                if (u is "F" or "N" or "0" or "FALSE" or "NO")
                    return false;
            }
            if (int.TryParse(v?.ToString(), out var n))
                return n != 0;
        }

        return null;
    }

    public static int TryGetOrdinal(DbDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
