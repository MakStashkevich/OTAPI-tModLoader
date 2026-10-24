# tModLoader 1.4.4.9 compatibility work

This fork contains experimental OTAPI compatibility changes for Terraria / tModLoader 1.4.4.9, plus an optional runtime telemetry plugin. The work lives on `yml-1444-compat` in `MakStashkevich/OTAPI-tModLoader`.

## Resume work on this fork

```sh
cd "/Volumes/T7/CodeProjects/Личное/OTAPI-tModLoader"
git switch yml-1444-compat
git pull --ff-only fork yml-1444-compat
```

The `fork` remote points to `git@github.com:MakStashkevich/OTAPI-tModLoader.git`; `origin` is the upstream `SignatureBeef/Open-Terraria-API` repository. Push this branch to `fork`, not `origin`.

Requirements:

- .NET 9 SDK to build OTAPI's patcher and launcher.
- .NET 8 runtime to run the patched tModLoader 1.4.4.9 dedicated server.
- A locally installed/extracted copy of tModLoader 1.4.4.9, including its `Libraries` directory. Game binaries are not stored in this repository.
- Initialize the FNA submodule once after a fresh clone: `git submodule update --init --recursive`.

## Build the tModLoader patch and launcher

Set `OTAPI_TML_PATH` to the directory containing `tModLoader.dll` and `Libraries/`. Then run:

```sh
export OTAPI_TML_PATH="/absolute/path/to/tModLoader"
dotnet restore OTAPI.Mods.slnf
dotnet restore OTAPI.Server.Launcher.slnf
dotnet build OTAPI.Mods.slnf -c Debug
dotnet run --project OTAPI.Patcher/OTAPI.Patcher.csproj -c Debug --framework net9.0 -- -patchTarget=t -latest=n
dotnet build OTAPI.Server.Launcher.slnf -c Debug
```

The patcher output and launcher are under `OTAPI.Patcher/bin/Debug/net9.0` and `OTAPI.Server.Launcher/bin/Debug/net9.0`. The launcher project copies files from the patched output when present. For existing worktrees with patched artifacts already generated, rebuilding the launcher is sufficient after editing launcher/plugin code.

## Runtime telemetry

`OTAPI.Server.Launcher/modifications/WorldTelemetry.Server.cs` is copied into the launcher's `modifications` directory. It streams newline-delimited JSON over loopback TCP at `127.0.0.1:38761` by default; set `OTAPI_TELEMETRY_PORT` to override the port. It reports server/world state, online players and built-in Terraria progression. The Python client reconnects automatically:

```sh
python3 examples/world-telemetry/telemetry_client.py
# Optional:
python3 examples/world-telemetry/telemetry_client.py --host 127.0.0.1 --port 38761
```

Do not run a real game/world merely to test edits. First build the launcher/plugin, then use a disposable test directory and `-test-init` if the launcher reaches its initialization test path. Never point test runs at the normal server save directory.

## Current verification state and known runtime blocker

- The telemetry C# source compiled against the generated OTAPI/tModLoader references.
- The Python client passed a syntax check and a JSON-lines fixture check.
- `OTAPI.Server.Launcher` built successfully for Debug with zero errors after the 1.4.4.9 hook guards were fixed.
- Full launcher initialization was **not** verified. A disposable `-test-init` run exited before OTAPI/module/plugin loading with `FileNotFoundException` for `Steamworks.NET, Version=1.0.0.0`, originating in tModLoader's `InstallVerifier`. Resolve the assembly binding/deployment mismatch before claiming end-to-end runtime success. This is not evidence that the telemetry plugin itself failed.

Use this disposable smoke-test pattern only after the Steamworks issue is resolved:

```sh
mkdir -p /tmp/otapi-telemetry-smoke-save
cd OTAPI.Server.Launcher/bin/Debug/net9.0
OTAPI_TELEMETRY_PORT=38762 dotnet OTAPI.Server.Launcher.dll -server -test-init -savedirectory /tmp/otapi-telemetry-smoke-save
```

The launcher targets .NET 9 in this experimental fork; the patched tModLoader runtime itself targets .NET 8. Do not remove framework guards or change dependency versions to hide load errors without validating the resulting runtime.

## Branch and commit workflow

```sh
git status --short --branch
git diff --check
git add <intended-files>
git commit -m "Describe the OTAPI change"
git push fork yml-1444-compat
```

Keep local game binaries, save data, decompilation output, and runtime logs out of Git. The current branch is experimental and has not yet passed a complete tModLoader runtime startup test.
