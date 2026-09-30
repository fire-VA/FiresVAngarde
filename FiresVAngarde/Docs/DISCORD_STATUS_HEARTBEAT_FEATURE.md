# Discord Status Heartbeat Feature

## Overview

Added a configurable status heartbeat system that posts periodic server status updates to a Discord channel, showing server uptime and currently online players. Includes automatic cleanup of messages older than 24 hours.

## Features Implemented

### 1. Status Heartbeat Messages

**Frequency**: Configurable (default: 15 minutes)  
**Channel**: Uses `StatusChannelId` config (falls back to general webhook)  
**Content**:
- ?? Server Status indicator (green if players online, yellow if empty)
- Server name
- Server uptime
- Player count
- In-game day
- List of currently online players (or "*No players connected*")
- Last updated timestamp

### 2. Automatic Message Cleanup

**Retention Period**: 24 hours  
**Mechanism**: Uses Discord Bot API to fetch and delete old messages  
**Trigger**: Runs after each heartbeat post  
**Requirements**: Bot listener must be enabled (needs bot token)

**How it works**:
1. After posting a heartbeat, triggers cleanup
2. Fetches up to 100 most recent messages from status channel
3. Parses timestamp of each message
4. Deletes messages older than 24 hours
5. Adds 0.5s delay between deletions to avoid rate limiting

### 3. Configuration Options

```ini
[Discord.Events]
## Enable periodic status heartbeat messages showing server uptime and online players
EnableStatusHeartbeat = false

## How often (in minutes) to send status heartbeat updates
## Range: 1-60 minutes
StatusHeartbeatIntervalMinutes = 15
```

```ini
[Discord.BotListener]
## Channel ID where status heartbeat messages are posted
## Falls back to ChatChannelId if empty
StatusChannelId = 

## Discord Bot Token (required for message cleanup)
BotToken = 
```

---

## Technical Implementation

### Files Modified

1. **DiscordIntegrationConfig.cs**
   - Added `EnableStatusHeartbeat` config entry
   - Added `StatusHeartbeatIntervalMinutes` config entry
   - Registered both with ConfigSync for server-locking

2. **DiscordStatusTracker.cs**
   - Added `_lastHeartbeatTimeUtc` timestamp tracker
   - Added heartbeat interval check to `Tick()` method
   - Implemented `SendStatusHeartbeat()` method
   - Implemented `TriggerOldMessageCleanup()` helper

3. **DiscordBotListener.cs**
   - Added `TriggerMessageCleanup()` public method
   - Implemented `CleanupOldMessages()` coroutine
   - Implemented `DeleteMessage()` helper coroutine

### Data Flow

```
ZNet.Update (every frame)
  ??> DiscordIntegrationPatches.ZNet_Update_Postfix
      ??> DiscordStatusTracker.Tick()
          ??> Check if heartbeat interval elapsed
          ?   ??> SendStatusHeartbeat()
          ?       ??> Build embed with current status
          ?       ??> Post to Discord webhook
          ?       ??> TriggerOldMessageCleanup()
          ?           ??> DiscordBotListener.TriggerMessageCleanup()
          ?               ??> CleanupOldMessages() coroutine
          ?                   ??> Fetch recent messages (up to 100)
          ?                   ??> Parse timestamps
          ?                   ??> Delete messages older than 24h
          ??> Check if hour elapsed (hourly summary)
          ??> Check if midnight EST (daily summary)
```

---

## Discord Bot Permissions Required

For message cleanup to work, the bot must have these permissions in the status channel:

- **Read Messages** / **View Channel** - To fetch message history
- **Manage Messages** - To delete old messages
- **Send Messages** - To post heartbeat updates (via webhook)

---

## Why 24 Hour Retention?

- **Keeps hourly summaries**: Hourly reports go to the same general channel, so 24 hours preserves the last ~24 hourly reports
- **Prevents channel spam**: Older messages are automatically cleaned up
- **Discord API friendly**: Fetches only 100 most recent messages (API limit), efficient for most use cases

---

## Example Heartbeat Embed

```
?? Server Status

Server: Verdant's Ascent
Uptime: 2d 14h 35m
Players Online: 3
In-Game Day: 142

Currently Online:
PlayerOne, PlayerTwo, PlayerThree

Last updated: 14:23:45
```

---

## Testing Checklist

- [ ] Enable `EnableStatusHeartbeat = true` in config
- [ ] Set `StatusHeartbeatIntervalMinutes` to 1 (for quick testing)
- [ ] Configure `StatusChannelId` with a test channel ID
- [ ] Configure `BotToken` with bot token
- [ ] Start server and wait for first heartbeat (1 minute)
- [ ] Verify heartbeat appears in status channel
- [ ] Verify player list shows when players are online
- [ ] Verify player list shows "*No players connected*" when empty
- [ ] Wait 24 hours and verify old messages are deleted
- [ ] Verify hourly summaries are NOT deleted (should be in general channel)

---

## Differences from Hourly Summary

| Feature | Status Heartbeat | Hourly Summary |
|---------|------------------|----------------|
| **Frequency** | Configurable (1-60 min) | Fixed (60 min) |
| **Content** | Minimal (uptime + players) | Detailed (joins, deaths, events, etc.) |
| **Purpose** | Quick server status check | Comprehensive activity report |
| **Channel** | StatusChannelId | GeneralWebhookUrl |
| **Cleanup** | Auto-deletes old messages | Kept forever |
| **Config** | `EnableStatusHeartbeat` | `NotifyHourlySummary` |

---

## Future Enhancements

Possible improvements for later:

1. **Edit instead of post**: Store message ID and edit the same message instead of posting new ones
2. **Configurable retention**: Add config for message retention period (currently fixed at 24h)
3. **Player avatars**: Show Steam avatars in embed (requires Steam API)
4. **Performance metrics**: Add FPS, memory usage, tick rate
5. **Backup count**: Show when last backup was created
6. **Mod count**: Show number of active mods on server
