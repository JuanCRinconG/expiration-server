#nullable enable
using System;
using System.Collections.Generic;
using Google.Cloud.Firestore;

namespace Backend;

/// <summary>
/// Firestore platform layout: collection ids, document ids, subcollections, and required field names.
/// Single source of truth for desktop, mobile, and notif-server.
/// </summary>
public static class DatabaseTopology
{
    public const int SchemaVersion = 2;

    /// <summary>Top-level and nested collection identifiers.</summary>
    public static class Collections
    {
        public const string DataRoot = "--DATA--";
        public const string System = "--SYSTEM--";
    }

    /// <summary>Well-known document ids (within their parent collection).</summary>
    public static class Documents
    {
        /// <summary>Alert document under <see cref="Collections.System"/> (<c>sources[]</c> and <c>rules[]</c>).</summary>
        public const string NotifAlert = "alert";
    }

    /// <summary>Shared keys on platform ensure documents (config roots).</summary>
    public static class PlatformDocumentFields
    {
        public const string SchemaVersion = "schemaVersion";
        public const string Kind = "kind";
    }

    /// <summary><see cref="PlatformDocumentFields.Kind"/> values for <see cref="ConfigPaths"/> parent documents.</summary>
    public static class ConfigRootKinds
    {
        public const string DataProfiles = "dataProfiles";
        public const string InventoryReports = "inventoryReports";
    }

    /// <summary>Fixed subcollection names under partition docs.</summary>
    public static class Subcollections
    {
        public const string Items = "items";
    }

    /// <summary>Parent document ids under <see cref="Collections.System"/> that own a subcollection.</summary>
    public static class ConfigPaths
    {
        public const string DataProfiles = ConfigRootKinds.DataProfiles;
        public const string InventoryReports = ConfigRootKinds.InventoryReports;
    }

    /// <summary>Subcollections under the parent documents in <see cref="ConfigPaths"/>.</summary>
    public static class ConfigSubcollections
    {
        public const string DataProfileDocs = "profiles";
        public const string InventoryReportDocs = "reports";
    }

    /// <summary>JSON keys on <c>--SYSTEM--/dataProfiles/profiles/{profileId}</c> documents.</summary>
    public static class DataProfileDocumentFields
    {
        public const string ProfileId = "profileId";
        public const string Label = "label";
        public const string Version = "version";
        public const string Fields = "fields";
    }

    /// <summary>Keys on each map inside <see cref="DataProfileDocumentFields.Fields"/>.</summary>
    public static class DataProfileFieldSpecFields
    {
        public const string Key = "key";
        public const string Label = "label";
        public const string Kind = "kind";
        public const string Required = "required";
        public const string ShowOnCard = "showOnCard";
    }

    public static readonly string[] ItemCoreFieldKeys =
    [
        ItemCoreFields.DataId,
        ItemCoreFields.DisplayName,
        ItemCoreFields.Active,
        ItemCoreFields.CreatedAt,
        ItemCoreFields.UpdatedAt,
        ItemCoreFields.CreatedBy,
        ItemCoreFields.StorageProvider,
        ItemCoreFields.PdfPath,
        ItemCoreFields.FileName,
        ItemCoreFields.ContentType
    ];

    public static bool IsItemCoreField(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        for (int i = 0; i < ItemCoreFieldKeys.Length; i++)
        {
            if (ItemCoreFieldKeys[i] == key)
                return true;
        }

        return false;
    }

    /// <summary>Top-level collections touched by platform ensure (<c>PlatformBaselineEnsure</c>).</summary>
    public static IReadOnlyList<string> BaselineCollectionIds { get; } =
    [
        Collections.DataRoot,
        Collections.System
    ];

    public static string BuildItemsCollectionPath(string partitionId)
        => $"{Collections.DataRoot}/{partitionId.Trim()}/{Subcollections.Items}";

    /// <summary>Resolves <c>--DATA--/{partition}/items</c>.</summary>
    public static CollectionReference ResolveInventoryCollection(FirestoreDb db, string collectionPath)
    {
        if (!TryParseItemsPath(collectionPath, out string root, out string partitionId, out string items))
        {
            throw new ArgumentException(
                $"Unsupported inventory collection path '{collectionPath}'. "
                + $"Expected '{Collections.DataRoot}/{{partition}}/{Subcollections.Items}'.",
                nameof(collectionPath));
        }

        return db.Collection(root).Document(partitionId).Collection(items);
    }

