# SteamManager — Claude Instructions

## After Every Code Change

After completing any code change, **restart the local dev server** automatically:

```bash
pkill -f "SteamManager.Web" 2>/dev/null; sleep 1
/Users/hugomin/.dotnet/dotnet run --project src/SteamManager.Web/SteamManager.Web.csproj
```

Run in background and wait ~8s, then confirm it prints `Now listening on: http://0.0.0.0:5066`.

## Local Dev Environment

- **dotnet**: `/Users/hugomin/.dotnet/dotnet`
- **URL**: http://localhost:5066
- **DB**: MySQL on NAS at `192.168.71.39:3306`, db=`steam_manager`, user=`steam_mgr`
- **Env vars**: set in `src/SteamManager.Web/Properties/launchSettings.json` (http profile)
