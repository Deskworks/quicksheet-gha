using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// QuickSheet GitHub Actions Extension — live workflow run statuses on your desktop.
/// Prefix: "gha". Usage: "gha: owner/repo" or "gha: owner/repo, N" (show N runs, default 10).
/// Optionally set GITHUB_TOKEN env var for private repos and higher rate limits.
/// Uses GitHub REST API v3 — free, no signup required for public repos.
/// </summary>
class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly HttpClient Http;

    // Cache: repo -> (runs, fetchedAt)
    private static readonly Dictionary<string, (List<WorkflowRun> runs, DateTime fetchedAt)> Cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    static Program()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("quicksheet-gha/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        Http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                string? type = doc.RootElement.TryGetProperty("type", out var tp) ? tp.GetString() : null;

                switch (type)
                {
                    case "init":
                        HandleInit();
                        break;
                    case "activate":
                        HandleActivate(doc.RootElement);
                        break;
                    case "deactivate":
                        break;
                }
            }
            catch (Exception ex)
            {
                SendJson(new { type = "error", id = "", message = $"Parse error: {ex.Message}" });
            }
        }
    }

    static void HandleInit()
    {
        SendJson(new
        {
            type = "register",
            prefix = "gha",
            name = "GitHub Actions Status",
            version = "1.0.0"
        });
        SendLog("GitHub Actions Status registered. Set GITHUB_TOKEN for private repos.");
    }

    static void HandleActivate(JsonElement root)
    {
        string id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";

        string[] extParams = [];
        if (root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Array)
        {
            extParams = paramsProp.EnumerateArray()
                .Select(e => e.GetString()?.Trim() ?? "")
                .Where(s => s.Length > 0)
                .ToArray();
        }

        if (extParams.Length == 0)
        {
            WriteResult(id, 0, 0, "gha: owner/repo");
            WriteResult(id, 1, 0, "Set GITHUB_TOKEN for private repos");
            SendWrite(id);
            return;
        }

        string repo = extParams[0];
        int maxRuns = 10;
        if (extParams.Length > 1 && int.TryParse(extParams[1], out int n))
            maxRuns = Math.Clamp(n, 1, 20);

        FetchAndRender(id, repo, maxRuns);
    }

    static void FetchAndRender(string id, string repo, int maxRuns)
    {
        try
        {
            List<WorkflowRun> runs = GetRuns(repo, maxRuns);

            int row = 0;
            string hasToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") != null ? "" : " (public)";
            WriteResult(id, row++, 0, $"⚡ {repo}{hasToken}");
            WriteResult(id, row++, 0, $"{"Status",-4} {"Workflow",-22} {"Branch",-18} {"Age",8}");
            WriteResult(id, row++, 0, new string('─', 56));

            if (runs.Count == 0)
            {
                WriteResult(id, row++, 0, "No workflow runs found.");
            }
            else
            {
                foreach (var run in runs.Take(maxRuns))
                {
                    string icon = GetIcon(run.Status, run.Conclusion);
                    string wfName = Truncate(run.Name ?? run.WorkflowId.ToString(), 22);
                    string branch = Truncate(run.HeadBranch ?? "?", 18);
                    string age = FormatAge(run.UpdatedAt);
                    WriteResult(id, row++, 0, $"{icon,-4} {wfName,-22} {branch,-18} {age,8}");
                }
            }

            SendWrite(id);
        }
        catch (Exception ex)
        {
            WriteResult(id, 0, 0, $"❌ Error: {ex.Message}");
            SendWrite(id);
        }
    }

    static List<WorkflowRun> GetRuns(string repo, int maxRuns)
    {
        if (Cache.TryGetValue(repo, out var cached) && DateTime.UtcNow - cached.fetchedAt < CacheTtl)
            return cached.runs;

        int perPage = Math.Min(maxRuns, 20);
        string url = $"https://api.github.com/repos/{repo}/actions/runs?per_page={perPage}";
        string json = Http.GetStringAsync(url).GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(json);
        var runs = new List<WorkflowRun>();

        if (doc.RootElement.TryGetProperty("workflow_runs", out var arr))
        {
            foreach (var elem in arr.EnumerateArray())
            {
                runs.Add(new WorkflowRun
                {
                    Id = elem.TryGetProperty("id", out var idP) ? idP.GetInt64() : 0,
                    Name = elem.TryGetProperty("name", out var nameP) ? nameP.GetString() : null,
                    WorkflowId = elem.TryGetProperty("workflow_id", out var wfP) ? wfP.GetInt64() : 0,
                    Status = elem.TryGetProperty("status", out var stP) ? stP.GetString() ?? "" : "",
                    Conclusion = elem.TryGetProperty("conclusion", out var conP) ? conP.GetString() : null,
                    HeadBranch = elem.TryGetProperty("head_branch", out var brP) ? brP.GetString() : null,
                    UpdatedAt = elem.TryGetProperty("updated_at", out var updP) && updP.TryGetDateTimeOffset(out var dt)
                        ? dt.UtcDateTime : DateTime.UtcNow,
                    RunNumber = elem.TryGetProperty("run_number", out var rnP) ? rnP.GetInt32() : 0
                });
            }
        }

        Cache[repo] = (runs, DateTime.UtcNow);
        return runs;
    }

    static string GetIcon(string status, string? conclusion) => (status, conclusion) switch
    {
        ("completed", "success") => "✅",
        ("completed", "failure") => "❌",
        ("completed", "cancelled") => "🚫",
        ("completed", "timed_out") => "⏱️",
        ("completed", "skipped") => "⏭️",
        ("completed", _) => "⚠️",
        ("in_progress", _) => "🔄",
        ("queued", _) => "⏳",
        ("waiting", _) => "⏸️",
        _ => "❓"
    };

    static string FormatAge(DateTime updatedAt)
    {
        var elapsed = DateTime.UtcNow - updatedAt;
        if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds}s ago";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }

    static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    static void WriteResult(string id, int r, int c, string v) =>
        SendJson(new { type = "write", id, r, c, v });

    static void SendWrite(string id) =>
        SendJson(new { type = "write", id });

    static void SendLog(string msg) =>
        SendJson(new { type = "log", message = msg });

    static void SendJson(object obj)
    {
        Console.WriteLine(JsonSerializer.Serialize(obj, JsonOpts));
        Console.Out.Flush();
    }
}

record WorkflowRun
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public long WorkflowId { get; init; }
    public string Status { get; init; } = "";
    public string? Conclusion { get; init; }
    public string? HeadBranch { get; init; }
    public DateTime UpdatedAt { get; init; }
    public int RunNumber { get; init; }
}
