using System.Text.Json;

namespace TimeKeeper.Services.Dataverse;

public sealed record Client(Guid Id, string Name, DateTime? LastReported)
{
    public override string ToString() => Name;
}

public sealed record Project(Guid Id, string Name, DateTime? LastReported)
{
    public override string ToString() => Name;
}

public sealed record WorkTask(Guid Id, string Title, string? Number, DateTime? LastReported)
{
    public string Display => string.IsNullOrEmpty(Number) ? Title : $"{Title} ({Number})";

    public override string ToString() => Display;
}

/// <summary>
/// Reads the client → project → task hierarchy (see dataverse-schema.md).
/// "Recent" = what the signed-in user reported on in the last 90 days.
/// </summary>
public sealed class DataverseService
{
    private const int RecentDays = 90;
    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";

    private readonly DataverseSession _session;
    private string? _hoursSet;
    private string? _projectSet;
    private Dictionary<string, string>? _hoursNav;

    public DataverseService(DataverseSession session)
    {
        _session = session;
    }

    /// <summary>Clients the user reported on recently, newest first.</summary>
    public async Task<IReadOnlyList<Client>> GetRecentClientsAsync(CancellationToken ct = default)
    {
        var rows = await RecentAsync("new_cust_name", extraCondition: null, ct);
        return rows.Select(r => new Client(r.Id, r.Name, r.Last)).ToList();
    }

    /// <summary>Clients by name, for search (active accounts only).</summary>
    public async Task<IReadOnlyList<Client>> SearchClientsAsync(string text, CancellationToken ct = default)
    {
        var q = Uri.EscapeDataString(text.Replace("'", "''"));
        using var doc = await _session.GetAsync(
            $"accounts?$select=name&$filter=statecode eq 0 and contains(name,'{q}')&$orderby=name&$top=30", ct);
        return Values(doc).Select(v => new Client(v.GetProperty("accountid").GetGuid(), Str(v, "name"), null)).ToList();
    }

    /// <summary>Active projects of a client; recently reported ones first.</summary>
    public async Task<IReadOnlyList<Project>> GetProjectsAsync(Guid clientId, CancellationToken ct = default)
    {
        var set = await ProjectSetAsync(ct);
        var recentTask = RecentAsync("new_project", $"<condition attribute='new_cust_name' operator='eq' value='{clientId}' />", ct);
        using var doc = await _session.GetAsync(
            $"{set}?$select=new_name&$filter=_new_account_value eq {clientId} and statecode eq 0&$orderby=new_name", ct);
        var recent = (await recentTask).ToDictionary(r => r.Id, r => r.Last);

        return Values(doc)
            .Select(v =>
            {
                var id = v.GetProperty("new_projectid").GetGuid();
                return new Project(id, Str(v, "new_name"), recent.GetValueOrDefault(id));
            })
            .OrderByDescending(p => p.LastReported.HasValue)
            .ThenByDescending(p => p.LastReported)
            .ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Open requests (incidents) of a project, even with no hours yet; recently reported first.</summary>
    public async Task<IReadOnlyList<WorkTask>> GetOpenTasksAsync(Guid projectId, CancellationToken ct = default)
    {
        var recentTask = RecentAsync("new_case", $"<condition attribute='new_project' operator='eq' value='{projectId}' />", ct);
        using var doc = await _session.GetAsync(
            $"incidents?$select=title,ticketnumber&$filter=_new_project_value eq {projectId} and statecode eq 0&$orderby=createdon desc&$top=200", ct);
        var recent = (await recentTask).ToDictionary(r => r.Id, r => r.Last);

        return Values(doc)
            .Select(v =>
            {
                var id = v.GetProperty("incidentid").GetGuid();
                return new WorkTask(id, Str(v, "title"), Str(v, "ticketnumber"), recent.GetValueOrDefault(id));
            })
            .OrderByDescending(t => t.LastReported.HasValue)
            .ThenByDescending(t => t.LastReported)
            .ToList();
    }

    /// <summary>Write one report to new_hours. Throws DataverseException on server errors.</summary>
    public async Task CreateHoursAsync(HoursReport report, CancellationToken ct = default)
    {
        var set = await HoursSetAsync(ct);
        var projectSet = await ProjectSetAsync(ct);
        var nav = await HoursNavigationAsync(ct);
        var userId = _session.UserId;
        if (userId == Guid.Empty) throw new InvalidOperationException("No signed-in user.");

        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["new_name"] = report.Description,
            ["new_dur_rep"] = report.Minutes,
            ["new_date"] = report.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            [nav["new_cust_name"] + "@odata.bind"] = $"/accounts({report.Target.ClientId})",
            [nav["new_project"] + "@odata.bind"] = $"/{projectSet}({report.Target.ProjectId})",
            [nav["new_case"] + "@odata.bind"] = $"/incidents({report.Target.TaskId})",
            [nav["new_reportedby"] + "@odata.bind"] = $"/systemusers({userId})",
        };

        var (status, text) = await _session.SendAsync("POST", set, body.ToJsonString(), ct);
        if (status == 401) throw new DataverseException(status, text);
        if (status is < 200 or >= 300) throw new DataverseException(status, text);
        Log.Info($"Saved report {report.LocalId}: {report.Minutes} min on {report.Target.TaskTitle}.");
    }

