using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// QuickSheet GitHub Actions Extension - live workflow run statuses on your desktop.
/// Prefix: "gha". Usage: "gha: owner/repo" or "gha: owner/repo, N" (show N runs, default 10).
/// Optionally set GITHUB_TOKEN env var for private repos and higher rate limits.
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
            var cells = new object[]
            {
                new { r = 0, c = 0, v = "gha: owner/repo" },
                new { r = 1, c = 0, v = "Set GITHUB_TOKEN for private repos" }
            };
            SendJson(new { type = "write", id, cells });
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

            var cells = new List<object>();
            int row = 0;
            string hasToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") != null ? "" : " (public)";
            cells.Add(new { r = row++, c = 0, v = $"\u26a1 {repo}{hasToken}" });
            cells.Add(new { r = row++, c = 0, v = $"{"Status",-4} {"Workflow",-22} {"Branch",-18} {"Age",8}" });
            cells.Add(new { r = row++, c = 0, v = new string('\u2500', 56) });

            if (runs.Count == 0)
            {
                cells.Add(new { r = row++, c = 0, v = "No workflow runs found." });
            }
            else
            {
                foreach (var run in runs.Take(maxRuns))
                {
                    string icon = GetIcon(run.Status, run.Conclusion);
                    string wfName = Truncate(run.Name ?? run.WorkflowId.ToString(), 22);
                    string branch = Truncate(run.HeadBranch ?? "?", 18);
                    string age = FormatAge(run.UpdatedAt);
                    cells.Add(new { r = row++, c = 0, v = $"{icon,-4} {wfName,-22} {branch,-18} {age,8}" });
                }
            }

            SendJson(new { type = "write", id, cells });
        }
        catch (Exception ex)
        {
            var cells = new object[] { new { r = 0, c = 0, v = $"\u274c Error: {ex.Message}" } };
            SendJson(new { type = "write", id, cells });
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
        ("completed", "success") => "\u2705",
        ("completed", "failure") => "\u274c",
        ("completed", "cancelled") => "\ud83d\udeab",
        ("completed", "timed_out") => "\u23f1\ufe0f",
        ("completed", "skipped") => "\u2b50\ufe0f",
        ("completed", _) => "\u26a0\ufe0f",
        ("in_progress", _) => "\ud83d\udd04",
        ("queued", _) => "\u23f3",
        ("waiting", _) => "\u23f8\ufe0f",
        _ => "\u2753"
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
        s.Length <= max ? s : s[..(max - 1)] + "\u2026";

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
