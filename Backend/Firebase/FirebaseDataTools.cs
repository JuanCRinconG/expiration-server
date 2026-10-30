#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FirebaseAdmin;
using Google.Cloud.Firestore;

namespace Backend.Firebase;

/// <summary>
/// Firestore reads for desktop, mobile, and the notif spine.
/// Optional storage resolves a PDF open URL. A null storage argument leaves that URL empty.
/// </summary>
public sealed class FirebaseDataTools : DataTools
{
    private readonly StorageService? _storage;
    private FirestoreDb? _db;

    public FirebaseDataTools(StorageService? storage = null)
    {
        _storage = storage;
    }

    public void InitializeDatabase()
    {
        if (_db != null)
            return;

        FirebaseConnect.InitializeFirebase();

        string? projectId = FirebaseApp.DefaultInstance?.Options?.ProjectId
            ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
            ?? Environment.GetEnvironmentVariable("GCLOUD_PROJECT")
            ?? Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID");

        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(
                "Firebase/Firestore project id is missing. Initialize FirebaseConnect or set FIREBASE_PROJECT_ID.");

        _db = FirestoreDb.Create(projectId);
    }

    public async Task<NotifAlertConfigResult> GetAlertConfigAsync()
    {
        try
        {
            DocumentSnapshot snap = await InventoryDocumentPipeline.Alert(GetFirestore()).GetSnapshotAsync();

            return NotifAlertFirestoreSupport.ParseAlertDocument(snap);
        }
        catch (Exception ex)
        {
            BackendLog.Error($"GetAlertConfigAsync failed: {ex.Message}");
            return NotifAlertConfigResult.Fail("Could not load notification alert config.");
        }
    }

    public async Task<ItemDetailResult> GetByDataIdAsync(string dataId)
    {
        if (string.IsNullOrWhiteSpace(dataId))
            return ItemDetailResult.Fail("Item id is required.");

        string id = dataId.Trim();

        try
        {
            QuerySnapshot snap = await GetFirestore()
                .CollectionGroup(DatabaseTopology.Subcollections.Items)
                .WhereEqualTo(DatabaseTopology.ItemCoreFields.DataId, id)
                .Limit(2)
                .GetSnapshotAsync();

            DocumentSnapshot? match = null;
            int count = 0;
            foreach (DocumentSnapshot doc in snap.Documents)
            {
                if (!doc.Exists)
                    continue;

                count++;
                match = doc;
            }

            if (count == 0 || match is null)
                return ItemDetailResult.Fail("Item was not found.");

            if (count > 1)
                return ItemDetailResult.Fail("More than one item uses this id.");

            Dictionary<string, object> data = match.ToDictionary();
            string? openUrl = await ResolveOpenUrlAsync(data);
            ItemDetail? detail = InventoryDocumentPipeline.ParseItemDetail(match.Id, data, openUrl);
            if (detail is null)
                return ItemDetailResult.Fail("Item was not found.");

            return ItemDetailResult.Success(detail.Value);
        }
        catch (Exception ex)
        {
            BackendLog.Error($"GetByDataIdAsync failed for {dataId}: {ex.Message}");
            return ItemDetailResult.Fail("Could not load this item.");
        }
    }

    public async Task<InventoryItemsResult> ListActiveInventoryItemsAsync(
        string collectionPath,
        string activeFieldPath = DatabaseTopology.ItemFields.Active,
        bool activeValue = true)
    {
        if (string.IsNullOrWhiteSpace(collectionPath))
            return InventoryItemsResult.Fail("Collection path is required.");

        try
        {
            CollectionReference collection = DatabaseTopology.ResolveInventoryCollection(
                GetFirestore(),
                collectionPath.Trim());

            QuerySnapshot snap = await collection
                .WhereEqualTo(activeFieldPath, activeValue)
                .GetSnapshotAsync();

            var items = new List<InventoryItemDocument>(snap.Count);
            foreach (DocumentSnapshot doc in snap.Documents)
            {
                if (!doc.Exists)
                    continue;

                items.Add(new InventoryItemDocument(doc.Id, doc.ToDictionary()));
            }

            return InventoryItemsResult.Success(items);
        }
        catch (Exception ex)
        {
            BackendLog.Error($"ListActiveInventoryItemsAsync failed for {collectionPath}: {ex.Message}");
            return InventoryItemsResult.Fail("Could not load inventory items.");
        }
    }

