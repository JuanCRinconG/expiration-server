#nullable enable
using System.Text;
using Backend;

/// <summary>Rule-driven alert collection and sectioned mail (uses <see cref="BackendServices.Database"/> only).</summary>
public static partial class NotifServer
{
    private const string DefaultSectionLabel = "alerts";

    private static async Task<List<NotifAlertHit>> CollectAlertHitsAsync()
    {
        var hits = new List<NotifAlertHit>();
        DateTime referenceUtc = DateTime.UtcNow;

        NotifAlertConfigResult configResult = await BackendServices.Database.GetAlertConfigAsync();
        if (!configResult.Ok)
        {
            LogWarn($"Alert config unavailable: {configResult.ErrorMessage}");
            return hits;
        }

        NotifAlertConfig config = configResult.Config!;
        LogInfo($"Alert config v{config.Version} (rules={config.Rules.Count}, sources={config.Sources.Count}).");

        if (config.Sources.Count == 0)
        {
            LogWarn("Alert config has no sources[] — add inventory paths in --SYSTEM--/alert.");
            return hits;
        }

        IReadOnlyList<NotifRule> rules = ParseConfiguredRules(config.Rules);
        if (rules.Count == 0)
        {
            LogWarn("Alert config has no valid rules[] — configure rules via desktop Notification setup.");
            return hits;
        }

        await CollectRuleBasedHitsAsync(hits, config.Sources, rules, referenceUtc);
        return hits;
    }

    private static List<NotifRule> ParseConfiguredRules(IReadOnlyList<Dictionary<string, object>>? ruleMaps)
    {
        var rules = new List<NotifRule>();
        if (ruleMaps is null || ruleMaps.Count == 0)
            return rules;

        foreach (Dictionary<string, object> map in ruleMaps)
        {
            if (!NotifRuleSupport.TryParseRule(map, out NotifRule rule, out string? error))
            {
                LogWarn($"Skipping invalid alert rule: {error}");
                continue;
            }

            rules.Add(rule);
        }

        return rules;
    }

    private static async Task CollectRuleBasedHitsAsync(
        List<NotifAlertHit> hits,
        IReadOnlyList<NotifAlertSource> sources,
        IReadOnlyList<NotifRule> rules,
        DateTime referenceUtc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (NotifAlertSource source in sources)
        {
            InventoryItemsResult itemsResult = await BackendServices.Database.ListActiveInventoryItemsAsync(
                source.CollectionPath,
                source.ActiveFieldPath,
                source.ActiveValue);

            if (!itemsResult.Ok)
            {
                LogWarn($"Source '{source.CollectionPath}' skipped: {itemsResult.ErrorMessage}");
                continue;
            }

            LogInfo($"Evaluating {itemsResult.Items!.Count} item(s) from '{source.CollectionPath}'.");

            foreach (InventoryItemDocument item in itemsResult.Items)
            {
                if (!TryReadItemIdentity(item, out string sdsId, out string displayName))
                    continue;

                foreach (NotifRule rule in rules)
                {
                    if (!rule.AppliesToSource(source))
                        continue;

                    NotifRuleMatchResult match = NotifRuleSupport.Evaluate(rule, item.Fields, referenceUtc);
                    if (!match.Matched)
                        continue;

                    string dedupeKey = $"{source.CollectionPath}|{sdsId}|{rule.Id}";
                    if (!seen.Add(dedupeKey))
                        continue;

                    hits.Add(BuildHit(sdsId, displayName, rule, match.Detail));
                }
            }
        }
    }

    private static bool TryReadItemIdentity(
        InventoryItemDocument item,
        out string sdsId,
        out string displayName)
    {
        sdsId = InventoryFieldReaders.GetString(item.Fields, DatabaseTopology.ItemFields.DataId) ?? item.DocumentId;
        string? name = InventoryFieldReaders.GetString(item.Fields, DatabaseTopology.ItemFields.DisplayName);
        displayName = string.IsNullOrWhiteSpace(name) ? sdsId : name;
        return !string.IsNullOrWhiteSpace(sdsId);
    }

    private static NotifAlertHit BuildHit(
        string sdsId,
        string displayName,
        NotifRule rule,
        string? detail)
    {
        string section = string.IsNullOrWhiteSpace(rule.Section) ? rule.Id : rule.Section!;
        return new NotifAlertHit(
            sdsId,
            displayName,
            rule.Id,
            section,
            detail ?? "Matched");
    }

    private static string BuildSectionedAlertBody(IReadOnlyList<NotifAlertHit> hits)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ChemClassify notification alert");
        sb.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        IEnumerable<IGrouping<string, NotifAlertHit>> sections = hits
            .GroupBy(h => string.IsNullOrWhiteSpace(h.Section) ? DefaultSectionLabel : h.Section)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, NotifAlertHit> section in sections)
        {
            sb.AppendLine($"--- {section.Key} ---");
            foreach (NotifAlertHit hit in section.OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(FormatHitLine(hit));
            }

            sb.AppendLine();
        }

        sb.AppendLine("Open the ChemClassify desktop app for full inventory details.");
        return sb.ToString();
    }

    private static string FormatHitLine(NotifAlertHit hit)
    {
        var parts = new List<string>
        {
            $"- {hit.DisplayName}",
            $"id={hit.DataId}",
            hit.Detail
        };

        return string.Join(" | ", parts);
    }

    private static string BuildAlertSubject(IReadOnlyList<NotifAlertHit> hits)
    {
        int sectionCount = hits.Select(h => h.Section).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return $"[ChemClassify] {hits.Count} alert(s) across {sectionCount} section(s)";
    }
}
