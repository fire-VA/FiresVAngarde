# FiresVAngarde

The server guardian for the Fires mod family. It keeps a server's mod list honest and its characters
server-owned.

Players normally never notice it. When a client's mods don't match what the server requires, the player gets
a panel naming exactly what to REMOVE, UPDATE (their version -> the server's) or INSTALL, instead of a silent
boot to the main menu. Fix the list, reconnect, done.

## Requires

- BepInExPack for Valheim
- FiresUnifiedCore
- FiresDiscordIntegration (optional) - when it is installed, kicks, violations and admin-command snapshots
  post to Discord.

Install it on the server. Clients need it too when the server enforces mod lists or server-owned characters,
because both are handshakes between the two.

## Mod validation

Connecting clients are challenged for their mod list, which is checked against text files in
`BepInEx/config/VAngarde/` on the server:

| file | meaning |
|---|---|
| `mod_exactmatch.txt` | required mods, at exact versions |
| `mod_whitelist.txt` | extra client mods that are allowed |
| `mod_blacklist.txt` | mods that get a client rejected |
| `mod_serveronly.txt` | server-side mods, kept out of the required set |
| `mod_adminonly.txt` | mods only admins may run |
| `exempt_players.txt` | player IDs that bypass anti-cheat entirely |
| `admin_character_exceptions.txt` | treat a real admin as a normal player on named characters |

`[VAngarde.ModValidation]` decides which checks run: `UseExactMatch`, `UseWhitelist`, `UseBlacklist` and
`EnforceModList`. `AutoUpdateModList` rewrites the required set from the server's own plugins at every
launch, so the list follows the modpack instead of going stale. `AdminModsExtendWhitelist` lets an admin who
connects with a new mod whitelist it for everyone.

The list files reload while the server runs: edit one and run `vangarde reload`. The `.cfg` itself is read at
startup, so change it with the server stopped.

## Detection

Under `[VAngarde.Detection]` and `[VAngarde.Enforcement]`:

- `DetectGodMode` is log-only and never auto-kicks, because admins use god mode legitimately.
- `DetectFlight`, `DetectSpeedHack` with `SpeedHackThreshold` in m/s, and `DetectCheatItems` with `BannedItems`.
- `ViolationAction` picks kick or log.
- `AdminBypass` exempts admins from enforcement. `AdminMonitoring` still scans and reports them, and
  `AdminCommandLogging` reports admin devcommands, god and fly toggles.
- `[VAngarde.Timing]` sets the cadence: `ChallengeTimeoutSeconds` and `PeriodicRecheckMinutes`.

**Read this before trusting it.** Anything a client reports about itself can be forged by someone determined
enough, so the `[VAngarde.HardeningV2]` audit, signature, integrity and DLL-hash toggles are telemetry, not
proof. They ship off. What the server decides for itself, and what cannot be spoofed by a client, is the
identity behind each message and whether the sender is an admin.

## Characters

VAngarde can own characters server-side, in place of the usual client-held ones.

- `[Characters] Enabled`, `BackupsToKeep` (rotating ZIP backups, default 25) and the generated `ServerKey`.
- `[Characters.Enforcement]`: `SingleCharacterMode` (one character per account here), `ForceServerCharacter`
  (characters made elsewhere are replaced by a fresh one), `HardcoreMode` (deleted on death),
  `AfkKickMinutes` (0 = off), plus `SingleCharacterAdminBypass`, `ForceServerCharacterAdminBypass` and
  `ExcludeAdminsFromAfk`.
- `[Characters.Maintenance]`: `MaintenanceMode` kicks non-admins on a `MaintenanceTimerSeconds` countdown
  until it is lifted.

Profiles move over a chunked, resumable channel that reports failure instead of dropping the peer, which is
what avoids the classic 30-second timeout on large characters at login (`[Characters.Transport]`).
`va_chan_test <sizeKB>` proves the pipe with a synthetic payload.

Logging out waits until the server confirms your character is saved ("Saving your character on the
server..."), for up to `LogoutSaveWaitSeconds`. Quitting the game (the Quit button, closing the window, Alt+F4)
logs out first, so the save always lands. Crossplay saves travel in smaller pieces, sized by
`CrossplayChunkSizeBytes` and `CrossplayInFlightBytes`.

`Characters.ServerKey` is generated on first start and signs saves so an older character file cannot be
rolled back in. Never edit or share it, and keep it when you move the server.

**New characters.** `BepInEx/config/VAngarde/PlayerTemplate.json` (a sample is created when none exists)
gives a brand-new character starter skills, starter items, a spawn point, and can skip the Valkyrie intro.

## Commands

| command | where |
|---|---|
| `vangarde status` | settings and tracked peers |
| `vangarde scan` | run a cheat scan now (server) |
| `vangarde kick <id>` | force-disconnect a peer (server) |
| `vangarde reload` | reload every list file (server) |
| `vangarde push whitelist\|blacklist\|exempt` | reload one list (server) |
| `vangarde push mods` | push your own client's mod list up as the standard (admin, on a client) |
| `vangarde bypass [on\|off]` | toggle admin bypass at runtime |
| `vangarde log <id>` | post a player's cached log to Discord (server) |
| `va_chan_test <sizeKB>` | stress-test the transfer channel |

## Notes

- Server-side settings are locked and pushed to clients, so the server's copy is the one that counts.
- The in-game Fires help panel has a VAngarde section; its admin pages show for admins.
- `[VAngarde] Enabled` and `[Characters] Enabled` are the two master switches.
