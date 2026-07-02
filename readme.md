<h3>
    <p align="center">COMPEL</p>
    <p>Heroes Of Newerth match server launcher to connect to the Project KONGOR services.</p>
    <p>If you would like to support the development of this project and buy me a coffee, please consider one of the following options: <a href="https://github.com/sponsors/K-O-N-G-O-R">GitHub Sponsors</a>, <a href="https://www.patreon.com/newerth">Patreon</a>, <a href="https://paypal.me/MissingLinkMedia">PayPal</a>. 💚</p>
</h3>

<hr/>

<br/>

## Overview

COMPEL is a cross-platform, Native-AOT ASP.NET Core application that launches and supervises Heroes Of Newerth match servers on a host. It:

- Synchronises the match server distribution from the CDN (incremental, hash-verified, atomic) into its own directory, then launches and supervises the Heroes Of Newerth manager process, restarting it if it exits unexpectedly.
- Runs a managed, cross-platform UDP proxy (anti-cheat / anti-DDoS port remapping) when enabled, forwarding the public game and voice ports to the local server ports. Disabled by default.
- Answers the master server's UDP latency pings.
- Exposes an HTTP control plane so it can be pinged for latency, queried by NEXUS, and managed remotely by the host operator.

It ships as a single self-contained binary plus a self-describing `COMPEL.json`; the match server distribution is synchronised into the same directory as the executable.

## Configuration

All host-facing configuration lives in a single `COMPEL.json` beside the executable, in the same self-documenting `{ "Value": …, "Description": … }` format as the legacy COMPEL. On first run it is generated with defaults and descriptions, and COMPEL stops so it can be edited. Every value is validated at startup; all problems are reported together.

| Key | Purpose |
| --- | --- |
| `UserName` / `Password` | The Project KONGOR host account credentials. |
| `Instances` | Number of server instances (1 … logical processor count). |
| `Gateway` | `kongor.net`, `localhost` (local NEXUS), `PUBLIC` (auto-detect public IP), an IP address, or a host name. |
| `Location` | TMM region: `USW`, `USE`, `EU`, `AU`, `BR`, `RU`, `SEA`, or `NEWERTH`. |
| `ServerNamePrefix` | Base server name; the instance index is appended. |
| `UseProxy` | Whether to run the anti-cheat / anti-DDoS proxy (default `false`). |
| `PortRangeOffset` | Offset into the game/voice port windows; `base + offset + instances` must stay within the 100-port window. |
| `RuntimeArtefactsPath` | `DEFAULT` (the host account's profile) or a fully qualified path. Windows only. |
| `CDNSynchronisation` | Whether to synchronise the distribution from the CDN on startup; set `false` to skip the initial synchronisation for development/testing (the `/sync` endpoint still works). |
| `AuthenticationToken` | Bearer token gating the management endpoints; leave empty to disable remote management. |
| `ControlPlanePort` | TCP port for the HTTP control plane (default `8080`). |

Infrastructure that hosts do not tune — the CDN host and per-OS variants, the master-server endpoint (derived from `Gateway`), and download concurrency — is hardcoded. The distribution is installed into the same directory as the COMPEL executable.

## Running

```
dotnet run --project source/COMPEL
```

On first run COMPEL writes a default `COMPEL.json` next to the executable and exits; set at least `UserName` and `Password` (and `AuthenticationToken` to enable remote management, or `Gateway` to `localhost` for a local NEXUS), then run again. Logs are written to the console and to rolling `COMPEL` log files beside the executable.

## Control plane

| Method | Route | Auth | Purpose |
| --- | --- | --- | --- |
| `GET` | `/ping` | none | Latency probe. |
| `GET` | `/health`, `/alive` | none | Readiness and liveness. |
| `GET` | `/status` | bearer | Configuration, ports, distribution version, sync state, manager/proxy state, uptime. |
| `POST` | `/sync` | bearer | Trigger a CDN re-synchronisation. |
| `POST` | `/instances/start`, `/instances/stop`, `/instances/restart` | bearer | Manager lifecycle. |

Authenticate management requests with `Authorization: Bearer <AuthenticationToken>`, using the `AuthenticationToken` from `COMPEL.json`.

## Building & publishing

Requires the .NET 10 SDK. A native, self-contained release is produced per platform via the publish profiles, or locally with the helper script (PowerShell 7+; on Windows, the Visual Studio C++ build tools are required for the Native AOT link step):

```
pwsh scripts/Publish-Native-AOT-Release.ps1
```

Tagged pushes (`vX.Y.Z`) trigger `.github/workflows/publish-release.yml`, which builds all four platform targets (Windows and Linux, each × x64 and arm64) and attaches one zip per platform to a single GitHub Release.

## Solution layout

```
scripts/    Native AOT release helper
source/     COMPEL.slnx, Directory.Build.props/.targets, .editorconfig, and the COMPEL project
```

<br/>
