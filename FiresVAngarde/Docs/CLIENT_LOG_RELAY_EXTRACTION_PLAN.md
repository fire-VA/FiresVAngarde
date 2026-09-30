# Client Log Relay — Extraction Plan

**Goal:** Split the "on every client login, gather the client's BepInEx log + mod list, parse
errors/warnings, persist to disk per-client, forward to Discord" pipeline into a self-contained
module folder (`Modules/ClientLogRelay/`) that can be copy-pasted into any mod with only a
namespace find/replace.

---

## Current Code Flow (before extraction)

```
Client connects
    ?
    ?
ZNet.OnNewConnection (Harmony postfix in VAngardePatches)
    ?  registers peer.m_rpc.Register<ZPackage>("VA_vgc" / "VA_vgr")
    ?
VAngardeCore.OnPeerValidated(peer)
    ?  - creates PeerState
    ?  - SendChallenge(state)  ? client.m_rpc.Invoke("VA_vgc", nonce + timestamp)
    ?
[Client] VAngardeCore.HandleServerChallenge(rpc, pkg)  [in the same VAngardeCore.cs]
    ?  - enumerates BepInEx plugins ? modList
    ?  - reads BepInEx/LogOutput.log ? logBytes
    ?  - computes SHA256(nonce | sorted modlist) ? hash
    ?  - rpc.Invoke("VA_vgr", nonce + modlist + hash + logBytes)
    ?
[Server] VAngardeCore.RPC_OnChallengeResponse(rpc, pkg)
    ?  1. verifies nonce
    ?  2. reads modlist
    ?  3. verifies hash
    ?  4. reads logBytes
    ?  ?? VAngardeModValidator.CacheClientLog     (writes LogOutput.log)
    ?  ?? VAngardeModValidator.CacheClientLoginArtifacts
    ?        • writes LogOutput.log
    ?        • writes modlist.txt
    ?        • writes errors_warnings.txt (via ExtractErrorsAndWarnings)
    ?  ?? DiscordIntegrationCore.OnClientLoginArtifacts
    ?        • builds embed + attaches all 3 files
    ?        • DiscordWebhookService.SendEmbedWithFiles
    ?  ?? VAngardeModValidator.ValidateModList    (anti-cheat)
    ?  ?? state.Validated = true
```

### Problems with the current layout

1. **Anti-cheat semantics are mixed with generic log pipeline semantics.** The challenge nonce +
   hash verification lives in `VAngardeCore` but it's really a wire-protocol concern (preventing
   replay/tamper of the uploaded bytes), not an anti-cheat policy concern.

2. **`VAngardeModValidator` owns log parsing + file writing + disk persistence.** These have
   nothing to do with mod validation — they're portable utilities.

3. **`DiscordIntegrationCore` owns the embed builder for login artifacts.** This makes it
   impossible to use the pipeline in a mod that doesn't import the entire Discord integration
   suite (bot listener, mention resolver, status tracker, etc.).

4. **Single-consumer assumption.** If another mod in the same process also wants to receive
   login artifacts, there is no fan-out path — VAngarde would have to be modified to know about it.

---

## Target Architecture

```
???????????????????????????????????????????????????????????????????
? Modules/ClientLogRelay/   ? THE PORTABLE MODULE                 ?
?                                                                 ?
?   ClientLogRelay.cs                                             ?
?     • ReportArtifacts(ClientLogArtifacts) ? public entry point  ?
?     • RegisterConsumer(IClientLogConsumer)                      ?
?     • UnregisterConsumer(IClientLogConsumer)                    ?
?     • HasConsumers                                              ?
?                                                                 ?
?   ClientLogArtifacts.cs      (DTO)                              ?
?   IClientLogConsumer.cs      (interface)                        ?
?   LogErrorWarningExtractor.cs (pure parser + benign filter)     ?
?   ClientLogArtifactWriter.cs  (pure file writer)                ?
?                                                                 ?
?   Consumers/                                                    ?
?     DiskConsumer.cs          (writes to a per-mod folder)       ?
?     DiscordWebhookConsumer.cs (fires a Discord embed)           ?
?                                                                 ?
?   Webhook/                                                      ?
?     MinimalWebhookPoster.cs  (self-contained multipart POST)    ?
?                                                                 ?
?   README.md                  (drop-in instructions)             ?
???????????????????????????????????????????????????????????????????

             ?
             ?  (register + call)
             ?
???????????????????????????????????????????????????????????????????
? Modules/VAngarde/                                               ?
?   VAngardeCore.cs                                               ?
?     RPC_OnChallengeResponse(...)                                ?
?       ... nonce/hash verification, modlist read, log read ...   ?
?       ClientLogRelay.ReportArtifacts(new ClientLogArtifacts     ?
?       {                                                         ?
?         PlatformId = ..., PlayerName = ...,                     ?
?         LogBytes = ..., ModList = ...                           ?
?       });                                                       ?
?       ... anti-cheat mod validation continues ...               ?
???????????????????????????????????????????????????????????????????

???????????????????????????????????????????????????????????????????
? Ascend.cs (or per-mod bootstrap)                                ?
?   ClientLogRelay.RegisterConsumer(new DiskConsumer(            ?
?       () => VAngardeConfig.ClientLogsDir));                     ?
?   ClientLogRelay.RegisterConsumer(new DiscordWebhookConsumer(  ?
?       () => DiscordIntegrationConfig.ClientLogWebhookUrl.Value, ?
?       () => DiscordIntegrationConfig.NotifyClientLoginArtifacts ?
?               .Value,                                           ?
?       /* attach toggles */ …));                                 ?
???????????????????????????????????????????????????????????????????
```

