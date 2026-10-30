#nullable enable
using System.Collections.Generic;

namespace Backend;

public sealed class InventoryCollectionSummary
{
    public required string PartitionId { get; init; }
    public required string Label { get; init; }
    public required string DataProfileId { get; init; }
    public bool Active { get; init; } = true;
    public int SortOrder { get; init; }

    /// <summary>When true, the mobile library lists this partition. Absent on the document means false.</summary>
    public bool MobileAllowed { get; init; }
}

public readonly record struct InventoryCollectionListResult(
    IReadOnlyList<InventoryCollectionSummary>? Collections,
    string? ErrorMessage)
{
    public bool Ok => Collections is not null;

    public static InventoryCollectionListResult Success(IReadOnlyList<InventoryCollectionSummary> collections)
        => new(collections, null);

    public static InventoryCollectionListResult Fail(string message) => new(null, message);
}

public readonly record struct InventoryCollectionCommandResult(bool Ok, string? ErrorMessage)
{
    public static InventoryCollectionCommandResult Success() => new(true, null);
    public static InventoryCollectionCommandResult Fail(string message) => new(false, message);
}

/// <summary>
/// New partition. The id is assigned by <see cref="DocumentIdGenerator"/> from <see cref="Label"/>.
/// </summary>
public readonly record struct CreateInventoryCollectionRequest(
    string Label,
    string DataProfileId,
    bool MobileAllowed = false);

/// <summary>
/// Renames an existing partition and stores whether phones may list it.
/// The partition id and data profile stay fixed.
/// </summary>
public readonly record struct UpdateInventoryCollectionRequest(
    string PartitionId,
    string Label,
    bool MobileAllowed);

public sealed class DataProfileSummary
{
    public required string ProfileId { get; init; }
    public required string Label { get; init; }
    public IReadOnlyList<DataProfileFieldSpec> Fields { get; init; } = [];
}

public readonly record struct DataProfileListResult(
    IReadOnlyList<DataProfileSummary>? Profiles,
    string? ErrorMessage)
{
    public bool Ok => Profiles is not null;

    public static DataProfileListResult Success(IReadOnlyList<DataProfileSummary> profiles) => new(profiles, null);
    public static DataProfileListResult Fail(string message) => new(null, message);
}

public readonly record struct DataProfileCommandResult(
    bool Ok,
    string? ErrorMessage,
    DataProfileSummary? Profile = null)
{
    public static DataProfileCommandResult Success(DataProfileSummary profile) => new(true, null, profile);
    public static DataProfileCommandResult Fail(string message) => new(false, message);
}

public readonly record struct DataProfileDeleteResult(
    bool Ok,
    string? ErrorMessage,
    int CollectionsRemoved = 0)
{
    public static DataProfileDeleteResult Success(int collectionsRemoved)
        => new(true, null, collectionsRemoved);

    public static DataProfileDeleteResult Fail(string message) => new(false, message, 0);
}
