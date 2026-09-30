# Login Data Transmission Analysis

## Overview
This document analyzes all data sent to clients during login to ensure we're not overwhelming the connection with unnecessary or redundant transmissions.

## Current Login Flow

### 1. **OnNewConnection** (ZNet)
- **Trigger**: When a peer first connects (socket established)
- **Data Sent**: None at this stage
- **Notes**: `peer.m_uid` is often 0, peer.m_playerName is empty

### 2. **RPC_PeerInfo** (ZNet) - **PRIMARY CONCERN**
- **Trigger**: After peer sends their character info
- **Data Sent**:
  1. **Quest Files** - `SendAllQuestFilesToPeer(peerUid)`
  2. **Quest Database** - `BroadcastQuestDatabaseToPeer(peerUid)`
  3. **All Config Files** - `ServerConfigFileWatcher.SendAllConfigsToClient(peerUid)`
     - Dialogues
     - Territories  
     - ServerInfos
     - UILayouts
     - NPC Files
     - **ANY OTHER CONFIG FILES**

### 3. **OnPeerValidated** (VAngarde)
- **Trigger**: After ZNet validates the peer
- **Data Sent**:
  - **VAngarde Challenge** - Requests client's mod list and BepInEx log

### 4. **OnSpawned** (Player)
- **Trigger**: When player character spawns in world
- **Client -> Server**: 
  - AnnouncePlayerInfo (playerId, playerName, platformId)
  - RequestPlayerData (playerId, playerName)
- **Server -> Client**:
  - ReceivePlayerData (vault data for the player)

---

## Issues Identified

### ? **Problem 1: Quest Files Sent EVERY Login**
**Location**: `VaultPatches.cs:171` in `ZNet_RPC_PeerInfo_VaultSetup`

```csharp
QuestManager.Instance.SendAllQuestFilesToPeer(peerUid);
QuestManager.Instance.BroadcastQuestDatabaseToPeer(peerUid);
```

**Issue**: Quest files are **static server configuration** that doesn't change per-player. They should be:
- Sent once on first login
- Sent only when files change (like territories do with file hashing)
- NOT sent every single login

**Impact**: 
- Wastes bandwidth
- Increases login time
- Can cause connection issues if many quest files exist
- Unnecessary disk I/O on client

---

### ? **Problem 2: All Config Files Sent EVERY Login**
**Location**: `VaultPatches.cs:179` in `ZNet_RPC_PeerInfo_VaultSetup`

```csharp
ServerConfigFileWatcher.SendAllConfigsToClient(peerUid);
```

**Issue**: This sends **ALL** config files from multiple folders:
- Quests (already sent above, so duplicate!)
- Dialogues
- Territories (has its own smart chunking system, but this bypasses it!)
- ServerInfos
- UILayouts
- NPCs
- Any other config folder

**Impact**:
- **MASSIVE** bandwidth waste
- **SEVERE** login time increase
- **HIGH RISK** of connection timeout/failure
- Duplicate data (quests sent twice, territories bypass their chunking)

---

### ? **Problem 3: VAngarde Mod List Request Every Login**
**Location**: `VAngardeCore.cs:332` in `OnPeerValidated`

```csharp
SendChallenge(logOnlyState);
```

**Issue**: VAngarde sends a challenge **every login** requesting:
- Client's full mod list
- Client's entire BepInEx LogOutput.log file (can be HUGE)

**Current Behavior**: 
- ? **GOOD**: Admins get mod list cached and bypass scanning
- ? **BAD**: Non-admin players send this EVERY login even if their mod list hasn't changed

**Impact**:
- Large log files can be 10+ MB
- Wastes bandwidth for unchanged mod lists
- Not actually necessary for normal players who aren't being scanned

---

### ? **Good: Territory System** 
**Location**: `TerritorySyncedState.cs:767-797`

The territory system does it RIGHT:
1. Client sends **hash manifest** of files it has
2. Server only sends territories that changed
3. Uses chunking to avoid Steam rate limits
4. Client caches territories and only requests on first login or after timeout

---

## Recommended Fixes

