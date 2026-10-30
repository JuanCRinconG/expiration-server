#nullable enable
using System;
using System.Collections.Generic;
using Google.Cloud.Firestore;

namespace Backend;

/// <summary>Reads typed values from inventory item field bags.</summary>
public static class InventoryFieldReaders
{
    public static string? GetString(IReadOnlyDictionary<string, object> data, string key)
        => data.TryGetValue(key, out object? value) ? value?.ToString() : null;

    public static string? ReadItemId(IReadOnlyDictionary<string, object> data)
        => GetString(data, DatabaseTopology.ItemCoreFields.DataId);

    public static DateTime? GetDateUtc(IReadOnlyDictionary<string, object> data, string fieldPath)
    {
        if (!data.TryGetValue(fieldPath, out object? raw) || raw is null)
            return null;

        return raw switch
        {
            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc).Date,
            Timestamp ts => DateTime.SpecifyKind(ts.ToDateTime(), DateTimeKind.Utc).Date,
            _ => null
        };
    }

    public static bool TryGetBool(IReadOnlyDictionary<string, object> data, string fieldPath, out bool value)
    {
        value = default;
        if (!data.TryGetValue(fieldPath, out object? raw) || raw is null)
            return false;

        if (raw is bool b)
        {
            value = b;
            return true;
        }

        return false;
    }

    public static bool TryGetNumber(IReadOnlyDictionary<string, object> data, string fieldPath, out double value)
    {
        value = default;
        if (!data.TryGetValue(fieldPath, out object? raw) || raw is null)
            return false;

        switch (raw)
        {
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case long l:
                value = l;
                return true;
            case int i:
                value = i;
                return true;
            default:
                return false;
        }
    }

    public static int? CoerceInt(object? raw) => raw switch
    {
        int i => i,
        long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
        double d when d >= int.MinValue && d <= int.MaxValue => (int)d,
        _ => null
    };
}
