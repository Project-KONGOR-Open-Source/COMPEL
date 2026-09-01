<h3>
    <p align="center">COMPEL</p>
    <p>Heroes Of Newerth match server launcher to connect to the Project KONGOR services.</p>
    <p>If you would like to support the development of this project and buy me a coffee, please consider one of the following options: <a href="https://github.com/sponsors/K-O-N-G-O-R">GitHub Sponsors</a>, <a href="https://www.patreon.com/newerth">Patreon</a>, <a href="https://paypal.me/MissingLinkMedia">PayPal</a>. 💚</p>
</h3>

<hr/>

<br/>

## Overview

COMPEL is a cross-platform, Native-AOT ASP.NET Core application that launches and supervises Heroes Of Newerth match servers on a host machine. Some of its functions are the following:

- synchronises the match server distribution from the CDN (incremental, hash-verified, atomic) into its own directory, then launches and supervises the Heroes Of Newerth manager process, restarting it if it exits unexpectedly
- runs a managed, cross-platform UDP proxy when enabled, forwarding the public game and voice ports to the local server ports and authenticating clients with the challenge protocol they require on that port range
- answers the master server's UDP latency pings
- exposes an HTTP control plane so it can be pinged for latency, queried by the master server, and managed remotely by the host operator

It ships as a single self-contained binary plus a self-describing `COMPEL.json`. The match server distribution is synchronised into the same directory as the executable.

## Configuration

All host-facing configuration lives in a single `COMPEL.json` beside the executable, in the self-documenting `{ "Value": …, "Description": … }` format. The release archives ship with a default one already in place; when it is missing (for example when running from source), COMPEL generates it on first run and stops so it can be edited. Every value is validated at startup, and all startup problems are reported together.

| Key                     | Purpose                                                                                                                                                                      |
| ----------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `UserName` / `Password` | The Project KONGOR host account credentials.                                                                                                                                 |
| `Instances`             | Number of match server instances (1 … logical processor count).                                                                                                              |
| `WarmInstancesTarget`   | Number of instances kept warm and idle (0 to `Instances`); the rest sleep and wake on demand. Default `1`.                                                                   |
| `Gateway`               | `kongor.net`, `localhost`, `PUBLIC` (auto-detect public IP), an IP address, or a host name.                                                                                  |
| `Location`              | TMM region: `USW`, `USE`, `EU`, `AU`, `BR`, `RU`, `SEA`, or `NEWERTH`.                                                                                                       |
| `ServerNamePrefix`      | The base match server name. The instance index is appended.                                                                                                                  |
| `UseProxy`              | Whether to run the proxy (public port remapping + client challenge authentication. Defaults to `true`).                                                                      |
| `PortRangeOffset`       | Offset into the game/voice port windows. `base + offset + instances` must stay within the 100-port window.                                                                   |
| `RuntimeArtefactsPath`  | `DEFAULT` (the host account's profile) or a fully qualified path. Windows only, as the runtime artefacts path is hard-coded for the Linux server distribution.               |
| `CDNSynchronisation`    | Whether to synchronise the distribution from the CDN on startup. Set `false` to skip the initial synchronisation for development/testing (the `/sync` endpoint still works). |
| `AuthenticationToken`   | Bearer token gating the management endpoints. Leave as `...` to disable remote management.                                                                                   |
| `ControlPlanePort`      | TCP port for the HTTP control plane (default `8080`).                                                                                                                        |

## Running

```
dotnet run --project source/COMPEL
```

The release archives ship with a default `COMPEL.json` next to the executable; when it is missing, COMPEL writes one on first run and exits. Set at least `UserName` and `Password` (and `AuthenticationToken` to enable remote management, or `Gateway` to `localhost` for a local master server), then run COMPEL. Logs are written to the console and to a single `COMPEL.log` beside the executable.

## Control Plane

| Method | Route                                                       | Authentication | Purpose                                                                              |
| ------ | ----------------------------------------------------------- | -------------- | ------------------------------------------------------------------------------------ |
| `GET`  | `/ping`                                                     | none           | Latency probe.                                                                       |
| `GET`  | `/health`, `/alive`                                         | none           | Readiness and liveness.                                                              |
| `GET`  | `/status`                                                   | bearer         | Configuration, ports, distribution version, sync state, manager/proxy state, uptime. |
| `POST` | `/sync`                                                     | bearer         | Trigger a CDN re-synchronisation.                                                    |
| `POST` | `/instances/start`, `/instances/stop`, `/instances/restart` | bearer         | Manage match server lifecycles.                                                      |

Authenticate management requests with `Authorization: Bearer <AuthenticationToken>`, using the `AuthenticationToken` from `COMPEL.json`.

## Building & Publishing

Requires the .NET 11 SDK. A native, self-contained release is produced per platform via the publish profiles, or locally with the helper script (PowerShell 7+; on Windows, the Visual Studio C++ build tools are required for the Native AOT link step):

```
pwsh scripts/Publish-Native-AOT-Release.ps1
```

Tagged pushes (`vX.Y.Z`) trigger `.github/workflows/publish-releases.yml`, which builds all four platform targets (Windows and Linux, each × x64 and arm64) and attaches one zip per platform, each containing the binary and a default `COMPEL.json`, to a single GitHub Release.

## Testing

```
dotnet test --solution source/COMPEL.slnx
```

The `COMPEL.Tests` project (TUnit) covers the port arithmetic, manager arguments, configuration validation and loading, the ping and proxy-challenge wire formats, and the proxy relay over loopback. The `global.json` at the repository root selects the Microsoft Testing Platform runner that TUnit requires. `.github/workflows/run-unit-tests.yml` runs the suite on Windows and Linux for every pull request to `main`.

## Solution Layout

```
scripts/    Native AOT release helper
source/     COMPEL.slnx, Directory.Build.props/.targets, .editorconfig, the COMPEL project, and the COMPEL.Tests project
```

<br/>
