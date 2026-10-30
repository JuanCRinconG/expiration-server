#nullable enable
using System;
using System.Collections.Generic;
using Google.Cloud.Firestore;

namespace Backend.Firebase;

/// <summary>Parse <c>--SYSTEM--/alert</c> for notif-server and admin preview.</summary>
public static class NotifAlertFirestoreSupport
{
    public static NotifAlertConfigResult ParseAlertDocument(DocumentSnapshot snap)
    {
        if (!snap.Exists)
            return NotifAlertConfigResult.Fail("Notification alert config was not found.");

        Dictionary<string, object> data = snap.ToDictionary();
        var sources = new List<NotifAlertSource>();
        if (data.TryGetValue(DatabaseTopology.NotifAlertFields.Sources, out object? sourcesRaw)
            && sourcesRaw is IEnumerable<object> sourceList)
        {
            foreach (object entry in sourceList)
            {
                if (entry is not Dictionary<string, object> sourceMap)
                    continue;

                NotifAlertSource? parsed = ParseSource(sourceMap);
                if (parsed is not null)
                    sources.Add(parsed);
            }
        }

        var rules = new List<Dictionary<string, object>>();
        if (data.TryGetValue(DatabaseTopology.NotifAlertFields.Rules, out object? rulesRaw)
            && rulesRaw is IEnumerable<object> ruleList)
        {
            foreach (object entry in ruleList)
            {
                if (entry is Dictionary<string, object> ruleMap)
                    rules.Add(new Dictionary<string, object>(ruleMap));
            }
        }

        int version = 1;
        if (data.TryGetValue(DatabaseTopology.NotifAlertFields.Version, out object? versionRaw))
            version = InventoryFieldReaders.CoerceInt(versionRaw) ?? 1;

        string configId = InventoryFieldReaders.GetString(data, DatabaseTopology.NotifAlertFields.ConfigId)
            ?? DatabaseTopology.Documents.NotifAlert;

        string? windowSource = InventoryFieldReaders.GetString(data, DatabaseTopology.NotifAlertFields.NotifyWindowDaysSource);

        return NotifAlertConfigResult.Success(new NotifAlertConfig
        {
            ConfigId = configId,
            Version = version,
            NotifyWindowDaysSource = windowSource,
            Sources = sources,
            Rules = rules
        });
    }

    private static NotifAlertSource? ParseSource(Dictionary<string, object> map)
    {
        string? path = InventoryFieldReaders.GetString(map, DatabaseTopology.NotifSourceFields.CollectionPath);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string activeField = InventoryFieldReaders.GetString(map, DatabaseTopology.NotifSourceFields.ActiveFieldPath)
            ?? DatabaseTopology.ItemFields.Active;

        bool activeValue = true;
        if (map.TryGetValue(DatabaseTopology.NotifSourceFields.ActiveValue, out object? activeRaw)
            && activeRaw is bool activeBool)
        {
            activeValue = activeBool;
        }

        return new NotifAlertSource
        {
            PartitionId = InventoryFieldReaders.GetString(map, DatabaseTopology.NotifSourceFields.PartitionId) ?? string.Empty,
            CollectionPath = path.Trim(),
            ActiveFieldPath = activeField,
            ActiveValue = activeValue
        };
    }
}
