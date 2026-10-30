#nullable enable

namespace Backend;

/// <summary>V1 data profile field types (stored under <c>--SYSTEM--/dataProfiles</c> when persisted).</summary>
public enum DataProfileFieldKind
{
    String,
    Number,
    Boolean,
    DateTime
}

public readonly record struct DataProfileFieldSpec(
    string Key,
    string Label,
    DataProfileFieldKind Kind,
    bool Required,
    bool ShowOnCard);
