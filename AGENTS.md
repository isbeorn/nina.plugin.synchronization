# AGENTS.md

## Project Purpose

This repository contains the N.I.N.A. Synchronization plugin. It is intended for multi-camera imaging rigs that run multiple N.I.N.A. instances on one mount and need those instances to coordinate sequencing milestones.

The plugin adds two Advanced Sequencer components:

- `Synchronized Dither`: a guider trigger that waits until all active N.I.N.A. instances reach the dither point, elects one guider-connected instance as leader, runs one real dither there, and lets the other instances wait until that dither completes.
- `Synchronized Wait`: a utility sequence item that blocks all participating instances until each active instance reaches the same wait point. This is useful for aligning instances at target changes or other sequence boundaries.

Only one instance hosts the synchronization server. The first N.I.N.A. process that loads the plugin starts a named-pipe gRPC server, and all instances communicate with that server. That host instance must stay alive for the whole synchronized acquisition.

## Repository Layout

- `Synchronization.sln`: Visual Studio solution containing the plugin and service projects.
- `Synchronization/`: main N.I.N.A. plugin assembly.
  - `SynchronizationPlugin.cs`: plugin manifest/runtime entry point, server startup, status heartbeat, and plugin setting storage.
  - `Instructions/SynchronizedDither.cs`: synchronized guider trigger implementation.
  - `Instructions/SynchronizedWait.cs`: synchronized utility instruction implementation.
  - `Options.xaml` and `Options.xaml.cs`: plugin settings UI, icons, and sequence-item templates.
  - `Properties/AssemblyInfo.cs`: N.I.N.A. plugin metadata, version, minimum N.I.N.A. version, tags, URLs, and long description.
  - `Changelog.md`: user-facing release history.
  - `SynchronizationLogo.jpg`, `SynchronizationSample.jpg`, `*.svg`, `*.ai`: plugin visuals and sequence icons.
- `Synchronization.Service/`: shared named-pipe gRPC synchronization service.
  - `SyncService.proto`: gRPC service contract and generated-code source.
  - `SyncServiceServer.cs`: in-process coordination server used by the first plugin instance.
  - `SyncServiceClient.cs`: singleton client used by all plugin instructions/triggers.
  - `ISyncServiceClient.cs`: client abstraction used by instruction code.
- `.github/workflows/github-action.yaml`: release pipeline that builds the .NET 10 solution and packages the plugin, service and gRPC runtime dependencies.

## Runtime Architecture

The plugin uses `GrpcDotNetNamedPipes` on the pipe name `NINA.Synchronization.Service.Sync`.

`SynchronizationPlugin.Initialize()` calls `StartServerIfNotStarted()`. A global mutex based on the plugin identifier prevents multiple N.I.N.A. instances from starting competing servers. If the pipe does not already exist, the instance creates a `NamedPipeServer`, binds `SyncServiceServer.Instance`, and starts a server heartbeat that writes the current sync status through `IApplicationStatusMediator`.

The service tracks state by `source`, where the current sources are `SynchronizedDither` and `SynchronizedWait`. Keeping separate source keys prevents one instruction type from unblocking another.

Each participating instance has a process-local GUID client id. Instructions register when their parent sequence context is running and unregister on teardown. The client sends a heartbeat once per second while registered. The server treats clients whose heartbeat timestamp is older than 30 seconds as inactive.

The normal synchronization flow is:

1. Register the client for a source.
2. Announce that this client reached the sync point and whether it can lead.
3. Wait until all active registered clients for that source have announced.
4. Elect a leader from announced clients where `canLead` is true.
5. The leader marks the sync in progress, performs the leader-only work, then marks it complete.
6. Non-leaders wait for completion.
7. Clients withdraw/unregister when canceled or torn down.

For `SynchronizedDither`, `canLead` is true only when `IGuiderMediator.GetInfo().Connected` is true. For `SynchronizedWait`, all instances announce `canLead = true` because the leader only marks the wait complete.

## Key Behavior To Preserve

- Do not let `SynchronizedDither` and `SynchronizedWait` share synchronization state. They must stay isolated by source name.
- Keep registration balanced. Multiple instructions can register the same client/source, and the server uses a registration count before fully removing a client.
- Keep the heartbeat behavior aligned with registration. Heartbeats are how the server detects dead/inactive instances.
- Preserve cancellation handling. Leaders should mark the sync complete when canceling during leader work; waiting clients should withdraw so the server does not wait on stale participants.
- Preserve the guider-connected leader rule for dithering. Only an instance connected to a guider should run the actual dither.
- Preserve the meridian-flip guard in `SynchronizedDither.ShouldTrigger()`.
- Be careful with timeout semantics. The user setting `DitherWaitTimeout` is a global maximum sync wait timeout in seconds and defaults to 300.

## Build And Development

The projects target `net10.0-windows` and use WPF:

```powershell
dotnet restore .\Synchronization.sln
dotnet build .\Synchronization.sln -c Debug
```

The plugin depends on `NINA.Plugin` version `3.3.0.1053-nightly`. The service project uses `Grpc.Tools` version `2.83.0` to generate the gRPC contract code.

The `NINA.Plugins.Test` project covers service coordination, named-pipe client behavior and deployment targets. Run it together with a solution build, then plan manual verification in multiple N.I.N.A. instances for behavioral or runtime dependency changes. The important manual scenarios are:

- two or more instances reach `Synchronized Wait` and all continue only after everyone arrives;
- only one guider-connected instance leads `Synchronized Dither`;
- non-leader instances wait while the leader dithers and resume after completion;
- timeout/cancellation does not leave later sync points stuck;
- closing the server-hosting N.I.N.A. instance during a sequence is handled or documented for the user.

The main plugin project has a post-build target that attempts to sign release builds and copy output files into `%localappdata%\NINA\Plugins\3.0.0\Synchronization`. The target has `IgnoreExitCode="true"`, but still be aware of local deployment side effects when building on Windows.

## Packaging Notes

The deployed plugin needs the main plugin assembly plus service/runtime dependencies. The current post-build copy list includes:

- `Synchronization.dll`
- `NINA.Plugins.Synchronization.Service.dll`
- `Grpc.Core.Api.dll`
- `GrpcDotNetNamedPipes.dll`
- `Google.Protobuf.dll`

If dependency versions or target frameworks change, revisit both the post-build copy list and the publishing pipeline.

## Code Style Notes

- This codebase uses C# with explicit N.I.N.A. MEF exports and WPF resource dictionaries. Follow the existing pattern for new sequence items or triggers.
- Plugin settings are stored with `PluginOptionsAccessor` using the main plugin assembly GUID from `AssemblyInfo.cs`.
- The gRPC contract lives in `SyncService.proto`; update server, client, and generated call sites together when changing protocol behavior.
- Keep changes narrowly scoped. The synchronization protocol is stateful and shared across all N.I.N.A. instances, so small-looking changes can alter multi-instance behavior.
- Prefer logging through `NINA.Core.Utility.Logger`, matching the existing severity conventions.
