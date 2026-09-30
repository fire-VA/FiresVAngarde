// Shared FUC (FiresCore) types used across FiresVAngarde. Trimmed to the infrastructure set
// during the Phase-1 strip; the VAngarde anti-cheat port (Phase 2) re-introduces any additional
// shared types it needs (ClientLogRelay, MainThreadDispatcher usages, etc.).
global using FiresCore.Async;
global using AdminSyncing = FiresCore.Sync.AdminSyncing;
global using SafeRoutedRpc = FiresCore.Net.SafeRoutedRpc;
