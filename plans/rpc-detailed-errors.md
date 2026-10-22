# Plan: detailed gRPC errors only in Development

## Context
Owner hosts set `EnableDetailedErrors = true` unconditionally. Analysis shows the client only uses
`StatusCode` for control flow (`SyncAgent.cs:571,597`); `Status.Detail` feeds only the diagnostic
`LastError` text (`SyncAgent.cs:279`, `AgentViewModel.cs:136`). No behavior depends on it, so production
hosts can turn it off. Tests assert on detail text (`SubscribeSessionTests.cs:22,59-60`,
`CursorCommitTests.cs:30`), so test/lab rigs must keep it on.

## Findings that shape the plan
- `AddGrpc` is called only by hosts, never by the library. `OwnerHostingExtensions.ConfigureSyncGrpcServiceOptions`
  (`Replication.Core/Owner/OwnerHostingExtensions.cs:25`) sets only per-service options, and `EnableDetailedErrors`
  is a global `GrpcServiceOptions` setting, so it cannot go there.
- The two repo hosts are `Sample.Lab/OwnerHost.cs:48-55` and `Replication.Tests/MultiOwner/TwoOwnerRig.cs:63-67`.
  Both use `WebApplication.CreateSlimBuilder()`, whose environment defaults to Production.
  `IsDevelopment()` would therefore be false and break the tests.

## Changes
1. `Sample.Lab/OwnerHost.cs:51-54` and `Replication.Tests/MultiOwner/TwoOwnerRig.cs:63-66`: leave `EnableDetailedErrors = true`.
   Add a one-line comment: lab/test rig, tests assert on error detail; a real host should use `builder.Environment.IsDevelopment()`.
2. `Replication.Core/Owner/OwnerHostingExtensions.cs`: add an XML doc note on `ConfigureSyncGrpcServiceOptions`
   saying `EnableDetailedErrors` is the host's choice, is not required by the client, and only affects the
   `LastError` text. No code change in the library.
3. Real-project host (outside this repo) uses:
   ```csharp
   .AddGrpc(o => o.EnableDetailedErrors = builder.Environment.IsDevelopment())
   .ConfigureSyncGrpcServiceOptions();
   ```

## Optional (not in scope unless requested)
Throw `RpcException` with a deliberate status/message at known failure points (as `SubscribeSession.cs:51` does)
so production `LastError` stays useful without leaking internals.

## Verification
- `dotnet build` and `dotnet test` in `src`: `SubscribeSessionTests` and `CursorCommitTests` must still pass.
- Manual: in a Production-environment host with the flag false, force a server exception and confirm the client
  logs `Unknown: Exception was thrown by handler.` and still retries.
