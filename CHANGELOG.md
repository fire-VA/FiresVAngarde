* v0.2.35 - server-held characters work on Valheim 1.0 and crossplay, and every way of quitting saves
  - server-held characters now save and load on Valheim 1.0; the server keeps your character and gives it back at every join
  - crossplay players no longer get stuck at login
  - logging out waits until the server confirms your character is saved, with a "Saving your character on the server..." message
  - quitting (the Quit button, closing the window, Alt+F4) now logs out and saves first
  - character backups are written safely: a crash mid-save can't damage them, and the previous backup is kept
  - the server no longer stalls when a player logs out, and logging in and out is smoother on busy servers
  - logging in no longer freezes while your plugins are checked
  - a brand-new character whose template skips the intro gets its controls at once instead of waiting in the intro
  - new server settings for how character saves travel over crossplay and how long logout waits
  - VAngarde's help is in the in-game Fires help panel
  - console text and banner fixes, and server log lines no longer print twice

* v0.2.16 - updated for Valheim 1.0

* v0.2.14 - maintenance and fixes
