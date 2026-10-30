#nullable enable
using Backend.Firebase;

namespace Backend;

/// <summary>Notif-server database reads. Same <see cref="DataTools"/> provider as desktop, without storage.</summary>
public static class BackendServices
{
    public static DataTools Database { get; } = new FirebaseDataTools();

    public static void EnsureDatabaseReady() => Database.InitializeDatabase();
}