    public async Task<ItemDetailResult> GetAsync(string dataId, string partitionId)
    {
        if (string.IsNullOrWhiteSpace(dataId))
            return ItemDetailResult.Fail("Item id is required.");

        if (string.IsNullOrWhiteSpace(partitionId))
            return ItemDetailResult.Fail("Collection id is required.");

        string id = dataId.Trim();
        string partition = partitionId.Trim();

        try
        {
            DocumentSnapshot itemSnap = await Item(partition, id).GetSnapshotAsync();
            if (!itemSnap.Exists)
                return ItemDetailResult.Fail("Item was not found.");

            Dictionary<string, object> data = itemSnap.ToDictionary();
            string? openUrl = await ResolveOpenUrlAsync(data);
            ItemDetail? detail = InventoryDocumentPipeline.ParseItemDetail(itemSnap.Id, data, openUrl);

            if (detail is null)
                return ItemDetailResult.Fail("Item was not found.");

            return ItemDetailResult.Success(detail.Value);
        }
        catch (Exception ex)
        {
            BackendLog.Error($"Item read failed for {dataId}: {ex.Message}");
            return ItemDetailResult.Fail("Could not load this item.");
        }
    }

    public async Task<ItemProfileResult> GetItemProfileAsync(string dataId, string partitionId)
    {
        if (string.IsNullOrWhiteSpace(dataId))
            return ItemProfileResult.Fail("Item id is required.");

        if (string.IsNullOrWhiteSpace(partitionId))
            return ItemProfileResult.Fail("Collection id is required.");

        string id = dataId.Trim();
        string partition = partitionId.Trim();

        try
        {
            DocumentSnapshot itemSnap = await Item(partition, id).GetSnapshotAsync();
            if (!itemSnap.Exists)
                return ItemProfileResult.Fail("Item was not found.");

            Dictionary<string, object> data = itemSnap.ToDictionary();
            string? displayName = InventoryFieldReaders.GetString(data, DatabaseTopology.ItemCoreFields.DisplayName);
            if (string.IsNullOrWhiteSpace(displayName))
                return ItemProfileResult.Fail("Item was not found.");

            Dictionary<string, object> profileFields = InventoryDocumentPipeline.ExtractProfileFields(data);
            return ItemProfileResult.Success(new ItemProfileSnapshot(id, displayName.Trim(), profileFields));
        }
        catch (Exception ex)
        {
            BackendLog.Error($"GetItemProfileAsync failed for {dataId}: {ex.Message}");
            return ItemProfileResult.Fail("Could not load this item.");
        }
    }

