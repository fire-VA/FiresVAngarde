using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace VerdantsAscent.Modules.Characters
{
    /// <summary>
    /// New-character bootstrap. When the server has no stored copy AND the local profile has never played a world
    /// (a brand-new character), <see cref="CharactersClient"/> sets <see cref="PendingApply"/>; the next
    /// <see cref="Player.OnSpawned"/> applies the configured template:
    /// starter skills, starter inventory, optional spawn override, and optional Valkyrie-intro skip.
    ///
    /// Template lives at <c>BepInEx/config/FiresVAngarde/PlayerTemplate.json</c>. Schema (all fields
    /// optional):
    /// <code>
    /// {
    ///   "skills": { "Run": 10, "Swim": 10 },
    ///   "items":  { "Wood": 20, "Stone": 5 },
    ///   "spawn":  [ { "x": 0, "y": 30, "z": 0 } ],
    ///   "skipIntro": true
    /// }
    /// </code>
    /// </summary>
    public static class CharactersPlayerTemplate
    {
        public static string TemplatePath =>
            Path.Combine(Paths.ConfigPath, "VAngarde", "PlayerTemplate.json");

        public class TemplateDef
        {
            public Dictionary<string, float> skills { get; set; } = new();
            public Dictionary<string, int> items { get; set; } = new();
            public List<Position> spawn { get; set; } = new();
            public bool skipIntro { get; set; } = false;
        }
        public class Position { public float x, y, z; }

        private static TemplateDef _template;
        public static bool PendingApply { get; private set; }

        public static void Mark() => PendingApply = true;
        public static void Clear() => PendingApply = false;

        public static TemplateDef Load()
        {
            if (_template != null) return _template;

            try
            {
                string dir = Path.GetDirectoryName(TemplatePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (!File.Exists(TemplatePath))
                {
                    var sample = new TemplateDef
                    {
                        skills = new Dictionary<string, float> { ["Run"] = 0, ["Swim"] = 0 },
                        items = new Dictionary<string, int>(),
                        spawn = new List<Position>(),
                        skipIntro = false
                    };
                    File.WriteAllText(TemplatePath, JsonConvert.SerializeObject(sample, Formatting.Indented));
                    Debug.Log($"[Characters] template: wrote sample to {TemplatePath}");
                    return _template = sample;
                }

                string json = File.ReadAllText(TemplatePath);
                _template = JsonConvert.DeserializeObject<TemplateDef>(json) ?? new TemplateDef();
                Debug.Log($"[Characters] template: loaded ({_template.skills.Count} skills, {_template.items.Count} items, " +
                          $"{_template.spawn.Count} spawn points, skipIntro={_template.skipIntro}).");
                return _template;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Characters] template: load failed — using empty: {ex.Message}");
                return _template = new TemplateDef();
            }
        }

        public static void ApplyTo(Player player)
        {
            if (player == null) return;
            var t = Load();

            try
            {
                if (player.m_skills != null)
                    foreach (var kv in t.skills)
                        if (Enum.TryParse(kv.Key, true, out Skills.SkillType skill))
                            player.m_skills.CheatRaiseSkill(skill.ToString(), kv.Value);

                if (player.m_inventory != null && ObjectDB.instance != null)
                    foreach (var kv in t.items)
                    {
                        var prefab = ObjectDB.instance.GetItemPrefab(kv.Key);
                        if (prefab == null) { Debug.LogWarning($"[Characters] template: unknown item '{kv.Key}'"); continue; }
                        player.m_inventory.AddItem(prefab, Mathf.Max(1, kv.Value));
                    }

                if (t.spawn != null && t.spawn.Count > 0)
                {
                    var p = t.spawn[UnityEngine.Random.Range(0, t.spawn.Count)];
                    player.TeleportTo(new Vector3(p.x, p.y, p.z), Quaternion.identity, distantTeleport: true);
                }

                Debug.Log($"[Characters] template: applied to '{player.GetPlayerName()}'.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Characters] template: apply failed: {ex}");
            }
        }
    }

    /// <summary>
    /// Apply the template on the FIRST <see cref="Player.OnSpawned"/> after an empty-profile login.
    /// Skip Valkyrie intro for new server-managed characters when the template asks.
    /// </summary>
    [HarmonyPatch]
    internal static class CharactersPlayerTemplatePatches
    {
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        [HarmonyPostfix]
        private static void Player_OnSpawned_Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer || !CharactersPlayerTemplate.PendingApply) return;
            CharactersPlayerTemplate.ApplyTo(__instance);
            CharactersPlayerTemplate.Clear();
        }

        [HarmonyPatch(typeof(Valkyrie), nameof(Valkyrie.Awake))]
        [HarmonyPrefix]
        private static bool Valkyrie_Awake_Prefix(Valkyrie __instance)
        {
            if (!CharactersClient.IsServerCharacter) return true;
            if (!CharactersPlayerTemplate.Load().skipIntro) return true;
            UnityEngine.Object.Destroy(__instance.gameObject);
            // Player.OnSpawned set the intro flag before spawning the Valkyrie, and only the Valkyrie clears it; without this the
            // character stayed "in the intro" for good (R47: the bot's new Autoplay02 never got its controls).
            Player.m_localPlayer?.SetIntro(false);
            return false;
        }
    }
}
