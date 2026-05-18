using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using NuclearOption.Networking;

namespace SpawnControl
{
    [BepInPlugin("com.spawncontrol", "SpawnControl", "1.0.0")]
    public class SpawnControlPlugin : BaseUnityPlugin
    {
        public static SpawnControlPlugin Instance;
        public static ManualLogSource Log;

        // Global Configuration Overrides
        public static ConfigEntry<bool> AllowAllEverywhere;
        public static ConfigEntry<bool> AllowShipsToSpawnAll;
        public static ConfigEntry<bool> AllowLandBasesToSpawnAll;
        public static ConfigEntry<bool> AllowAllVTOLsOnHelipads;

        // Metadata structures for granular configs
        public class ShipHangarInfo
        {
            public string HangarType; 
            public string ConfigName; 
        }

        // Temporary structure used during config generation to flatten and sort all locations
        public class GlobalHangarInfo
        {
            public string UnitName;
            public string RelativePath;
            public string DisplayName;
            public AircraftDefinition[] NativeAllowed;
            public int UnitSortOrder;
            public int HangarIndex;
        }

        // Maps internal prefab names (e.g. "helipad1") to the primary dictionary key ("Helipad")
        public static Dictionary<string, string> UnitNameAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Key: Unit Name -> Transform Path -> Metadata Info
        public static Dictionary<string, Dictionary<string, ShipHangarInfo>> HangarMetadataByPath = 
            new Dictionary<string, Dictionary<string, ShipHangarInfo>>();

        // Unified Config Dictionaries:
        // Key 1: Unit Name (e.g. "Large Hangar", "Fleet Carrier")
        // Key 2: Hangar Relative Path (hierarchy tree from ship/building root)
        // Key 3: Aircraft Definition UnitName (e.g. "CI-22 Cricket")
        public static Dictionary<string, Dictionary<string, Dictionary<string, ConfigEntry<bool>>>> UnitHangarConfigs = 
            new Dictionary<string, Dictionary<string, Dictionary<string, ConfigEntry<bool>>>>();

        // Caches VTOL reflection checks by Aircraft unitName for maximum performance
        public static Dictionary<string, bool> VTOLCache = new Dictionary<string, bool>();

        void Awake()
        {
            Instance = this;
            Log = Logger;
            Log.LogInfo("Spawn Control Mod initializing...");

            // Bind Global Controls with Explicit Order
            AllowAllEverywhere = Config.Bind("0. Global Overrides", "Allow All Aircraft Everywhere", false, 
                new ConfigDescription("If enabled, completely bypasses all restrictions.", null, 
                new ConfigurationManagerAttributes { Order = 4 }));
            
            AllowShipsToSpawnAll = Config.Bind("0. Global Overrides", "Allow Ships to Spawn All", false, 
                new ConfigDescription("If enabled, ship-based elevators, hangars, and helipads can spawn all aircraft types.", null, 
                new ConfigurationManagerAttributes { Order = 3 }));

            AllowLandBasesToSpawnAll = Config.Bind("0. Global Overrides", "Allow Land Bases to Spawn All", false, 
                new ConfigDescription("If enabled, land bases can spawn any aircraft on all standard hangars and helipads.", null, 
                new ConfigurationManagerAttributes { Order = 2 }));

            AllowAllVTOLsOnHelipads = Config.Bind("0. Global Overrides", "Allow All VTOLs on Helipads", true, 
                new ConfigDescription("If enabled, allows all VTOL/helicopter aircraft to spawn on any land or ship helipad.", null, 
                new ConfigurationManagerAttributes { Order = 1 }));

            // Apply Harmony Patches
            try
            {
                var harmony = new Harmony("com.spawncontrol");
                harmony.PatchAll(typeof(SpawnControlPlugin));
                Log.LogInfo("Spawn Control Harmony patches applied successfully.");
            }
            catch (Exception ex)
            {
                Log.LogError($"Spawn Control failed to apply Harmony patches: {ex}");
            }

            // Start the asynchronous loading scan coroutine
            StartCoroutine(InitSpawnControlConfigs());
        }

