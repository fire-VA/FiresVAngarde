# Login Hang Fix Plan

## Evidence

Client log excerpt (abbreviated) during a login:

```
[UILayoutSyncRPC] Queued 2 chunks to send to peer -76614994: vanilla\vanilla_hud.json (228246 bytes, compressed=True, raw=5815610)
[FiresRPGmaker] Sent 10 UILayout file(s) to client -76614994
[SavedNpcManager] Sent 42 NPC entries to client -76614994
```

The client freezes for several seconds once the chunks land. The freeze is not
network I/O; it is synchronous work on the Unity main thread triggered by our
RPC handlers and the `SendAllConfigsToClientCoroutine` fan-out.

## Root Causes (ranked by cost)

1. **`UILayoutCodex.Reload()` called per-file on receive.**
   `UILayoutSyncRPC.ApplyLayoutLocally` writes the JSON then calls
   `UILayoutCodex.Reload()` ? `UILayoutSerializer.LoadAll()` ?
   `Directory.GetFiles(..., AllDirectories)` + `File.ReadAllText` + a hand-written
   recursive-descent JSON parser for **every** `.json` under `UILayouts/`.
   Ten incoming layouts at ~5.8 MB raw ? O(N²) parses on the main thread.

2. **Synchronous disk I/O on the RPC callback frame.**
   `File.WriteAllText(fullPath, jsonContent)` of a 5.8 MB string plus
   `Directory.CreateDirectory` happen inside `RPC_OnUpdateChunk`'s final-chunk
   branch. Same frame as the decompression spike.

3. **Large-buffer decompression on the last chunk.**
   `DecodePayload` GZip-decompresses into a `MemoryStream` then calls
   `Encoding.UTF8.GetString` on the 5.8 MB buffer ? allocation + copy.

4. **`ClientLogCollector.ReadLocalBepInExLog()` on the challenge-response path.**
   Reads the entire `BepInEx/LogOutput.log` synchronously from
   `VAngardeCore.HandleServerChallenge`, which runs on the main thread.

5. **`ServerConfigFileWatcher.SendAllConfigsToClientCoroutine` opens with a hard
   `WaitForSeconds(1f)`** ? pure added latency before anything is sent.

## Fix Plan

Implement in priority order. Each phase builds cleanly on the previous.

### Phase 1 ? Debounce codex reload _(highest leverage)_

- Replace the per-file `UILayoutCodex.Reload()` call in
  `UILayoutSyncRPC.ApplyLayoutLocally` with a dirty flag + timestamp
  (`_codexDirty`, `_codexDirtySinceUtc`).
- Extend the `DrainSendQueue()` pump (already called each `Update`) to check the
  flag and invoke `UILayoutCodex.Reload()` exactly once after ~250 ms of quiet,
  or immediately if the queue drains and the client ends an RPC batch.
- Net effect: N² full-codex parses collapse to **one** per login burst.

### Phase 2 ? Content-hash short-circuit on receive _(pairs with Task A manifest exchange)_

- In `ApplyLayoutLocally`, before writing:
  - If the target file exists and its SHA256 matches the incoming JSON's
    SHA256, **skip** the `File.WriteAllText` and skip setting `_codexDirty`.
  - Reuse `UILayoutSyncRPC.ComputeSha256(string)` already added for the
    manifest path.
- Combined with the manifest-based skip on the server side, a joining peer
  whose files are already current does zero disk I/O and zero reloads.

### Phase 3 ? Off-thread BepInEx log read

- Introduce `ClientLogCollector.ReadLocalBepInExLogAsync(Action<byte[]>)`:
  schedules the file read on a ThreadPool worker (`Task.Run`), marshals the
  result back to the main thread via `MainThreadDispatcher.Enqueue`.
- Update `VAngardeCore.HandleServerChallenge` to build the challenge response
  from the async callback; the RPC `Invoke` still happens on the main thread.
- Removes a multi-megabyte file read from the login-critical frame budget.

### Phase 4 ? Shorter initial send delay

- Replace `yield return new WaitForSeconds(1f)` at the top of
  `SendAllConfigsToClientCoroutine` with `WaitForSeconds(0.25f)`.
- The sleep exists so the client's RPC table is fully registered before the
  server fires routed RPCs at it. 250 ms is empirically sufficient and cuts a
  fixed 750 ms off every login.

### Phase 5 (future, not in this pass)

- Move `File.ReadAllText` of layout / NPC / cfg files off the main thread in
  `SendAllConfigsToClientCoroutine` (`BackgroundWorker`).
- Stream `SavedNpcManager.SendAllNpcDataToClient` and
  `QuestManager.BroadcastQuestDatabaseToPeer` in N-per-frame batches instead of
  tight loops.
- Consider replacing `UILayoutSerializer`'s hand-written parser with
  Newtonsoft (already shipped with Valheim) for a ~10? parse speedup on large
  layouts.

## Files Expected to Change

| Phase | File |
|------:|------|
| 1 | `FiresNPCs/UI/UIBuilderSDK/UILayoutSyncRPC.cs` |
| 2 | `FiresNPCs/UI/UIBuilderSDK/UILayoutSyncRPC.cs` |
| 3 | `FiresNPCs/Modules/ClientLogRelay/Transport/ClientLogCollector.cs` |
| 3 | `FiresNPCs/Modules/VAngarde/VAngardeCore.cs` |
| 4 | `FiresNPCs/Managers/ServerConfigFileWatcher.cs` |

## Validation

1. `dotnet build` (Visual Studio build) ? green.
2. Manual login test: confirm the `[UILayoutSyncRPC] Queued N chunks ... `
   log lines arrive without the Unity window becoming unresponsive.
3. Confirm only **one** `[UILayoutCodex] Loaded N layout(s)` per login burst.
4. Confirm identical-file logins produce zero `Wrote` / `Reloaded` spam.
