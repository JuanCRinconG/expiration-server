#nullable enable
using System;
using System.Collections.Generic;

namespace Backend;

/// <summary>Universal item fields on <c>--DATA--/{partition}/items/{id}</c>.</summary>
public readonly record struct ItemRecord(
    string DataId,
    string DisplayName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool Active,
    string? CreatedBy = null,
    string StorageProvider = "",
    string PdfPath = "",
    string? FileName = null,
    string? ContentType = null);

/// <summary>Desktop read model: item plus a PDF URL resolved at read time.</summary>
public readonly record struct ItemDetail(
    ItemRecord Item,
    string? ResolvedOpenUrl)
{
    public string DataId => Item.DataId;
    public string DisplayName => Item.DisplayName;
    public DateTime CreatedAt => Item.CreatedAt;
    public DateTime UpdatedAt => Item.UpdatedAt;
    public bool Active => Item.Active;
    public string? CreatedBy => Item.CreatedBy;

    public string StorageProvider => Item.StorageProvider;
    public string StoragePath => Item.PdfPath;
    public string? FileName => Item.FileName;
    public string? ContentType => Item.ContentType;
    public string FileUrl => ResolvedOpenUrl ?? string.Empty;
}

public readonly record struct ItemDetailResult(ItemDetail? Detail, string? ErrorMessage)
{
    public bool Ok => Detail is not null;

    public static ItemDetailResult Success(ItemDetail detail) => new(detail, null);
    public static ItemDetailResult Fail(string message) => new(null, message);
}

public readonly record struct ItemDetailListResult(
    IReadOnlyList<ItemDetail>? Details,
    string? ErrorMessage,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>? ProfileFields = null)
{
    public bool Ok => Details is not null;

    public static ItemDetailListResult Success(
        IReadOnlyList<ItemDetail> details,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>? profileFields = null)
        => new(details, null, profileFields);

    public static ItemDetailListResult Fail(string message) => new(null, message);
}

/// <summary>
/// Display name plus profile field values for one item.
/// Profile values stay a field bag. They are not properties of <see cref="ItemDetail"/>.
/// </summary>
public readonly record struct ItemProfileSnapshot(
    string DataId,
    string DisplayName,
    IReadOnlyDictionary<string, object> ProfileFields);

public readonly record struct ItemProfileResult(ItemProfileSnapshot? Snapshot, string? ErrorMessage)
{
    public bool Ok => Snapshot is not null;

    public static ItemProfileResult Success(ItemProfileSnapshot snapshot) => new(snapshot, null);
    public static ItemProfileResult Fail(string message) => new(null, message);
}

public sealed class NotifAlertSource
{
    public string PartitionId { get; init; } = string.Empty;
    public string CollectionPath { get; init; } = string.Empty;
    public string ActiveFieldPath { get; init; } = DatabaseTopology.ItemFields.Active;
    public bool ActiveValue { get; init; } = true;
}

public sealed class NotifAlertConfig
{
    public string ConfigId { get; init; } = DatabaseTopology.Documents.NotifAlert;
    public int Version { get; init; }

    /// <summary>Legacy alert flag; date rules use each rule&apos;s <c>leadDays</c> in Firestore.</summary>
    public string? NotifyWindowDaysSource { get; init; }

    public IReadOnlyList<NotifAlertSource> Sources { get; init; } = [];
    public IReadOnlyList<Dictionary<string, object>> Rules { get; init; } = [];
}

/// <summary>One matched admin rule against one inventory item (alert row).</summary>
public readonly record struct NotifAlertHit(
    string DataId,
    string DisplayName,
    string RuleId,
    string Section,
    string Detail);

public readonly record struct NotifAlertConfigResult(NotifAlertConfig? Config, string? ErrorMessage)
{
    public bool Ok => Config is not null;

    public static NotifAlertConfigResult Success(NotifAlertConfig config) => new(config, null);
    public static NotifAlertConfigResult Fail(string message) => new(null, message);
}

public readonly record struct InventoryItemDocument(
    string DocumentId,
    IReadOnlyDictionary<string, object> Fields);

public readonly record struct InventoryItemsResult(IReadOnlyList<InventoryItemDocument>? Items, string? ErrorMessage)
{
    public bool Ok => Items is not null;

    public static InventoryItemsResult Success(IReadOnlyList<InventoryItemDocument> items) => new(items, null);
    public static InventoryItemsResult Fail(string message) => new(null, message);
}

/// <summary>
/// Inventory write for <c>--DATA--/{partition}/items/{id}</c>.
/// An optional local PDF is uploaded first and its pointer is stored on that item.
/// </summary>
public readonly record struct ItemDataSaveRequest(
    string PartitionId,
    string DisplayName,
    IReadOnlyDictionary<string, object?>? ProfileFields = null,
    string? DataId = null,
    string? CreatedByUid = null,
    string? LocalPdfPath = null);

public readonly record struct ItemSaveResult(string? DataId, string? ErrorMessage)
{
    public bool Ok => !string.IsNullOrWhiteSpace(DataId);

    public static ItemSaveResult Success(string itemId)
        => new(itemId, null);

    public static ItemSaveResult Fail(string message)
        => new(null, message);
}

public readonly record struct ItemCommandResult(bool Ok, string? ErrorMessage)
{
    public static ItemCommandResult Success() => new(true, null);
    public static ItemCommandResult Fail(string message) => new(false, message);
}

public readonly record struct NotifConfigCommandResult(bool Ok, string? ErrorMessage)
{
    public static NotifConfigCommandResult Success() => new(true, null);
    public static NotifConfigCommandResult Fail(string message) => new(false, message);
}
