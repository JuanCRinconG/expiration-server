#nullable enable
using System.Threading.Tasks;

namespace Backend;

/// <summary>
/// Shared database reads for desktop, mobile, and the notif spine.
/// One deployment implements this once. Desktop writes stay on the admin tools.
/// </summary>
public interface DataTools
{
    /// <summary>Opens the process database client. Idempotent. Call once at startup.</summary>
    void InitializeDatabase();

    Task<NotifAlertConfigResult> GetAlertConfigAsync();

    Task<InventoryItemsResult> ListActiveInventoryItemsAsync(
        string collectionPath,
        string activeFieldPath = DatabaseTopology.ItemFields.Active,
        bool activeValue = true);

    /// <summary>
    /// Partition-free read for a QR payload. The payload is the raw item id.
    /// Matches <c>items</c> documents whose <c>dataId</c> field equals that id.
    /// </summary>
    Task<ItemDetailResult> GetByDataIdAsync(string dataId);

    Task<ItemDetailResult> GetAsync(string dataId, string partitionId);

    /// <summary>Display name plus profile field values for one item. Core item keys are omitted from the bag.</summary>
    Task<ItemProfileResult> GetItemProfileAsync(string dataId, string partitionId);

    /// <summary>
    /// Active items in one partition. <see cref="ItemDetail.ResolvedOpenUrl"/> stays empty.
    /// <see cref="ItemDetailListResult.ProfileFields"/> is the profile bag for each <c>dataId</c>.
    /// </summary>
    Task<ItemDetailListResult> ListActiveAsync(string partitionId);

    /// <summary>
    /// Active items in partitions whose <c>mobileAllowed</c> field is true.
    /// A missing flag stays hidden. <see cref="ItemDetail.ResolvedOpenUrl"/> stays empty.
    /// </summary>
    Task<ItemDetailListResult> ListActiveMobileAsync();

    /// <summary>
    /// Partitions under <c>--DATA--</c>. Each row includes <see cref="InventoryCollectionSummary.MobileAllowed"/>.
    /// Callers decide which rows to show.
    /// </summary>
    Task<InventoryCollectionListResult> ListInventoryCollectionsAsync();
}
