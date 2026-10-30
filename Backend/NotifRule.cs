#nullable enable
using System;
using System.Collections.Generic;

namespace Backend;

/// <summary>Discriminator for <see cref="NotifRule"/>. Wire names are <see cref="DatabaseTopology.NotifRuleTypes"/>.</summary>
public enum NotifRuleKind
{
    DateWarnBefore,
    BoolEquals,
    NumberOutOfRange
}

/// <summary>
/// One alert rule stored on <c>--SYSTEM--/alert</c>. Desktop builds these; the notif-server parses and evaluates them.
/// </summary>
public readonly struct NotifRule
{
    public NotifRuleKind Kind { get; init; }
    public string Id { get; init; }
    public string FieldPath { get; init; }
    public string? PartitionId { get; init; }
    public string? Section { get; init; }
    public bool IncludeOverdueRelativeToToday { get; init; }
    public int LeadDays { get; init; }
    public bool BoolTarget { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }

    public static NotifRuleKind? KindForTypeName(string? type)
    {
        if (string.Equals(type, DatabaseTopology.NotifRuleTypes.DateWarnBefore, StringComparison.Ordinal))
            return NotifRuleKind.DateWarnBefore;
        if (string.Equals(type, DatabaseTopology.NotifRuleTypes.BoolEquals, StringComparison.Ordinal))
            return NotifRuleKind.BoolEquals;
        if (string.Equals(type, DatabaseTopology.NotifRuleTypes.NumberOutOfRange, StringComparison.Ordinal))
            return NotifRuleKind.NumberOutOfRange;
        return null;
    }

    public static string TypeNameFor(NotifRuleKind kind) => kind switch
    {
        NotifRuleKind.DateWarnBefore => DatabaseTopology.NotifRuleTypes.DateWarnBefore,
        NotifRuleKind.BoolEquals => DatabaseTopology.NotifRuleTypes.BoolEquals,
        NotifRuleKind.NumberOutOfRange => DatabaseTopology.NotifRuleTypes.NumberOutOfRange,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public string TypeName => TypeNameFor(Kind);

    public static NotifRule DateWarnBefore(
        string id,
        string fieldPath,
        int leadDays,
        bool includeOverdueRelativeToToday,
        string? section = null,
        string? partitionId = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Rule id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(fieldPath))
            throw new ArgumentException("Field path is required.", nameof(fieldPath));
        if (leadDays < 1)
            throw new ArgumentOutOfRangeException(nameof(leadDays), "Lead days must be at least 1.");

        return new NotifRule
        {
            Kind = NotifRuleKind.DateWarnBefore,
            Id = id.Trim(),
            FieldPath = fieldPath.Trim(),
            PartitionId = Normalize(partitionId),
            Section = Normalize(section),
            LeadDays = leadDays,
            IncludeOverdueRelativeToToday = includeOverdueRelativeToToday
        };
    }

    public static NotifRule BoolEquals(
        string id,
        string fieldPath,
        bool target,
        string? section = null,
        string? partitionId = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Rule id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(fieldPath))
            throw new ArgumentException("Field path is required.", nameof(fieldPath));

        return new NotifRule
        {
            Kind = NotifRuleKind.BoolEquals,
            Id = id.Trim(),
            FieldPath = fieldPath.Trim(),
            PartitionId = Normalize(partitionId),
            Section = Normalize(section),
            BoolTarget = target
        };
    }

    public static NotifRule NumberOutOfRange(
        string id,
        string fieldPath,
        double? min,
        double? max,
        string? section = null,
        string? partitionId = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Rule id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(fieldPath))
            throw new ArgumentException("Field path is required.", nameof(fieldPath));
        if (min is null && max is null)
            throw new ArgumentException("At least one of min or max must be set.", nameof(min));
        if (min is not null && max is not null && min.Value > max.Value)
            throw new ArgumentException("Min cannot be greater than max.");

        return new NotifRule
        {
            Kind = NotifRuleKind.NumberOutOfRange,
            Id = id.Trim(),
            FieldPath = fieldPath.Trim(),
            PartitionId = Normalize(partitionId),
            Section = Normalize(section),
            Min = min,
            Max = max
        };
    }

    public Dictionary<string, object> ToFirestoreMap()
    {
        var map = new Dictionary<string, object>
        {
            [DatabaseTopology.NotifRuleFields.Id] = Id,
            [DatabaseTopology.NotifRuleFields.Type] = TypeName,
            [DatabaseTopology.NotifRuleFields.FieldPath] = FieldPath
        };

        if (!string.IsNullOrWhiteSpace(PartitionId))
            map[DatabaseTopology.NotifRuleFields.PartitionId] = PartitionId!;
        if (!string.IsNullOrWhiteSpace(Section))
            map[DatabaseTopology.NotifRuleFields.Section] = Section!;

        switch (Kind)
        {
            case NotifRuleKind.DateWarnBefore:
                map[DatabaseTopology.NotifRuleFields.LeadDays] = LeadDays;
                map[DatabaseTopology.NotifRuleFields.IncludeOverdueRelativeToToday] = IncludeOverdueRelativeToToday;
                break;
            case NotifRuleKind.BoolEquals:
                map[DatabaseTopology.NotifRuleFields.Value] = BoolTarget;
                break;
            case NotifRuleKind.NumberOutOfRange:
                if (Min is not null)
                    map[DatabaseTopology.NotifRuleFields.Min] = Min.Value;
                if (Max is not null)
                    map[DatabaseTopology.NotifRuleFields.Max] = Max.Value;
                break;
            default:
                throw new InvalidOperationException($"Unknown rule kind: {Kind}");
        }

        return map;
    }

    public bool AppliesToSource(NotifAlertSource source)
    {
        if (string.IsNullOrWhiteSpace(PartitionId))
            return true;

        return string.Equals(PartitionId, source.PartitionId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public readonly record struct NotifRuleMatchResult(bool Matched, string? Detail)
{
    public static NotifRuleMatchResult NoMatch(string? detail = null) => new(false, detail);
    public static NotifRuleMatchResult Match(string? detail = null) => new(true, detail);
}

/// <summary>Parse and evaluate <see cref="NotifRule"/> documents.</summary>
public static class NotifRuleSupport
{
    public static bool TryParseRule(IReadOnlyDictionary<string, object> data, out NotifRule rule, out string? error)
    {
        rule = default;
        error = null;

        string? id = InventoryFieldReaders.GetString(data, DatabaseTopology.NotifRuleFields.Id);
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "Rule id is required.";
            return false;
        }

        string? type = InventoryFieldReaders.GetString(data, DatabaseTopology.NotifRuleFields.Type);
        string? fieldPath = InventoryFieldReaders.GetString(data, DatabaseTopology.NotifRuleFields.FieldPath);
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            error = "Rule fieldPath is required.";
            return false;
        }

        string? section = InventoryFieldReaders.GetString(data, DatabaseTopology.NotifRuleFields.Section);
        string? partitionId = InventoryFieldReaders.GetString(data, DatabaseTopology.NotifRuleFields.PartitionId);
        NotifRuleKind? kind = NotifRule.KindForTypeName(type);
        if (kind is null)
        {
            error = $"Unknown rule type '{type}'.";
            return false;
        }

        try
        {
            rule = kind.Value switch
            {
                NotifRuleKind.DateWarnBefore => ParseDate(data, id, fieldPath, section, partitionId),
                NotifRuleKind.BoolEquals => ParseBool(data, id, fieldPath, section, partitionId),
                NotifRuleKind.NumberOutOfRange => ParseNumber(data, id, fieldPath, section, partitionId),
                _ => throw new InvalidOperationException($"Unknown rule kind: {kind}")
            };
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static NotifRuleMatchResult Evaluate(
        NotifRule rule,
        IReadOnlyDictionary<string, object> fields,
        DateTime referenceUtc)
    {
        return rule.Kind switch
        {
            NotifRuleKind.DateWarnBefore => EvaluateDateWarnBefore(rule, fields, referenceUtc),
            NotifRuleKind.BoolEquals => EvaluateBoolEquals(rule, fields),
            NotifRuleKind.NumberOutOfRange => EvaluateNumberOutOfRange(rule, fields),
            _ => NotifRuleMatchResult.NoMatch($"Unknown rule kind: {rule.Kind}")
        };
    }

    private static NotifRule ParseDate(
        IReadOnlyDictionary<string, object> data,
        string id,
        string fieldPath,
        string? section,
        string? partitionId)
    {
        int leadDays = TryGetInt(data, DatabaseTopology.NotifRuleFields.LeadDays, out int parsedLead)
            ? parsedLead
            : 30;
        if (leadDays < 1)
            leadDays = 30;

        if (!InventoryFieldReaders.TryGetBool(data, DatabaseTopology.NotifRuleFields.IncludeOverdueRelativeToToday, out bool includeOverdue))
            throw new ArgumentException("dateWarnBefore requires includeOverdueRelativeToToday.");

        return NotifRule.DateWarnBefore(id, fieldPath, leadDays, includeOverdue, section, partitionId);
    }

    private static NotifRule ParseBool(
        IReadOnlyDictionary<string, object> data,
        string id,
        string fieldPath,
        string? section,
        string? partitionId)
    {
        if (!InventoryFieldReaders.TryGetBool(data, DatabaseTopology.NotifRuleFields.Value, out bool target))
            throw new ArgumentException("boolEquals requires value.");

        return NotifRule.BoolEquals(id, fieldPath, target, section, partitionId);
    }

    private static NotifRule ParseNumber(
        IReadOnlyDictionary<string, object> data,
        string id,
        string fieldPath,
        string? section,
        string? partitionId)
    {
        bool hasMin = InventoryFieldReaders.TryGetNumber(data, DatabaseTopology.NotifRuleFields.Min, out double min);
        bool hasMax = InventoryFieldReaders.TryGetNumber(data, DatabaseTopology.NotifRuleFields.Max, out double max);
        if (!hasMin && !hasMax)
            throw new ArgumentException("numberOutOfRange requires min and/or max.");

        return NotifRule.NumberOutOfRange(
            id,
            fieldPath,
            hasMin ? min : null,
            hasMax ? max : null,
            section,
            partitionId);
    }

    public static NotifRuleMatchResult EvaluateDateWarnBefore(
        NotifRule rule,
        IReadOnlyDictionary<string, object> fields,
        DateTime referenceUtc)
    {
        if (rule.Kind != NotifRuleKind.DateWarnBefore)
            throw new ArgumentException("Rule kind must be DateWarnBefore.", nameof(rule));

        DateTime? fieldDate = InventoryFieldReaders.GetDateUtc(fields, rule.FieldPath);
        if (fieldDate is null)
            return NotifRuleMatchResult.NoMatch("Date field missing or invalid.");

        DateTime today = DateTime.SpecifyKind(referenceUtc.Date, DateTimeKind.Utc);
        DateTime lastDayInWindow = today.AddDays(rule.LeadDays);
        DateTime valueDate = fieldDate.Value.Date;

        bool overdue = valueDate < today;
        if (overdue && !rule.IncludeOverdueRelativeToToday)
            return NotifRuleMatchResult.NoMatch("Date is overdue and overdue alerts are disabled.");

        if (valueDate > lastDayInWindow)
            return NotifRuleMatchResult.NoMatch("Date is after the warn-before window.");

        string detail = overdue
            ? $"Overdue ({valueDate:yyyy-MM-dd})"
            : valueDate == today
                ? "Due today"
                : $"Within {rule.LeadDays} day(s) ({valueDate:yyyy-MM-dd})";

        return NotifRuleMatchResult.Match(detail);
    }

    public static NotifRuleMatchResult EvaluateBoolEquals(NotifRule rule, IReadOnlyDictionary<string, object> fields)
    {
        if (rule.Kind != NotifRuleKind.BoolEquals)
            throw new ArgumentException("Rule kind must be BoolEquals.", nameof(rule));

        if (!InventoryFieldReaders.TryGetBool(fields, rule.FieldPath, out bool value))
            return NotifRuleMatchResult.NoMatch("Bool field missing or invalid.");

        if (value != rule.BoolTarget)
            return NotifRuleMatchResult.NoMatch($"Value is {value}, expected {rule.BoolTarget}.");

        return NotifRuleMatchResult.Match($"Value is {rule.BoolTarget}.");
    }

    public static NotifRuleMatchResult EvaluateNumberOutOfRange(NotifRule rule, IReadOnlyDictionary<string, object> fields)
    {
        if (rule.Kind != NotifRuleKind.NumberOutOfRange)
            throw new ArgumentException("Rule kind must be NumberOutOfRange.", nameof(rule));

        if (!InventoryFieldReaders.TryGetNumber(fields, rule.FieldPath, out double value))
            return NotifRuleMatchResult.NoMatch("Number field missing or invalid.");

        if (rule.Min is not null && value < rule.Min.Value)
            return NotifRuleMatchResult.Match($"Value {value} is below min {rule.Min.Value}.");

        if (rule.Max is not null && value > rule.Max.Value)
            return NotifRuleMatchResult.Match($"Value {value} is above max {rule.Max.Value}.");

        return NotifRuleMatchResult.NoMatch("Value is within range.");
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, object> data, string key, out int result)
    {
        result = default;
        if (!data.TryGetValue(key, out object? raw))
            return false;

        int? coerced = InventoryFieldReaders.CoerceInt(raw);
        if (coerced is null)
            return false;

        result = coerced.Value;
        return true;
    }
}
