#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Google.Cloud.Firestore;

namespace Backend.Firebase;

/// <summary>
/// Shared document pipeline for inventory items: validate, build, parse, and address.
/// The PDF pointer is stored on the item. PDF bytes stay in external storage.
/// </summary>
public static class InventoryDocumentPipeline
{
    public static string? ValidateDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "Display name is required.";

        return null;
    }

    public static string? ValidatePdfFile(string? localPdfPath)
    {
        if (string.IsNullOrWhiteSpace(localPdfPath))
            return null;

        if (!File.Exists(localPdfPath))
            return "The selected PDF file was not found.";

        if (!string.Equals(Path.GetExtension(localPdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            return "Only PDF files are allowed.";

        return null;
    }

    public static string? ValidateProfileFields(IReadOnlyDictionary<string, object?>? profileFields)
    {
        if (profileFields is null)
            return null;

        foreach (KeyValuePair<string, object?> pair in profileFields)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                return "Profile field key is required.";

            if (DatabaseTopology.IsItemCoreField(pair.Key))
                return $"Profile field '{pair.Key.Trim()}' uses a reserved item field name.";

            if (pair.Value is null or string or bool or DateTime)
                continue;

            if (pair.Value is double or float or long or int or decimal)
                continue;

            return $"Profile field '{pair.Key.Trim()}' has an unsupported value.";
        }

        return null;
    }

    public static ItemDetail? ParseItemDetail(
        string documentId,
        IReadOnlyDictionary<string, object> itemData,
        string? resolvedOpenUrl = null)
    {
        ItemRecord? item = ParseItem(documentId, itemData);
        if (item is null)
            return null;

        return new ItemDetail(item.Value, resolvedOpenUrl);
    }

    public static Dictionary<string, object> BuildItemDocument(
        string dataId,
        string displayName,
        DateTime nowUtc,
        string? createdByUid,
        bool isCreate,
        IReadOnlyDictionary<string, object?>? profileFields = null,
        string? storageProvider = null,
        string? pdfPath = null,
        string? fileName = null,
        string? contentType = null)
    {
        var map = new Dictionary<string, object>
        {
            [DatabaseTopology.ItemCoreFields.DataId] = dataId,
            [DatabaseTopology.ItemCoreFields.DisplayName] = displayName.Trim(),
            [DatabaseTopology.ItemCoreFields.Active] = true,
            [DatabaseTopology.ItemCoreFields.UpdatedAt] = Timestamp.FromDateTime(nowUtc)
        };

        if (isCreate)
            map[DatabaseTopology.ItemCoreFields.CreatedAt] = Timestamp.FromDateTime(nowUtc);

        if (!string.IsNullOrWhiteSpace(createdByUid))
            map[DatabaseTopology.ItemCoreFields.CreatedBy] = createdByUid.Trim();

        AppendPdfPointer(map, storageProvider, pdfPath, fileName, contentType);
        AppendProfileFields(map, profileFields, deleteEmpty: false);
        return map;
    }

    public static Dictionary<string, object> BuildItemDataUpdates(
        string displayName,
        DateTime nowUtc,
        IReadOnlyDictionary<string, object?>? profileFields,
        string? storageProvider = null,
        string? pdfPath = null,
        string? fileName = null,
        string? contentType = null)
    {
        var map = new Dictionary<string, object>
        {
            [DatabaseTopology.ItemCoreFields.DisplayName] = displayName.Trim(),
            [DatabaseTopology.ItemCoreFields.UpdatedAt] = Timestamp.FromDateTime(nowUtc)
        };

        AppendPdfPointer(map, storageProvider, pdfPath, fileName, contentType);
        AppendProfileFields(map, profileFields, deleteEmpty: true);
        return map;
    }

    /// <summary>Profile values only. Timestamps become UTC <see cref="DateTime"/> so callers stay off the Firestore SDK.</summary>
    public static Dictionary<string, object> ExtractProfileFields(IReadOnlyDictionary<string, object> itemData)
    {
        var bag = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object> pair in itemData)
        {
            if (DatabaseTopology.IsItemCoreField(pair.Key) || pair.Value is null)
                continue;

            switch (pair.Value)
            {
                case Timestamp timestamp:
                    bag[pair.Key] = DateTime.SpecifyKind(timestamp.ToDateTime(), DateTimeKind.Utc);
                    break;
                case DateTime dateTime:
                    bag[pair.Key] = DateTime.SpecifyKind(
                        dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime(),
                        DateTimeKind.Utc);
                    break;
                case bool or string:
                    bag[pair.Key] = pair.Value;
                    break;
                case double or float or long or int or decimal:
                    bag[pair.Key] = Convert.ToDouble(pair.Value, CultureInfo.InvariantCulture);
                    break;
                default:
                    string? text = pair.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        bag[pair.Key] = text;
                    break;
            }
        }

        return bag;
    }

    public static DocumentReference Partition(FirestoreDb db, string partitionId)
        => db.Collection(DatabaseTopology.Collections.DataRoot).Document(partitionId.Trim());

    public static CollectionReference Items(FirestoreDb db, string partitionId)
        => Partition(db, partitionId).Collection(DatabaseTopology.Subcollections.Items);

    public static DocumentReference Item(FirestoreDb db, string partitionId, string dataId)
        => Items(db, partitionId).Document(dataId);

    public static DocumentReference Alert(FirestoreDb db)
        => db.Collection(DatabaseTopology.Collections.System)
            .Document(DatabaseTopology.Documents.NotifAlert);

    private static ItemRecord? ParseItem(string documentId, IReadOnlyDictionary<string, object> itemData)
    {
        string itemId = InventoryFieldReaders.ReadItemId(itemData) ?? documentId;
        string? displayName = InventoryFieldReaders.GetString(itemData, DatabaseTopology.ItemCoreFields.DisplayName);
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        DateTime createdAt = InventoryFieldReaders.GetDateUtc(itemData, DatabaseTopology.ItemCoreFields.CreatedAt)
            ?? DateTime.UnixEpoch;
        DateTime updatedAt = InventoryFieldReaders.GetDateUtc(itemData, DatabaseTopology.ItemCoreFields.UpdatedAt)
            ?? createdAt;

        if (!InventoryFieldReaders.TryGetBool(itemData, DatabaseTopology.ItemCoreFields.Active, out bool active))
            active = true;

        string? createdBy = InventoryFieldReaders.GetString(itemData, DatabaseTopology.ItemCoreFields.CreatedBy);
        string pdfPath = InventoryFieldReaders.GetString(itemData, DatabaseTopology.ItemCoreFields.PdfPath) ?? string.Empty;
        string storageProvider = InventoryFieldReaders.GetString(itemData, DatabaseTopology.ItemCoreFields.StorageProvider)
            ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(pdfPath) && string.IsNullOrWhiteSpace(storageProvider))
            storageProvider = StorageProviderKeys.OneDrive;

        return new ItemRecord(
            itemId,
            displayName.Trim(),
            createdAt,
            updatedAt,
            active,
            createdBy,
            storageProvider,
            pdfPath,
            InventoryFieldReaders.GetString(itemData, DatabaseTopology.ItemCoreFields.FileName),
            InventoryFieldReaders.GetString(itemData, DatabaseTopology.ItemCoreFields.ContentType));
    }

    private static void AppendPdfPointer(
        Dictionary<string, object> map,
        string? storageProvider,
        string? pdfPath,
        string? fileName,
        string? contentType)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
            return;

        map[DatabaseTopology.ItemCoreFields.StorageProvider] = string.IsNullOrWhiteSpace(storageProvider)
            ? StorageProviderKeys.OneDrive
            : storageProvider.Trim();
        map[DatabaseTopology.ItemCoreFields.PdfPath] = pdfPath.Trim();

        if (!string.IsNullOrWhiteSpace(fileName))
            map[DatabaseTopology.ItemCoreFields.FileName] = fileName.Trim();
        if (!string.IsNullOrWhiteSpace(contentType))
            map[DatabaseTopology.ItemCoreFields.ContentType] = contentType.Trim();
    }

    private static void AppendProfileFields(
        Dictionary<string, object> map,
        IReadOnlyDictionary<string, object?>? profileFields,
        bool deleteEmpty)
    {
        if (profileFields is null)
            return;

        foreach (KeyValuePair<string, object?> pair in profileFields)
        {
            object? stored = ToStoredProfileValue(pair.Value);
            if (stored is null)
            {
                if (deleteEmpty)
                    map[pair.Key] = FieldValue.Delete;
                continue;
            }

            map[pair.Key] = stored;
        }
    }

    public static InventoryCollectionSummary? ParsePartitionSummary(string documentId, Dictionary<string, object> data)
    {
        string partitionId = InventoryFieldReaders.GetString(data, DatabaseTopology.PartitionFields.PartitionId)
            ?? documentId;
        string? label = InventoryFieldReaders.GetString(data, DatabaseTopology.PartitionFields.Label);
        string? profileId = InventoryFieldReaders.GetString(data, DatabaseTopology.PartitionFields.DataProfileId);
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(profileId))
            return null;

        data.TryGetValue(DatabaseTopology.PartitionFields.SortOrder, out object? sortRaw);
        int sortOrder = InventoryFieldReaders.CoerceInt(sortRaw) ?? 0;

        bool active = true;
        if (InventoryFieldReaders.TryGetBool(data, DatabaseTopology.PartitionFields.Active, out bool activeValue))
            active = activeValue;

        bool mobileAllowed = false;
        if (InventoryFieldReaders.TryGetBool(data, DatabaseTopology.PartitionFields.MobileAllowed, out bool mobileAllowedValue))
            mobileAllowed = mobileAllowedValue;

        return new InventoryCollectionSummary
        {
            PartitionId = partitionId.Trim(),
            Label = label.Trim(),
            DataProfileId = profileId.Trim(),
            Active = active,
            SortOrder = sortOrder,
            MobileAllowed = mobileAllowed
        };
    }

    private static object? ToStoredProfileValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            case bool flag:
                return flag;
            case DateTime dateTime:
                DateTime utc = dateTime.Kind == DateTimeKind.Utc
                    ? dateTime
                    : dateTime.ToUniversalTime();
                return Timestamp.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
            case double or float or long or int or decimal:
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }
}
