#nullable enable
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;

namespace Backend.Firebase;

/// <summary>Process-wide Firebase Admin SDK initialization.</summary>
public static class FirebaseConnect
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void InitializeFirebase()
    {
        if (_initialized)
            return;

        lock (Gate)
        {
            if (_initialized)
                return;

            if (FirebaseApp.DefaultInstance == null)
            {
                FirebaseApp.Create(new AppOptions
                {
                    Credential = GoogleCredential.GetApplicationDefault()
                });
            }

            _initialized = true;
        }
    }
}