### Why the relay does NOT own the RPC

Giving the relay its own RPC would duplicate work (two challenge responses per login) and would
double-bill the client for bandwidth. Instead:

- **Wire transport is the consuming mod's responsibility** (or it can use a stock helper the relay
  provides later).
- **Post-byte processing is the relay's responsibility** — parse, persist, fan out.

This keeps the relay ~400 LOC of pure C# with zero Valheim networking dependencies (except the
two utility types `ZNetPeer` / `ZPackage` for the consumer interface data, which are optional).

---

## Files to Create

| Path | Role | Approx LOC |
|---|---|---|
| `Modules/ClientLogRelay/README.md` | Drop-in instructions + namespace rename | — |
| `Modules/ClientLogRelay/ClientLogArtifacts.cs` | DTO (platformId, playerName, logBytes, modList) | 40 |
| `Modules/ClientLogRelay/IClientLogConsumer.cs` | Consumer interface | 25 |
| `Modules/ClientLogRelay/ClientLogRelay.cs` | Entry point + fan-out | 120 |
| `Modules/ClientLogRelay/LogErrorWarningExtractor.cs` | Parser + benign filter + dedup | 220 |
| `Modules/ClientLogRelay/ClientLogArtifactWriter.cs` | Per-dir file writer | 130 |
| `Modules/ClientLogRelay/Consumers/DiskConsumer.cs` | Generic disk persister | 80 |
| `Modules/ClientLogRelay/Consumers/DiscordWebhookConsumer.cs` | Discord embed builder + multipart POST | 180 |
| `Modules/ClientLogRelay/Webhook/MinimalWebhookPoster.cs` | Self-contained HTTP multipart POST | 150 |

Total new code: **~950 LOC**, all in one folder with one root namespace
(`VerdantsAscent.Modules.ClientLogRelay`).

## Files to Modify

| Path | Change |
|---|---|
| `Modules/VAngarde/VAngardeCore.cs` | `RPC_OnChallengeResponse` calls `ClientLogRelay.ReportArtifacts` instead of the old two calls |
| `Modules/VAngarde/VAngardeModValidator.cs` | Remove `ExtractErrorsAndWarnings`, `CacheClientLoginArtifacts`, `IsBenignWarning`, `NormalizeLogLineForDedup`, `_benignWarningPatterns` (all moved to relay) |
| `Modules/Discord/DiscordIntegrationCore.cs` | Remove `OnClientLoginArtifacts` body (moved to `DiscordWebhookConsumer` in the relay); keep the method as a thin deprecation-forwarder or delete |
| `Ascend.cs` | On server init, register `DiskConsumer` + `DiscordWebhookConsumer` with the relay |

## What Stays Unchanged

- `VAngardePatches.cs` — still registers the VA_vgr RPC on peer connect, still calls
  `OnPeerValidated`. The challenge/response wire protocol is untouched.
- `VAngardeCore.HandleServerChallenge` (client side) — still reads the log + modlist and sends
  the response. This is the wire transport, relay does not own it.
- `DiscordWebhookService.cs` + `DiscordEmbed` / `DiscordFileAttachment` / `DiscordColors` in
  `Modules/Discord/` — the existing webhook service stays put and is still used by all the
  other Discord integrations (bot listener, status tracker, etc.). The relay has its OWN
  minimal poster inside `Modules/ClientLogRelay/Webhook/` to keep the module self-contained.

---

## Phased Execution

### Phase 1 — Scaffolding (this change)
Create all the new files, wire `RPC_OnChallengeResponse` to call into the relay, register the
two default consumers at plugin init. Old `CacheClientLoginArtifacts` / `OnClientLoginArtifacts`
become dead code but stay for safety.

### Phase 2 — Cleanup
Delete the now-dead methods from `VAngardeModValidator` and `DiscordIntegrationCore`. Build,
smoke test.

### Phase 3 — Documentation
Write `Modules/ClientLogRelay/README.md` with:
- How to copy the folder into a new mod
- Single-point namespace find/replace (`VerdantsAscent.Modules.ClientLogRelay` ? `MyMod.ClientLogRelay`)
- Minimum registration snippet
- How to implement a custom `IClientLogConsumer`

---

## Portability Contract (what "copy-pasteable" actually means)

When the user drops `Modules/ClientLogRelay/` into another mod, they must:

1. Find/replace the single namespace root:
   `VerdantsAscent.Modules.ClientLogRelay` ? `TheirMod.ClientLogRelay`
2. Register at least one consumer from their plugin's `Awake()`.
3. Call `ClientLogRelay.ReportArtifacts(...)` from wherever their mod receives the client bytes
   (VAngarde-style challenge, a simple routed RPC, a REST endpoint — transport doesn't matter).

Zero external file edits, zero BepInEx config migrations, zero ConfigSync churn.

The relay takes **only** these dependencies:

- `UnityEngine` (for `Debug.Log`)
- `System.IO` (for file writes)
- `UnityEngine.Networking.UnityWebRequest` (inside `Webhook/MinimalWebhookPoster.cs`)
- `Newtonsoft.Json` (already in any BepInEx mod via Valheim's shipped libs)

It does NOT depend on:
- BepInEx configuration APIs (consumers own their own config)
- ConfigSync (consumers own their own sync)
- ZNetPeer / ZRpc (consumers own their own wire transport)
- Any Valheim-specific types
