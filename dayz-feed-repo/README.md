# DayZ server feed

A scheduled GitHub Action enumerates the DayZ master list (Steam Web API), filters the
spam-flood registrations with evidence-based rules, verifies every surviving server with
direct A2S queries, and publishes the clean result to docs/servers.json every 20 minutes.

No secrets live in this repository; the Steam Web API key is an Actions secret.
