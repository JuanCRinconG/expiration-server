#nullable enable
using System;
using System.Collections.Generic;
using Backend;

/// <summary>
/// V1 notification alert rules — loaded from <c>__SYSTEM__/alert</c> and evaluated by <see cref="NotifServer"/>.
/// </summary>
public static class NotifRuleTypeNames
{
    public const string DateWarnBefore = Backend.DatabaseTopology.NotifRuleTypes.DateWarnBefore;
    public const string BoolEquals = Backend.DatabaseTopology.NotifRuleTypes.BoolEquals;
    public const string NumberOutOfRange = Backend.DatabaseTopology.NotifRuleTypes.NumberOutOfRange;
}

/// <summary>Profile field types that notification rules may reference.</summary>
public enum NotifEntityFieldKind
{
    Date,
    Bool,
    Number
}

/// <summary>Discriminator for <see cref="NotifRule"/>.</summary>
public enum NotifRuleKind
{
    DateWarnBefore,
    BoolEquals,
    NumberOutOfRange
}

/// <summary>
/// One alert rule. Kind-specific properties are set only for the matching <see cref="Kind"/>.
/// </summary>
public readonly struct NotifRule
{
    public NotifRuleKind Kind { get; init; }
    public string Id { get; init; }
    public string FieldPath { get; init; }
    public string? Section { get; init; }

    /// <summary><see cref="NotifRuleKind.DateWarnBefore"/> — include dates before today (overdue).</summary>
    public bool IncludeOverdueRelativeToToday { get; init; }

    /// <summary><see cref="NotifRuleKind.DateWarnBefore"/> — alert when field date is on or before today + lead days.</summary>
    public int LeadDays { get; init; }

    /// <summary><see cref="NotifRuleKind.BoolEquals"/> — alert when the bool field equals this value.</summary>
    public bool BoolTarget { get; init; }

    /// <summary><see cref="NotifRuleKind.NumberOutOfRange"/> — alert when value &lt; min (if set).</summary>
    public double? Min { get; init; }

    /// <summary><see cref="NotifRuleKind.NumberOutOfRange"/> — alert when value &gt; max (if set).</summary>
    public double? Max { get; init; }

    public static NotifEntityFieldKind RequiredFieldKind(NotifRuleKind kind) => kind switch
    {
        NotifRuleKind.DateWarnBefore => NotifEntityFieldKind.Date,
        NotifRuleKind.BoolEquals => NotifEntityFieldKind.Bool,
        NotifRuleKind.NumberOutOfRange => NotifEntityFieldKind.Number,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static NotifRule DateWarnBefore(
        string id,
        string fieldPath,
        int leadDays,
        bool includeOverdueRelativeToToday,
        string? section = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Rule id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(fieldPath))
            throw new ArgumentException("Field path is required.", nameof(fieldPath));
        if (leadDays < 0)
            throw new ArgumentOutOfRangeException(nameof(leadDays), "Lead days must be >= 0.");

        return new NotifRule
        {
            Kind = NotifRuleKind.DateWarnBefore,
            Id = id.Trim(),
            FieldPath = fieldPath.Trim(),
            Section = string.IsNullOrWhiteSpace(section) ? null : section.Trim(),
            LeadDays = leadDays,
            IncludeOverdueRelativeToToday = includeOverdueRelativeToToday
        };
    }

    public static NotifRule BoolEquals(string id, string fieldPath, bool target, string? section = null)
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
            Section = string.IsNullOrWhiteSpace(section) ? null : section.Trim(),
            BoolTarget = target
        };
    }

    public static NotifRule NumberOutOfRange(
        string id,
        string fieldPath,
        double? min,
        double? max,
        string? section = null)
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
            Section = string.IsNullOrWhiteSpace(section) ? null : section.Trim(),
            Min = min,
            Max = max
        };
    }

    public string TypeName => Kind switch
    {
        NotifRuleKind.DateWarnBefore => NotifRuleTypeNames.DateWarnBefore,
        NotifRuleKind.BoolEquals => NotifRuleTypeNames.BoolEquals,
        NotifRuleKind.NumberOutOfRange => NotifRuleTypeNames.NumberOutOfRange,
        _ => throw new InvalidOperationException($"Unknown rule kind: {Kind}")
    };

    public Dictionary<string, object> ToFirestoreMap()
    {
        var map = new Dictionary<string, object>
        {
            ["id"] = Id,
            ["type"] = TypeName,
            ["fieldPath"] = FieldPath
        };

        if (!string.IsNullOrWhiteSpace(Section))
            map["section"] = Section!;

        switch (Kind)
        {
            case NotifRuleKind.DateWarnBefore:
                map["leadDays"] = LeadDays;
                map["includeOverdueRelativeToToday"] = IncludeOverdueRelativeToToday;
                break;
            case NotifRuleKind.BoolEquals:
                map["value"] = BoolTarget;
                break;
            case NotifRuleKind.NumberOutOfRange:
                if (Min is not null)
                    map["min"] = Min.Value;
                if (Max is not null)
                    map["max"] = Max.Value;
                break;
            default:
                throw new InvalidOperationException($"Unknown rule kind: {Kind}");
        }

        return map;
    }

    public NotifRule WithLeadDays(int leadDays)
    {
        if (Kind != NotifRuleKind.DateWarnBefore)
            throw new InvalidOperationException("WithLeadDays applies only to dateWarnBefore rules.");

        return DateWarnBefore(Id, FieldPath, leadDays, IncludeOverdueRelativeToToday, Section);
    }
}