### Fix 1: Quest Files - Add Delta Sync Like Territories
```csharp
// VaultPatches.cs - RPC_PeerInfo postfix
// BEFORE:
QuestManager.Instance.SendAllQuestFilesToPeer(peerUid);
QuestManager.Instance.BroadcastQuestDatabaseToPeer(peerUid);

// AFTER:
// Quest files should use the same manifest/hash system as territories
// Only send if client doesn't have them or they changed
QuestManager.Instance.SendQuestDeltaIfNeeded(peerUid);
```

**Implementation**:
1. Client sends hash manifest of quest files it has
2. Server compares hashes
3. Server only sends changed/new quest files
4. Store quest hash in client cache (like territories)

---

### Fix 2: Remove Duplicate Config File Sends
```csharp
// VaultPatches.cs - RPC_PeerInfo postfix
// BEFORE:
QuestManager.Instance.SendAllQuestFilesToPeer(peerUid);
QuestManager.Instance.BroadcastQuestDatabaseToPeer(peerUid);
ServerConfigFileWatcher.SendAllConfigsToClient(peerUid); // ? SENDS EVERYTHING AGAIN

// AFTER:
// Only send configs that NEED to be sent on every login (none?)
// Everything else should use delta sync or be client-cached
// If ServerConfigFileWatcher is needed, exclude folders that already have their own sync:
ServerConfigFileWatcher.SendAllConfigsToClient(peerUid, exclude: new[] { "Quests", "Territories" });
```

---

### Fix 3: VAngarde - Cache Mod List for Non-Admins
```csharp
// VAngardeCore.cs - OnPeerValidated
// Add mod list caching for regular players
private static readonly Dictionary<string, (DateTime lastCheck, Dictionary<string, string> modList)> _modListCache = new();
private static readonly TimeSpan ModListCacheExpiry = TimeSpan.FromHours(24);

// In OnPeerValidated, check cache first:
if (!isValheimAdmin && !VAngardeModValidator.IsPlayerExempt(platformId))
{
    // Check if we have a recent mod list cached
    if (_modListCache.TryGetValue(platformId, out var cached))
    {
        if (DateTime.UtcNow - cached.lastCheck < ModListCacheExpiry)
        {
            // Use cached mod list, skip challenge
            LogVerbose($"Using cached mod list for {platformId}");
            return;
        }
    }

    // Cache expired or not found, send challenge
    SendChallenge(state);
}
```

---

### Fix 4: Dialogue Files - Smart Sync
Dialogues likely have the same issue as quests. Apply the same delta sync pattern.

---

### Fix 5: NPC Files - Send Only When Needed
NPC files should only be sent when:
1. Client doesn't have them (first login)
2. An admin edited an NPC (broadcast change only)
3. NOT every single login

---

## Priority Order

### ?? **CRITICAL - Fix Immediately**
1. **Remove `ServerConfigFileWatcher.SendAllConfigsToClient()` from RPC_PeerInfo**
   - This is sending EVERYTHING every login
   - Likely the main cause of connection issues

### ?? **HIGH - Fix Soon**  
2. **Add Delta Sync to Quest Files**
   - Like territories, send manifest and only transmit changes

3. **Cache VAngarde Mod Lists for 24h**
   - Only re-request if cache expired or player changed mods

### ?? **MEDIUM - Optimize Later**
4. **Dialogue File Delta Sync**
5. **NPC File Smart Sync**
6. **UILayout Smart Sync**

---

## Testing Checklist

After implementing fixes, test:
- [ ] First-time login (should receive all files)
- [ ] Second login without server restart (should receive nothing or minimal delta)
- [ ] Login after quest file change (should only receive changed file)
- [ ] Login after territory change (verify existing system still works)
- [ ] Login with large mod list (verify VAngarde cache works)
- [ ] Monitor bandwidth usage during login (should be < 1MB for repeat logins)

---

## Estimated Impact

### Before Fixes:
- Quest files: 10-50 individual RPCs
- Config files: 50-200+ individual RPCs (depends on config count)
- VAngarde: 1 RPC + potentially 10+ MB response
- **Total**: 100-300+ RPCs on EVERY login

### After Fixes:
- Quest files: 0 RPCs (cached) or 1-5 RPCs (delta changes)
- Config files: 0-10 RPCs (only actual changes)
- VAngarde: 0 RPCs (cached for 24h) or 1 RPC if expired
- **Total**: 0-20 RPCs on repeat login

**Expected improvement**: 90-95% reduction in login data transmission