        private IEnumerator InitSpawnControlConfigs()
        {
            Log.LogInfo("SpawnControl: Waiting for all definitions to load...");
            UnitDefinition[] allUnits = null;
            AircraftDefinition[] allAircraft = null;

            // Wait until both units and aircraft are populated in memory
            while (true)
            {
                allUnits = Resources.FindObjectsOfTypeAll<UnitDefinition>();
                allAircraft = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
                
                if (allUnits != null && allUnits.Length > 0 && allAircraft != null && allAircraft.Length > 0)
                    break;
                    
                yield return new WaitForSeconds(2.0f);
            }

            Log.LogInfo("SpawnControl: Definitions loaded. Building deterministic location-based configuration...");

            // 1. GATHER AND SORT ALL AIRCRAFT (These will become the Main Categories)
            var sortedAircraft = allAircraft
                .Where(a => a != null && !string.IsNullOrEmpty(a.unitName))
                .GroupBy(a => a.unitName).Select(g => g.First()) 
                .OrderBy(a => a.unitName.Contains("???") ? 1 : 0) // Push UFO to bottom visually
                .ThenBy(a => a.unitName) 
                .ToList();

            // 2. GATHER ALL HANGARS/LOCATIONS (These will become the Sublist Toggles)
            var validUnits = allUnits
                .Where(def => def != null && def.unitPrefab != null && !string.IsNullOrEmpty(def.unitName))
                .Where(def => def.unitPrefab.GetComponentsInChildren<Hangar>(true).Length > 0)
                .ToList();

            // Sort Ships dynamically by Mass (descending) to logically group Heavy Carriers > Destroyers > Patrol Ships
            var ships = validUnits
                .Where(def => def is ShipDefinition || def.unitPrefab.GetComponentInChildren<Ship>(true) != null)
                .OrderByDescending(def => def.mass)
                .ThenBy(def => def.unitName)
                .ToList();

            // Sort Ground structures alphabetically
            var grounds = validUnits
                .Where(def => !(def is ShipDefinition) && def.unitPrefab.GetComponentInChildren<Ship>(true) == null)
                .OrderBy(def => def.unitName)
                .ToList();

            List<GlobalHangarInfo> allLocations = new List<GlobalHangarInfo>();

            // Local method to process dynamic categories cleanly
            void ProcessUnitCategory(List<UnitDefinition> unitList, string categoryName, int baseSortOrder)
            {
                for (int uIndex = 0; uIndex < unitList.Count; uIndex++)
                {
                    var def = unitList[uIndex];
                    string unitName = def.unitName;
                    if (string.IsNullOrEmpty(unitName)) unitName = def.name;

                    // Register robust aliases for runtime mapping to handle map-placed clones perfectly
                    if (!string.IsNullOrEmpty(def.name)) UnitNameAliases[def.name] = unitName;
                    if (!string.IsNullOrEmpty(def.unitName)) UnitNameAliases[def.unitName] = unitName;
                    
                    string sanitizedName = SanitizeGameObjectName(def.name);
                    if (!string.IsNullOrEmpty(sanitizedName)) UnitNameAliases[sanitizedName] = unitName;
                    
                    string sanitizedUnitName = SanitizeGameObjectName(def.unitName);
                    if (!string.IsNullOrEmpty(sanitizedUnitName)) UnitNameAliases[sanitizedUnitName] = unitName;

                    var airbase = def.unitPrefab.GetComponentInChildren<Airbase>(true);
                    List<Hangar> baseHangars = null;
                    
                    // Attempt native game sort if an Airbase controller is present
                    if (airbase != null)
                    {
                        try
                        {
                            var findMethod = typeof(Airbase).GetMethod("FindAttachedHangars", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (findMethod != null) findMethod.Invoke(airbase, null);

                            var sortMethod = typeof(Airbase).GetMethod("SortHangarsByPriority", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (sortMethod != null) sortMethod.Invoke(airbase, null);

                            var hangarsProperty = typeof(Airbase).GetProperty("hangars", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (hangarsProperty != null) baseHangars = hangarsProperty.GetValue(airbase) as List<Hangar>;
                        }
                        catch (Exception ex)
                        {
                            Log.LogWarning($"SpawnControl: Failed to sort prefab airbase hangars for '{unitName}': {ex.Message}");
                        }
                    }

                    // Fallback to strict component gather (Crucial for Ground Structures)
                    if (baseHangars == null || baseHangars.Count == 0)
                    {
                        baseHangars = def.unitPrefab.GetComponentsInChildren<Hangar>(true).ToList();
                    }

                    if (baseHangars.Count == 0) continue; // Final safety net

                    int unitSortOrder = baseSortOrder + uIndex;
                    string categoryPrefix = $"{categoryName} - {uIndex + 1:D2}. ";

                    var elevatorField = typeof(Hangar).GetField("elevator", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var doorsField = typeof(Hangar).GetField("doors", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var nativeListField = typeof(Hangar).GetField("availableAircraft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    int elevatorCount = 0;
                    int helipadCount = 0;
                    int deckspawnCount = 0;
                    int hangarCount = 0;
                    
                    if (!HangarMetadataByPath.ContainsKey(unitName))
                        HangarMetadataByPath[unitName] = new Dictionary<string, ShipHangarInfo>();

                    for (int i = 0; i < baseHangars.Count; i++)
                    {
                        var hangar = baseHangars[i];
                        if (hangar == null) continue;

                        bool isElevator = elevatorField != null && (bool)elevatorField.GetValue(hangar);
                        string name = hangar.name.ToLower();
                        string goName = hangar.gameObject.name.ToLower();
                        bool isHelipad = name.Contains("helipad") || name.Contains("helo") || name.Contains("pad") ||
                                         goName.Contains("helipad") || goName.Contains("helo") || goName.Contains("pad");

                        string hangarType = "";
                        string configName = "";

                        if (isElevator)
                        {
                            elevatorCount++;
                            hangarType = "elevator";
                            configName = $"elevator_{elevatorCount}";
                        }
                        else if (isHelipad)
                        {
                            helipadCount++;
                            hangarType = "helipad";
                            configName = $"helipad_{helipadCount}";
                        }
                        else
                        {
                            bool hasDoors = doorsField != null && (doorsField.GetValue(hangar) as Array)?.Length > 0;

                            if (!hasDoors)
                            {
                                deckspawnCount++;
                                hangarType = "deckspawn";
                                configName = $"deckspawn_{deckspawnCount}";
                            }
                            else
                            {
                                hangarCount++;
                                hangarType = "hangar";
                                configName = $"hangar_{hangarCount}";
                            }
                        }

                        string relativePath = GetRelativePath(hangar.transform, def.unitPrefab.transform);
                        var info = new ShipHangarInfo { HangarType = hangarType, ConfigName = configName };
                        HangarMetadataByPath[unitName][relativePath] = info;

                        AircraftDefinition[] nativeAllowed = null;
                        if (nativeListField != null)
                        {
                            nativeAllowed = nativeListField.GetValue(hangar) as AircraftDefinition[];
                        }

                        // Log this location to be flat-mapped against every aircraft later
                        allLocations.Add(new GlobalHangarInfo {
                            UnitName = unitName,
                            RelativePath = relativePath,
                            DisplayName = $"{categoryPrefix}{unitName} - {i + 1:D2}. {configName}",
                            NativeAllowed = nativeAllowed,
                            UnitSortOrder = unitSortOrder,
                            HangarIndex = i
                        });
                    }
                }
            }

            // Process Ground structures first (Order series 100), then Ships (Order series 200)
            ProcessUnitCategory(grounds, "1. Ground", 100);
            ProcessUnitCategory(ships, "2. Ship", 200);

            // Order all locations precisely so the UI toggles show Ground -> Carriers -> Destroyers -> Elevators
            var sortedLocations = allLocations.OrderBy(h => h.UnitSortOrder).ThenBy(h => h.HangarIndex).ToList();

            // 3. INVERTED CONFIG BINDING: Aircraft are the Main Categories, Locations are the Sublist
            int acIndex = 1;
            foreach (var ac in sortedAircraft)
            {
                string acKey = ac.unitName;
                
                // Prefix with 1. Aircraft or 9. UFO to force them below 0. Global Overrides
                string sectionName = acKey.Contains("???") ? $"9. UFO - {acKey}" : $"1. Aircraft - {acIndex:D2}. {acKey}";

                int locationOrderCounter = sortedLocations.Count; // High number = renders at the top of the category

                foreach (var location in sortedLocations)
                {
                    bool defaultAllowed = location.NativeAllowed != null && location.NativeAllowed.Any(nativeAc => nativeAc != null && nativeAc.unitName == acKey);

                    string keyName = SanitizeConfigKey(location.DisplayName);

                    var configEntry = Instance.Config.Bind(
                        sectionName, 
                        keyName, 
                        defaultAllowed, 
                        new ConfigDescription($"Allow {acKey} to spawn at {location.DisplayName}.", null, 
                        new ConfigurationManagerAttributes { DispName = keyName, Order = locationOrderCounter })
                    );

                    locationOrderCounter--; // Decrement ensures perfectly grouped display for hangars

                    // Insert deep into our ultra-fast runtime lookup dictionary
                    if (!UnitHangarConfigs.ContainsKey(location.UnitName))
                        UnitHangarConfigs[location.UnitName] = new Dictionary<string, Dictionary<string, ConfigEntry<bool>>>();
                    
                    if (!UnitHangarConfigs[location.UnitName].ContainsKey(location.RelativePath))
                        UnitHangarConfigs[location.UnitName][location.RelativePath] = new Dictionary<string, ConfigEntry<bool>>();
                        
                    UnitHangarConfigs[location.UnitName][location.RelativePath][acKey] = configEntry;
                }
                acIndex++;
            }

            Log.LogInfo($"SpawnControl: Configuration binding workflow complete. Bound {sortedAircraft.Count} aircraft against {sortedLocations.Count} locations.");
        }

        public static string GetRelativePath(Transform t, Transform root)
        {
            if (t == null || root == null) return "";
            if (t == root) return t.name;

            // Dropping sibling index entirely for a pure name-path string. 
            // This guarantees modded maps or runtime injection don't break the configuration matching!
            string path = t.name;
            while (t.parent != null && t.parent != root)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        public static string SanitizeGameObjectName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            // Strip any instantiated suffixes like "(Clone)" or " (1)" to perfectly match definitions
            int idx1 = n.IndexOf(" (");
            if (idx1 > 0) return n.Substring(0, idx1).Trim();
            int idx2 = n.IndexOf("(");
            if (idx2 > 0) return n.Substring(0, idx2).Trim();
            return n.Trim();
        }

        private static bool IsVTOL(AircraftDefinition def)
        {
            if (def == null) return false;
            string acKey = def.unitName;
            if (string.IsNullOrEmpty(acKey)) acKey = def.name;

            if (VTOLCache.TryGetValue(acKey, out bool isVtol))
                return isVtol;

            bool result = false;
            
            if (def.unitPrefab != null)
            {
                var ac = def.unitPrefab.GetComponent<Aircraft>();
                if (ac != null)
                {
                    var ap = ac.GetComponent<Autopilot>() ?? ac.autopilot;
                    if (ap != null)
                    {
                        Type apType = ap.GetType();
                        try
                        {
                            var hoverField = apType.GetField("hoverController", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (hoverField == null && apType.BaseType != null)
                            {
                                hoverField = apType.BaseType.GetField("hoverController", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            }

                            if (hoverField != null)
                            {
                                var hoverVal = hoverField.GetValue(ap);
                                if (hoverVal != null) result = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.LogWarning($"SpawnControl: Failed to reflect Autopilot for '{acKey}': {ex.Message}");
                        }
                    }
                }
            }

            if (!result)
            {
                string lower = def.name.ToLower();
                string unitNameLower = acKey.ToLower();
                if (lower.Contains("chicane") || lower.Contains("ibis") || lower.Contains("tarantula") || lower.Contains("medusa") || lower.Contains("vortex") ||
                    unitNameLower.Contains("chicane") || unitNameLower.Contains("ibis") || unitNameLower.Contains("tarantula") || unitNameLower.Contains("medusa") || unitNameLower.Contains("vortex"))
                {
                    result = true;
                }
            }

            VTOLCache[acKey] = result;
            return result;
        }

        public static string SanitizeConfigKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Unknown";
            s = s.Replace("[", "(").Replace("]", ")").Replace("=", "-").Replace("\\", "/")
                 .Replace("'", "").Replace("\"", "").Replace("\n", " ").Replace("\t", " ");
            return s.Trim();
        }

        /// <summary>
        /// Highly resilient engine that maps mangled runtime map objects to their pristine Configuration Dictionary Key.
        /// Performs strict hierarchy climbs and fuzzy substring alias matches.
        /// </summary>
        public static bool ResolveHangarConfigContext(Hangar hangar, out string resolvedUnitName, out Transform resolvedRootTransform)
        {
            resolvedUnitName = "";
            resolvedRootTransform = null;

            // 1. Direct Attachment Check (Ships & Dedicated Bases)
            Unit attachedUnit = hangar.attachedUnit ?? hangar.GetComponentInParent<Unit>();
            if (attachedUnit != null && attachedUnit.definition != null)
            {
                resolvedUnitName = attachedUnit.definition.unitName;
                if (string.IsNullOrEmpty(resolvedUnitName)) resolvedUnitName = attachedUnit.definition.name;
                resolvedRootTransform = attachedUnit.transform;
                
                if (UnitHangarConfigs.ContainsKey(resolvedUnitName)) return true;
            }

            Transform t = hangar.transform;
            Airbase parentAirbase = hangar.GetComponentInParent<Airbase>();
            Transform stopAt = parentAirbase != null ? parentAirbase.transform.parent : null;

            // 2. Strict & Sanitized Match against Prefab Aliases (Catches pure map-placed clones)
            while (t != null && t != stopAt)
            {
                string cleanName = SanitizeGameObjectName(t.gameObject.name);
                if (UnitNameAliases.TryGetValue(cleanName, out string mappedUnitName) && UnitHangarConfigs.ContainsKey(mappedUnitName))
                {
                    resolvedUnitName = mappedUnitName;
                    resolvedRootTransform = t;
                    return true;
                }
                t = t.parent;
            }

            // 3. Fuzzy Substring Match (Catches map objects that were entirely renamed by modders e.g. "K92_helipad1_south")
            t = hangar.transform;
            while (t != null && t != stopAt)
            {
                string rawName = t.gameObject.name.ToLower();
                
                // Sort aliases by length descending so "Medium Aircraft Hangar" matches before "Hangar" to prevent misattribution
                var sortedAliases = UnitNameAliases.Keys.OrderByDescending(k => k.Length).ToList();
                
                foreach (string aliasKey in sortedAliases)
                {
                    if (rawName.Contains(aliasKey.ToLower()) && UnitHangarConfigs.ContainsKey(UnitNameAliases[aliasKey]))
                    {
                        resolvedUnitName = UnitNameAliases[aliasKey];
                        resolvedRootTransform = t;
                        return true;
                    }
                }
                t = t.parent;
            }

            return false;
        }

        public static bool IsAircraftAllowed(Hangar hangar, AircraftDefinition definition)
        {
            if (hangar == null || definition == null) return false;
            if (AllowAllEverywhere.Value) return true;

            string acKey = definition.unitName;
            if (string.IsNullOrEmpty(acKey)) acKey = definition.name;

            bool isShip = false;
            Unit attachedUnit = hangar.attachedUnit ?? hangar.GetComponentInParent<Unit>();
            if (attachedUnit != null)
            {
                var shipComp = attachedUnit.GetComponent<Ship>() ?? attachedUnit.GetComponentInChildren<Ship>(true);
                isShip = shipComp != null || attachedUnit.gameObject.name.ToLower().Contains("carrier") || attachedUnit is Ship;
            }

            // 1. Resolve configuration context completely dynamically
            if (ResolveHangarConfigContext(hangar, out string unitName, out Transform configRootTransform))
            {
                // Process God-Mode Toggles FIRST if they apply
                if (isShip && AllowShipsToSpawnAll.Value) return true;
                if (!isShip && AllowLandBasesToSpawnAll.Value) return true;

                string relativePath = GetRelativePath(hangar.transform, configRootTransform);

                if (UnitHangarConfigs.TryGetValue(unitName, out var unitDict))
                {
                    // Strict match check
                    if (unitDict.TryGetValue(relativePath, out var hangarDict) && 
                        hangarDict.TryGetValue(acKey, out var configEntry))
                    {
                        return configEntry.Value;
                    }

                    // FUZZY MATCH 1: If map design injected extra folders, simply match by the direct node name (e.g. "pad")
                    string nodeName = hangar.gameObject.name;
                    foreach (var kvp in unitDict)
                    {
                        if (kvp.Key.EndsWith(nodeName, StringComparison.OrdinalIgnoreCase) || nodeName.EndsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            if (kvp.Value.TryGetValue(acKey, out var fuzzyEntry))
                            {
                                return fuzzyEntry.Value;
                            }
                        }
                    }

                    // FUZZY MATCH 2: Total failsafe. If this building only has exactly 1 spawn point total (like a Helipad or Revetment), 
                    // completely ignore the mangled relative path and just feed it the exact configuration toggled by the user.
                    if (unitDict.Count == 1)
                    {
                        foreach (var kvp in unitDict)
                        {
                            if (kvp.Value.TryGetValue(acKey, out var fuzzyEntry2))
                            {
                                return fuzzyEntry2.Value;
                            }
                        }
                    }
                }
            }

            // 2. Global VTOL Settings (Acts as a fallback if the config check was entirely skipped/unresolved)
            string hangarName = hangar.name.ToLower();
            string goName = hangar.gameObject.name.ToLower();
            bool isHelipad = hangarName.Contains("helipad") || hangarName.Contains("helo") || hangarName.Contains("pad") ||
                             goName.Contains("helipad") || goName.Contains("helo") || goName.Contains("pad");

            if (isHelipad && AllowAllVTOLsOnHelipads.Value && IsVTOL(definition))
            {
                return true;
            }

            // 3. Absolute Final Fallback: Vanilla Game Rules
            var origField = typeof(Hangar).GetField("availableAircraft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (origField != null)
            {
                var origList = origField.GetValue(hangar) as AircraftDefinition[];
                if (origList != null) return origList.Contains(definition);
            }

            return false;
        }

        public static AircraftDefinition[] GetAllowedAircraftForHangar(Hangar hangar)
        {
            if (hangar == null) return new AircraftDefinition[0];

            var allAircraft = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
            if (AllowAllEverywhere.Value) return allAircraft;

            List<AircraftDefinition> allowed = new List<AircraftDefinition>();
            foreach (var def in allAircraft)
            {
                if (def == null || string.IsNullOrEmpty(def.unitName)) continue;
                if (IsAircraftAllowed(hangar, def)) allowed.Add(def);
            }

            return allowed.ToArray();
        }

        // ==================== HARMONY PATCHES ====================

        [HarmonyPatch(typeof(Hangar), nameof(Hangar.CanSpawnAircraft))]
        [HarmonyPostfix]
        static void Hangar_CanSpawnAircraft_Postfix(Hangar __instance, AircraftDefinition definition, ref bool __result)
        {
            __result = IsAircraftAllowed(__instance, definition);
        }

        [HarmonyPatch(typeof(Hangar), nameof(Hangar.GetAvailableAircraft))]
        [HarmonyPostfix]
        static void Hangar_GetAvailableAircraft_Postfix(Hangar __instance, ref AircraftDefinition[] __result)
        {
            __result = GetAllowedAircraftForHangar(__instance);
        }

        [HarmonyPatch(typeof(Hangar), nameof(Hangar.TrySpawnAircraft))]
        [HarmonyPrefix]
        static void Hangar_TrySpawnAircraft_Prefix(Hangar __instance, Player player, AircraftDefinition definition)
        {
            string attachedName = "Land Base";
            string hangarLabel = __instance.name;

            if (ResolveHangarConfigContext(__instance, out string unitName, out Transform configRootTransform))
            {
                attachedName = unitName;
                string relativePath = GetRelativePath(__instance.transform, configRootTransform);

                if (HangarMetadataByPath.TryGetValue(unitName, out var pathDict))
                {
                    if (pathDict.TryGetValue(relativePath, out var info))
                    {
                        hangarLabel = info.ConfigName;
                    }
                    else if (pathDict.Count == 1) // Fuzzy fallback mapping for the logs
                    {
                        hangarLabel = pathDict.Values.First().ConfigName;
                    }
                }
            }

            string targetUnitName = definition?.unitName;
            if (string.IsNullOrEmpty(targetUnitName)) targetUnitName = definition?.name;

            Log.LogInfo($"SpawnControl: Spawn request by {player?.PlayerName ?? "Unknown"} for '{targetUnitName}' at {attachedName} ({hangarLabel})");
        }

        [HarmonyPatch(typeof(Airbase), nameof(Airbase.CanSpawnAircraft))]
        [HarmonyPostfix]
        static void Airbase_CanSpawnAircraft_Postfix(Airbase __instance, AircraftDefinition definition, ref bool __result)
        {
            var hangarsProperty = typeof(Airbase).GetProperty("hangars", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            List<Hangar> baseHangars = hangarsProperty?.GetValue(__instance) as List<Hangar>;

            if (baseHangars == null)
            {
                __result = false;
                return;
            }

            bool allowed = false;
            foreach (var hangar in baseHangars)
            {
                if (hangar != null && IsAircraftAllowed(hangar, definition))
                {
                    allowed = true;
                    break;
                }
            }
            __result = allowed;
        }

        [HarmonyPatch(typeof(Airbase), nameof(Airbase.GetAvailableAircraft))]
        [HarmonyPostfix]
        static void Airbase_GetAvailableAircraft_Postfix(Airbase __instance, ref List<AircraftDefinition> __result)
        {
            var hangarsProperty = typeof(Airbase).GetProperty("hangars", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            List<Hangar> baseHangars = hangarsProperty?.GetValue(__instance) as List<Hangar>;

            if (baseHangars == null)
            {
                __result = new List<AircraftDefinition>();
                return;
            }

            var allowedSet = new HashSet<AircraftDefinition>();
            foreach (var hangar in baseHangars)
            {
                if (hangar == null) continue;
                var allowed = GetAllowedAircraftForHangar(hangar);
                foreach (var ac in allowed)
                {
                    allowedSet.Add(ac);
                }
            }
            __result = allowedSet.ToList();
        }
    }
}