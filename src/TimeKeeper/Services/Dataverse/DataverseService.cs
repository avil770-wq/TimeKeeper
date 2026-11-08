using System.Text.Json;

namespace TimeKeeper.Services.Dataverse;

public sealed record Client(Guid Id, string Name, DateTime? LastReported);

public sealed record Project(Guid Id, string Name, DateTime? LastReported);

public sealed record WorkTask(Guid Id, string Title, string? Number, DateTime? LastReported)
{
    public string Display => string.IsNullOrEmpty(Number) ? Title : $"{Title} · {Number}";
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