    /// <summary>
    /// Offline / signed out / server trouble → keep in the queue and retry.
    /// A 4xx validation error won't fix itself, so it goes back to the user.
    /// </summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        DataverseException d => d.Status is 0 or 401 or 408 or 429 or >= 500,
        InvalidOperationException or TimeoutException or TaskCanceledException => true,
        _ => false,
    };

    /// <summary>Lookup attribute → navigation property name, for @odata.bind (case matters).</summary>
    private async Task<Dictionary<string, string>> HoursNavigationAsync(CancellationToken ct)
    {
        if (_hoursNav is not null) return _hoursNav;
        using var doc = await _session.GetAsync(
            "EntityDefinitions(LogicalName='new_hours')/ManyToOneRelationships?$select=ReferencingAttribute,ReferencingEntityNavigationPropertyName,ReferencedEntity", ct);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Values(doc))
        {
            var attr = Str(r, "ReferencingAttribute");
            var navName = Str(r, "ReferencingEntityNavigationPropertyName");
            var target = Str(r, "ReferencedEntity");
            // Customer-type lookups have one relationship per target; we want account.
            if (attr == "new_cust_name" && target != "account") continue;
            map.TryAdd(attr, navName);
        }

        foreach (var needed in new[] { "new_cust_name", "new_project", "new_case", "new_reportedby" })
        {
            if (!map.ContainsKey(needed)) throw new NotSupportedException($"new_hours has no relationship for {needed}.");
        }

        Log.Info("new_hours navigation: " + string.Join(", ", map.Where(kv => kv.Key.StartsWith("new_")).Select(kv => $"{kv.Key}→{kv.Value}")));
        return _hoursNav = map;
    }

    private sealed record RecentRow(Guid Id, string Name, DateTime? Last);

    /// <summary>groupby(lookup) + max(new_date) over my hours in the last 90 days.</summary>
    private async Task<IReadOnlyList<RecentRow>> RecentAsync(string lookup, string? extraCondition, CancellationToken ct)
    {
        var set = await HoursSetAsync(ct);
        var fetch = $"""
            <fetch aggregate='true'>
              <entity name='new_hours'>
                <attribute name='{lookup}' alias='k' groupby='true' />
                <attribute name='new_date' alias='last' aggregate='max' />
                <filter>
                  <condition attribute='new_reportedby' operator='eq-userid' />
                  <condition attribute='new_date' operator='last-x-days' value='{RecentDays}' />
                  <condition attribute='{lookup}' operator='not-null' />
                  {extraCondition}
                </filter>
                <order alias='last' descending='true' />
              </entity>
            </fetch>
            """;
        using var doc = await _session.GetAsync($"{set}?fetchXml={Uri.EscapeDataString(fetch)}", ct);
        return Values(doc)
            .Where(v => v.TryGetProperty("k", out var k) && k.ValueKind == JsonValueKind.String)
            .Select(v => new RecentRow(
                v.GetProperty("k").GetGuid(),
                Str(v, "k" + Formatted),
                v.TryGetProperty("last", out var l) && l.ValueKind == JsonValueKind.String && DateTime.TryParse(l.GetString(), out var d) ? d : null))
            .ToList();
    }

    private async Task<string> HoursSetAsync(CancellationToken ct) =>
        _hoursSet ??= await EntitySetAsync("new_hours", ct);

    private async Task<string> ProjectSetAsync(CancellationToken ct) =>
        _projectSet ??= await EntitySetAsync("new_project", ct);

    /// <summary>Custom tables' plural names aren't guessable ("new_hourses"?), so ask.</summary>
    private async Task<string> EntitySetAsync(string logicalName, CancellationToken ct)
    {
        using var doc = await _session.GetAsync($"EntityDefinitions(LogicalName='{logicalName}')?$select=EntitySetName", ct);
        var name = doc.RootElement.GetProperty("EntitySetName").GetString()!;
        Log.Info($"Entity set for {logicalName}: {name}");
        return name;
    }

    private static IEnumerable<JsonElement> Values(JsonDocument doc) =>
        doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.Clone()).ToList();

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