    public async Task<ItemDetailListResult> ListActiveAsync(string partitionId)
    {
        if (string.IsNullOrWhiteSpace(partitionId))
            return ItemDetailListResult.Fail("Collection id is required.");

        string partition = partitionId.Trim();

        try
        {
            QuerySnapshot itemSnap = await Items(partition)
                .WhereEqualTo(DatabaseTopology.ItemCoreFields.Active, true)
                .GetSnapshotAsync();

            if (itemSnap.Count == 0)
            {
                return ItemDetailListResult.Success(
                    Array.Empty<ItemDetail>(),
                    new Dictionary<string, IReadOnlyDictionary<string, object>>());
            }

            var details = new List<ItemDetail>(itemSnap.Count);
            var bags = new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.Ordinal);
            foreach (DocumentSnapshot item in itemSnap.Documents)
            {
                if (!item.Exists)
                    continue;

                Dictionary<string, object> data = item.ToDictionary();
                ItemDetail? detail = InventoryDocumentPipeline.ParseItemDetail(item.Id, data);
                if (detail is null)
                    continue;

                details.Add(detail.Value);
                bags[detail.Value.DataId] = InventoryDocumentPipeline.ExtractProfileFields(data);
            }

            details.Sort((a, b) => string.Compare(
                a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            return ItemDetailListResult.Success(details, bags);
        }
        catch (Exception ex)
        {
            BackendLog.Error($"Item list failed: {ex.Message}");
            return ItemDetailListResult.Fail("Could not load this collection.");
        }
    }

    /// <inheritdoc cref="DataTools.ListInventoryCollectionsAsync" />
    public async Task<InventoryCollectionListResult> ListInventoryCollectionsAsync()
    {
        try
        {
            QuerySnapshot snap = await GetFirestore()
                .Collection(DatabaseTopology.Collections.DataRoot)
                .GetSnapshotAsync();

            var collections = new List<InventoryCollectionSummary>(snap.Count);
            foreach (DocumentSnapshot doc in snap.Documents)
            {
                if (!doc.Exists)
                    continue;

                InventoryCollectionSummary? parsed = InventoryDocumentPipeline.ParsePartitionSummary(
                    doc.Id,
                    doc.ToDictionary());
                if (parsed is not null)
                    collections.Add(parsed);
            }

            collections.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            return InventoryCollectionListResult.Success(collections);
        }
        catch (Exception ex)
        {
            BackendLog.Error($"ListInventoryCollectionsAsync failed: {ex.Message}");
            return InventoryCollectionListResult.Fail("Could not load inventory collections.");
        }
    }

    /// <inheritdoc cref="DataTools.ListActiveMobileAsync" />
    public async Task<ItemDetailListResult> ListActiveMobileAsync()
    {
        try
        {
            QuerySnapshot partitions = await GetFirestore()
                .Collection(DatabaseTopology.Collections.DataRoot)
                .WhereEqualTo(DatabaseTopology.PartitionFields.MobileAllowed, true)
                .GetSnapshotAsync();

            var details = new List<ItemDetail>();
            var bags = new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.Ordinal);

            foreach (DocumentSnapshot partition in partitions.Documents)
            {
                if (!partition.Exists)
                    continue;

                Dictionary<string, object> data = partition.ToDictionary();
                string partitionId = InventoryFieldReaders.GetString(
                    data,
                    DatabaseTopology.PartitionFields.PartitionId) ?? partition.Id;

                ItemDetailListResult part = await ListActiveAsync(partitionId);
                if (!part.Ok || part.Details is null)
                    return ItemDetailListResult.Fail(part.ErrorMessage ?? "Could not load this collection.");

                details.AddRange(part.Details);
                if (part.ProfileFields is null)
                    continue;

                foreach (KeyValuePair<string, IReadOnlyDictionary<string, object>> pair in part.ProfileFields)
                    bags[pair.Key] = pair.Value;
            }

            details.Sort((a, b) => string.Compare(
                a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return ItemDetailListResult.Success(details, bags);
        }
        catch (Exception ex)
        {
            BackendLog.Error($"ListActiveMobileAsync failed: {ex.Message}");
            return ItemDetailListResult.Fail("Could not load this collection.");
        }
    }

    internal FirestoreDb Database => GetFirestore();

    private FirestoreDb GetFirestore()
    {
        InitializeDatabase();
        return _db!;
    }

    private async Task<string?> ResolveOpenUrlAsync(IReadOnlyDictionary<string, object> itemData)
    {
        if (_storage is null)
            return null;

        string pdfPath = InventoryFieldReaders.GetString(itemData, DatabaseTopology.ItemCoreFields.PdfPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pdfPath))
            return null;

        return await _storage.ResolveOpenUrlAsync(pdfPath);
    }

    private CollectionReference Items(string partitionId)
        => InventoryDocumentPipeline.Items(Database, partitionId);

    private DocumentReference Item(string partitionId, string dataId)
        => InventoryDocumentPipeline.Item(Database, partitionId, dataId);
}