public readonly record struct NotifRuleMatchResult(bool Matched, string? Detail)
{
    public static NotifRuleMatchResult NoMatch(string? detail = null) => new(false, detail);
    public static NotifRuleMatchResult Match(string? detail = null) => new(true, detail);
}

/// <summary>Parse and evaluate V1 rules (notif-server).</summary>
public static class NotifRuleSupport
{
    public static bool TryParseRule(IReadOnlyDictionary<string, object> data, out NotifRule rule, out string? error)
    {
        rule = default;
        error = null;

        string? id = GetString(data, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "Rule id is required.";
            return false;
        }

        string? type = GetString(data, "type");
        string? fieldPath = GetString(data, "fieldPath");
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            error = "Rule fieldPath is required.";
            return false;
        }

        string? section = GetString(data, "section");

        if (string.Equals(type, NotifRuleTypeNames.DateWarnBefore, StringComparison.Ordinal))
        {
            int leadDays = TryGetInt(data, "leadDays", out int parsedLead) ? parsedLead : 0;
            if (leadDays < 0)
            {
                error = "dateWarnBefore leadDays must be >= 0.";
                return false;
            }

            if (!TryGetBool(data, "includeOverdueRelativeToToday", out bool includeOverdue))
            {
                error = "dateWarnBefore requires includeOverdueRelativeToToday.";
                return false;
            }

            rule = NotifRule.DateWarnBefore(id, fieldPath, leadDays, includeOverdue, section);
            return true;
        }

        if (string.Equals(type, NotifRuleTypeNames.BoolEquals, StringComparison.Ordinal))
        {
            if (!TryGetBool(data, "value", out bool target))
            {
                error = "boolEquals requires value.";
                return false;
            }

            rule = NotifRule.BoolEquals(id, fieldPath, target, section);
            return true;
        }

        if (string.Equals(type, NotifRuleTypeNames.NumberOutOfRange, StringComparison.Ordinal))
        {
            bool hasMin = TryGetDouble(data, "min", out double min);
            bool hasMax = TryGetDouble(data, "max", out double max);
            if (!hasMin && !hasMax)
            {
                error = "numberOutOfRange requires min and/or max.";
                return false;
            }

            double? minOpt = hasMin ? min : null;
            double? maxOpt = hasMax ? max : null;
            try
            {
                rule = NotifRule.NumberOutOfRange(id, fieldPath, minOpt, maxOpt, section);
                return true;
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        error = $"Unknown rule type '{type}'.";
        return false;
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

    private static string? GetString(IReadOnlyDictionary<string, object> data, string key)
        => data.TryGetValue(key, out object? value) ? value?.ToString() : null;

    private static bool TryGetInt(IReadOnlyDictionary<string, object> data, string key, out int result)
    {
        result = default;
        if (!data.TryGetValue(key, out object? raw) || raw is null)
            return false;

        switch (raw)
        {
            case int i:
                result = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                result = (int)l;
                return true;
            case double d when d >= int.MinValue && d <= int.MaxValue:
                result = (int)d;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, object> data, string key, out double result)
    {
        result = default;
        if (!data.TryGetValue(key, out object? raw) || raw is null)
            return false;

        switch (raw)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetBool(IReadOnlyDictionary<string, object> data, string key, out bool value)
    {
        value = default;
        if (!data.TryGetValue(key, out object? raw) || raw is null)
            return false;

        if (raw is bool b)
        {
            value = b;
            return true;
        }

        return false;
    }
}
