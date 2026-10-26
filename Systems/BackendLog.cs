#nullable enable
using System;

/// <summary>Diagnostics for Godot-free backend code (Firebase, OneDrive, validation).</summary>
public static class BackendLog
{
    public static void Error(string message) => Console.Error.WriteLine(message);
}