    public static bool TryParseItemsPath(
        string collectionPath,
        out string root,
        out string partitionId,
        out string items)
    {
        root = string.Empty;
        partitionId = string.Empty;
        items = string.Empty;
        if (string.IsNullOrWhiteSpace(collectionPath))
            return false;

        string[] parts = collectionPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !IsInventoryRoot(parts[0]) || !string.Equals(parts[2], Subcollections.Items, StringComparison.Ordinal))
            return false;

        root = parts[0];
        partitionId = parts[1];
        items = parts[2];
        return true;
    }

    public static bool IsInventoryRoot(string segment)
        => string.Equals(segment, Collections.DataRoot, StringComparison.Ordinal);

    /// <summary>JSON keys for <c>--SYSTEM--/alert</c> rules (V1).</summary>
    public static class NotifRuleTypes
    {
        public const string DateWarnBefore = "dateWarnBefore";
        public const string BoolEquals = "boolEquals";
        public const string NumberOutOfRange = "numberOutOfRange";
    }

    /// <summary>Keys on each map inside <see cref="NotifAlertFields.Rules"/>.</summary>
    public static class NotifRuleFields
    {
        public const string Id = "id";
        public const string Type = "type";
        public const string FieldPath = "fieldPath";
        public const string PartitionId = "partitionId";
        public const string Section = "section";
        public const string LeadDays = "leadDays";
        public const string IncludeOverdueRelativeToToday = "includeOverdueRelativeToToday";
        public const string Value = "value";
        public const string Min = "min";
        public const string Max = "max";
    }

    /// <summary>Universal item fields on partition items. The PDF pointer lives here; bytes stay in external storage.</summary>
    public static class ItemCoreFields
    {
        public const string DataId = "dataId";
        public const string DisplayName = "displayName";
        public const string Active = "active";
        public const string CreatedAt = "createdAt";
        public const string UpdatedAt = "updatedAt";
        public const string CreatedBy = "createdBy";
        public const string StorageProvider = "storageProvider";
        public const string PdfPath = "pdfPath";
        public const string FileName = "fileName";
        public const string ContentType = "contentType";
    }

    /// <summary>Partition metadata doc fields under <see cref="Collections.DataRoot"/>.</summary>
    public static class PartitionFields
    {
        public const string PartitionId = "partitionId";
        public const string Label = "label";
        public const string SortOrder = "sortOrder";
        public const string DataProfileId = "dataProfileId";
        public const string Active = "active";

        /// <summary>When true, the mobile library lists this partition. Absent parses as false.</summary>
        public const string MobileAllowed = "mobileAllowed";
    }

    /// <summary><c>--SYSTEM--/alert</c> document keys.</summary>
    public static class NotifAlertFields
    {
        public const string ConfigId = "configId";
        public const string Version = "version";
        public const string NotifyWindowDaysSource = "notifyWindowDaysSource";
        public const string Sources = "sources";
        public const string Rules = "rules";
    }

    /// <summary>Keys within each <c>sources[]</c> entry.</summary>
    public static class NotifSourceFields
    {
        public const string PartitionId = "partitionId";
        public const string CollectionPath = "collectionPath";
        public const string ActiveFieldPath = "activeFieldPath";
        public const string ActiveValue = "activeValue";
    }

    /// <summary>Keys on <c>--SYSTEM--/inventoryReports/reports/{reportId}</c> template documents.</summary>
    public static class InventoryReportDocumentFields
    {
        public const string ReportId = "reportId";
        public const string Label = "label";
        public const string Sections = "sections";
    }

    /// <summary>Keys on each map inside <see cref="InventoryReportDocumentFields.Sections"/>.</summary>
    public static class InventoryReportSectionFields
    {
        public const string PartitionId = "partitionId";
        public const string Columns = "columns";
    }

    /// <summary>Item keys used by shared reads. Profile fields stay on the lab manager's data profile.</summary>
    public static class ItemFields
    {
        public const string DataId = ItemCoreFields.DataId;
        public const string DisplayName = ItemCoreFields.DisplayName;
        public const string Active = ItemCoreFields.Active;
        public const string CreatedAt = ItemCoreFields.CreatedAt;
        public const string UpdatedAt = ItemCoreFields.UpdatedAt;
        public const string CreatedBy = ItemCoreFields.CreatedBy;
        public const string StorageProvider = ItemCoreFields.StorageProvider;
        public const string PdfPath = ItemCoreFields.PdfPath;
        public const string FileName = ItemCoreFields.FileName;
        public const string ContentType = ItemCoreFields.ContentType;
    }
}
