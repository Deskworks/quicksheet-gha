# quicksheet-gha

> **GitHub Actions workflow status on your desktop wallpaper** — no browser tab needed.

A [QuickSheet](https://github.com/cemheren/QuickSheet) extension that shows live GitHub Actions workflow run statuses for any public (or private, with a token) repository.

## Usage

In any QuickSheet cell:

```
gha: owner/repo
```

```
gha: cemheren/QuickSheet, 8
```

The second parameter controls how many runs to display (default: 10, max: 20).

## Output

```
⚡ cemheren/QuickSheet (public)
Status  Workflow               Branch              Age
────────────────────────────────────────────────────────
✅     pages build and depl…  gh-pages            2m ago
✅     pages build and depl…  gh-pages            4h ago
❌     CI                      feat/undo           2h ago
🔄     CI                      main                1m ago
⏳     Deploy                  main                 0s ago
```

**Status icons:**

| Icon | Meaning     |
|------|-------------|
| ✅   | success     |
| ❌   | failure     |
| 🔄   | in progress |
| ⏳   | queued      |
| ⏸️   | waiting     |
| 🚫   | cancelled   |
| ⏱️   | timed out   |
| ⏭️   | skipped     |

## Auth (optional — private repos + higher rate limits)

The GitHub API allows ~60 unauthenticated requests/hour. For private repos or to increase the rate limit, set a `GITHUB_TOKEN` environment variable:

```bash
export GITHUB_TOKEN=ghp_your_token_here
```

Or add it to your shell profile. The extension picks it up automatically.

## Install

```bash
ext: github:cemheren/quicksheet-gha
```

In a QuickSheet cell. QuickSheet will clone, build, and register the extension automatically.

## Requirements

- [QuickSheet](https://github.com/cemheren/QuickSheet) v0.1.0+
- .NET 9 SDK
- Internet access (uses `api.github.com`)

## How it works

- Calls `GET /repos/{owner}/{repo}/actions/runs` via the free GitHub REST API v3
- Caches results for 5 minutes to avoid rate-limiting
- Runs are sorted newest-first by GitHub's API
- Zero NuGet dependencies — pure .NET 9 `HttpClient`

## Related extensions

- [quicksheet-ghpr](https://github.com/cemheren/quicksheet-ghpr) — Pull request dashboard
- [quicksheet-gitlog](https://github.com/cemheren/quicksheet-gitlog) — Recent git commits
- [quicksheet-apistatus](https://github.com/cemheren/quicksheet-apistatus) — Service status pages
- [quicksheet-health](https://github.com/cemheren/quicksheet-health) — HTTP endpoint health

---

Part of the [QuickSheet](https://github.com/cemheren/QuickSheet) extension ecosystem.
