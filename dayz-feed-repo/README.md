# feedgen

The generator the feed workflow runs (`dotnet run --project dayz-feed-repo/feedgen`). It needs
`STEAM_WEB_API_KEY` in the environment, writes `servers.json` to the working directory, and
reads/writes population history in `./history` → `./history-out`.

See the [repository README](../README.md) for what it does and the output format.
