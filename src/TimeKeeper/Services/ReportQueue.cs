using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TimeKeeper.Services;

/// <summary>One time report, ready to be written to new_hours.</summary>
public sealed record HoursReport(
    Guid LocalId,
    TimerTarget Target,
    string Description,
    int Minutes,
    DateOnly Date,
    DateTimeOffset CreatedAt)
{
    public string? LastError { get; init; }
}

/// <summary>
/// Reports that couldn't be sent yet (offline, signed out, server down).
/// Stored encrypted with DPAPI for the current Windows user, since they
/// include free-text descriptions.
/// </summary>
public sealed class ReportQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly List<HoursReport> _items = new();

    public IReadOnlyList<HoursReport> Items => _items;

    public int Count => _items.Count;

    public event EventHandler? Changed;

    private static string FilePath => Path.Combine(AppPaths.DataFolder, "queue.dat");

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            _items.AddRange(JsonSerializer.Deserialize<List<HoursReport>>(plain, JsonOptions) ?? new());
            Log.Info($"Loaded {_items.Count} pending report(s).");
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the pending queue; keeping the file aside.", ex);
            try
            {
                File.Move(FilePath, FilePath + $".bad-{DateTime.Now:yyyyMMdd-HHmmss}");
            }
            catch
            {
                // Leave it; next save overwrites.
            }
        }
    }

    public void Enqueue(HoursReport report)
    {
        _items.RemoveAll(r => r.LocalId == report.LocalId);
        _items.Add(report);
        Save();
    }

    public void Remove(Guid localId)
    {
        if (_items.RemoveAll(r => r.LocalId == localId) > 0) Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            if (_items.Count == 0)
            {
                File.Delete(FilePath);
            }
            else
            {
                var plain = JsonSerializer.SerializeToUtf8Bytes(_items, JsonOptions);
                var tmp = FilePath + ".tmp";
                File.WriteAllBytes(tmp, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the pending queue.", ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
