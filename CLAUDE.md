# SteamManager — Claude Instructions

## After Every Code Change

After completing any code change, **restart the local dev server** automatically:

```bash
pkill -f "SteamManager.Web" 2>/dev/null; sleep 1
export $(grep -v '^#' .env | xargs)
/Users/hugomin/.dotnet/dotnet run --project src/SteamManager.Web/SteamManager.Web.csproj
```

Run in background and wait ~8s, then confirm it prints `Now listening on: http://0.0.0.0:5066`.

> Secrets (`DB_PASSWORD`, `SESSION_ENCRYPTION_KEY`, etc.) live in `.env` (gitignored). `launchSettings.json` only sets `ASPNETCORE_ENVIRONMENT`; all other env vars come from the shell after the export above.

## Release Process

When asked to publish a version:

1. **Review commits** — `git log <last-tag>..HEAD --oneline` to summarize what changed
2. **Update README** — add a new `### vX.Y.Z` entry at the top of the `## Changelog` section in `README.md`
3. **Write changelog** — create `docs/changelog/X.Y.Z.md` (bilingual EN + ZH) following the existing pattern in that directory
4. **Commit** — stage all modified/new files and commit with message `docs: vX.Y.Z — <one-line summary>`
5. **Tag** — `git tag vX.Y.Z`
6. **Push** — `git push && git push --tags`
7. **Stop local server** — `pkill -f "SteamManager.Web" 2>/dev/null`

Version bump rules:
- **Minor version** (patch): last digit +1 → `v0.3.0` → `v0.3.1`
- **Major version** (feature): middle digit +1, last resets → `v0.3.1` → `v0.4.0`

