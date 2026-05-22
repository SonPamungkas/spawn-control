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
    [BepInPlugin("com.spawncontrol", "SpawnControl", "1.2")]
    public class SpawnControlPlugin : BaseUnityPlugin
    {
        public static SpawnControlPlugin Instance;
        public static ManualLogSource Log;

        // Global Configuration Overrides
        public static ConfigEntry<bool> ResetAllSettings;
        public static ConfigEntry<bool> DebugAuditorMode;
        public static ConfigEntry<bool> AllowAllEverywhere;
        public static ConfigEntry<bool> AllowShipsToSpawnAll;
        public static ConfigEntry<bool> AllowLandBasesToSpawnAll;
        public static ConfigEntry<bool> AllowAllVTOLsOnHelipads;

        // Mappings from prefab.txt
        public static readonly Dictionary<string, string> PrefabToDisplayName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "AttackHelo1", "SAH-46 Chicane" },
            { "SmallFighter1", "FS-20 Vortex" },
            { "FastBomber1", "Alkyon AB-4" },
            { "UFO", "???" },
            { "COIN", "CI-22 Cricket" },
            { "EW1", "EW-25 Medusa" },
            { "QuadVTOL1", "VL-49 Tarantula" },
            { "SFB", "SFB-81 Darkreach" },
            { "Trainer", "T/A-30 Compass" },
            { "UtilityHelo1", "UH-90 Ibis" },
            { "CAS1", "A-19 Brawler" },
            { "Fighter1", "FS-12 Revoker" },
            { "Multirole1", "KR-67 Ifrit" },
            { "Aryx_LightHelicopter1_Definition", "RAH-72 Knockout" },
            { "Aryx_MiG-15_AircraftDefinition", "MiG-15" },
            { "P_Trisurface1_definition", "FS-3 Ternion" },
            { "Aryx_F16M_KingViper_AircraftDefinition", "F-16M King Viper" },
            { "Aryx_MC260_Chimera_Definition", "MC-260 Chimera" },
            { "kestrel_definition", "FQ-106 Kestrel" }
        };

        // Startup scanned cache of natively allowed aircraft per hangar
        public static Dictionary<string, Dictionary<string, HashSet<string>>> NativeAllowedCache = 
            new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);

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
        // Key 3: Aircraft Prefab Name (e.g. "COIN")
        public static Dictionary<string, Dictionary<string, Dictionary<string, ConfigEntry<bool>>>> UnitHangarConfigs = 
            new Dictionary<string, Dictionary<string, Dictionary<string, ConfigEntry<bool>>>>();

        // Key: Aircraft Key (prefab name)
        // Value: ConfigEntry<int> (0=Both, 1=PALA Only, 2=BDF Only)
        public static Dictionary<string, ConfigEntry<int>> AircraftFactionRestrictions = 
            new Dictionary<string, ConfigEntry<int>>(StringComparer.OrdinalIgnoreCase);

        // Optimized compiled field accessors for high-performance zero-allocation hot paths
        private static readonly AccessTools.FieldRef<Hangar, AircraftDefinition[]> HangarAvailableAircraftRef =
            AccessTools.FieldRefAccess<Hangar, AircraftDefinition[]>("availableAircraft");

        private static readonly AccessTools.FieldRef<Airbase, List<Hangar>> AirbaseHangarsRef =
            AccessTools.FieldRefAccess<Airbase, List<Hangar>>("<hangars>k__BackingField");

        private static readonly AccessTools.FieldRef<Airbase, List<AircraftDefinition>> AirbaseAvailableAircraftRef =
            AccessTools.FieldRefAccess<Airbase, List<AircraftDefinition>>("availableAircraft");

        // Caches VTOL reflection checks by Aircraft prefab name for maximum performance
        public static Dictionary<string, bool> VTOLCache = new Dictionary<string, bool>();

        public static HashSet<Hangar> CachedHangars = new HashSet<Hangar>();
        private static List<AircraftDefinition> allAircraft = new List<AircraftDefinition>();

        void Awake()
        {
            Instance = this;
            Log = Logger;
            Log.LogInfo("Spawn Control Mod initializing...");

            // Bind Global Controls with Explicit Order
            ResetAllSettings = Config.Bind("0. Global Overrides", "Reset All to Default", false,
                new ConfigDescription("Clicking this button resets all configuration options below to their default values.", null,
                new ConfigurationManagerAttributes 
                { 
                    Order = 10, 
                    CustomDrawer = DrawResetButton,
                    HideSettingName = true,
                    HideDefaultButton = true
                }));

            DebugAuditorMode = Config.Bind("0. Global Overrides", "Debug Auditor Mode Only", false, 
                new ConfigDescription("If enabled, restricts spawning strictly to vanilla/baked predesignated locations.", null, 
                new ConfigurationManagerAttributes { Order = 5 }));

            AllowAllEverywhere = Config.Bind("0. Global Overrides", "Allow All Aircraft Everywhere", false, 
                new ConfigDescription("If enabled, completely bypasses all restrictions.", null, 
                new ConfigurationManagerAttributes { Order = 4 }));
            
            AllowShipsToSpawnAll = Config.Bind("0. Global Overrides", "Allow Ships to Spawn All", false, 
                new ConfigDescription("If enabled, ship-based elevators, hangars, and helipads can spawn all aircraft types.", null, 
                new ConfigurationManagerAttributes { Order = 3 }));

            AllowLandBasesToSpawnAll = Config.Bind("0. Global Overrides", "Allow Land Bases to Spawn All", false, 
                new ConfigDescription("If enabled, land bases can spawn any aircraft on all standard hangars and helipads.", null, 
                new ConfigurationManagerAttributes { Order = 2 }));

            AllowAllVTOLsOnHelipads = Config.Bind("0. Global Overrides", "Allow All VTOLs on Helipads", false, 
                new ConfigDescription("If enabled, allows all VTOL/helicopter aircraft to spawn on any land or ship helipad.", null, 
                new ConfigurationManagerAttributes { Order = 1 }));

            // Apply Harmony patches
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

            StartCoroutine(InitSpawnControlConfigs());
        }

        static void DrawResetButton(BepInEx.Configuration.ConfigEntryBase entry)
        {
            if (GUILayout.Button("Reset All to Default", GUILayout.ExpandWidth(true)))
            {
                ResetAllToDefaults();
            }
        }

        public static void ResetAllToDefaults()
        {
            Log.LogInfo("Resetting all configurations to defaults...");

            // 1. Reset Global Overrides
            if (DebugAuditorMode != null) DebugAuditorMode.Value = (bool)DebugAuditorMode.DefaultValue;
            if (AllowAllEverywhere != null) AllowAllEverywhere.Value = (bool)AllowAllEverywhere.DefaultValue;
            if (AllowShipsToSpawnAll != null) AllowShipsToSpawnAll.Value = (bool)AllowShipsToSpawnAll.DefaultValue;
            if (AllowLandBasesToSpawnAll != null) AllowLandBasesToSpawnAll.Value = (bool)AllowLandBasesToSpawnAll.DefaultValue;
            if (AllowAllVTOLsOnHelipads != null) AllowAllVTOLsOnHelipads.Value = (bool)AllowAllVTOLsOnHelipads.DefaultValue;

            // 2. Reset Aircraft Hangar Configurations
            if (UnitHangarConfigs != null)
            {
                foreach (var unitKvp in UnitHangarConfigs.Values)
                {
                    if (unitKvp == null) continue;
                    foreach (var pathKvp in unitKvp.Values)
                    {
                        if (pathKvp == null) continue;
                        foreach (var configKvp in pathKvp.Values)
                        {
                            if (configKvp != null)
                            {
                                configKvp.Value = (bool)configKvp.DefaultValue;
                            }
                        }
                    }
                }
            }

            // 3. Reset Faction Restrictions
            if (AircraftFactionRestrictions != null)
            {
                foreach (var factionEntry in AircraftFactionRestrictions.Values)
                {
                    if (factionEntry != null)
                    {
                        factionEntry.Value = (int)factionEntry.DefaultValue;
                    }
                }
            }

            // 4. Save the config file
            if (Instance != null && Instance.Config != null)
            {
                Instance.Config.Save();
            }
            Log.LogInfo("All configuration settings have been reset to defaults.");
        }

        private IEnumerator InitSpawnControlConfigs()
        {
            Log.LogInfo("SpawnControl: Waiting for Blueprinter assets to load and stabilize...");

            // 1. Wait until both AircraftDefinition and UnitDefinition counts stabilize (non-zero and unchanging) with hard timeout
            UnitDefinition[] allUnits = null;
            AircraftDefinition[] allAircraftList = null;

            int stableCount = 0;
            int lastUnitsCount = 0;
            int lastAircraftCount = 0;
            int waitSeconds = 0;

            while (waitSeconds < 15) // 15 seconds safety timeout to prevent infinite hang at startup
            {
                allUnits = Resources.FindObjectsOfTypeAll<UnitDefinition>();
                allAircraftList = Resources.FindObjectsOfTypeAll<AircraftDefinition>();

                int uCount = allUnits != null ? allUnits.Length : 0;
                int aCount = allAircraftList != null ? allAircraftList.Length : 0;

                if (uCount > 0 && aCount > 0)
                {
                    if (uCount == lastUnitsCount && aCount == lastAircraftCount)
                    {
                        stableCount++;
                        if (stableCount >= 3) // Stable for 3 seconds
                        {
                            break;
                        }
                    }
                    else
                    {
                        lastUnitsCount = uCount;
                        lastAircraftCount = aCount;
                        stableCount = 0;
                    }
                }
                else
                {
                    stableCount = 0;
                }

                waitSeconds++;
                yield return new WaitForSeconds(1.0f);
            }

            Log.LogInfo($"SpawnControl: Stable at {lastAircraftCount} aircraft and {lastUnitsCount} units. Pre-generating hangar options...");

            allUnits = Resources.FindObjectsOfTypeAll<UnitDefinition>();
            allAircraftList = Resources.FindObjectsOfTypeAll<AircraftDefinition>();

            // 1. POPULATE GLOBAL NATIVE ALLOWED CACHE DIRECTLY FROM PREFABS
            var validUnits = allUnits
                .Where(def => def != null && def.unitPrefab != null && !string.IsNullOrEmpty(def.unitName))
                .Where(def => def.unitPrefab.GetComponentsInChildren<Hangar>(true).Length > 0)
                .ToList();

            var nativeListField = typeof(Hangar).GetField("availableAircraft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            foreach (var def in validUnits)
            {
                string unitName = GetUnitDisplayName(string.IsNullOrEmpty(def.unitName) ? def.name : def.unitName);

                var baseHangars = def.unitPrefab.GetComponentsInChildren<Hangar>(true).ToList();
                if (baseHangars.Count == 0) continue;

                if (!NativeAllowedCache.ContainsKey(unitName))
                    NativeAllowedCache[unitName] = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < baseHangars.Count; i++)
                {
                    var hangar = baseHangars[i];
                    if (hangar == null) continue;

                    string relativePath = GetRelativePath(hangar.transform, def.unitPrefab.transform);
                    
                    AircraftDefinition[] nativeAllowed = null;
                    if (nativeListField != null)
                    {
                        nativeAllowed = nativeListField.GetValue(hangar) as AircraftDefinition[];
                    }

                    var allowedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (nativeAllowed != null)
                    {
                        foreach (var ac in nativeAllowed)
                        {
                            if (ac != null && !string.IsNullOrEmpty(ac.name))
                            {
                                allowedSet.Add(ac.name);
                            }
                        }
                    }

                    NativeAllowedCache[unitName][relativePath] = allowedSet;
                }
            }

            // 2. GATHER AND SORT ALL AIRCRAFT (These will become the Main Categories)
            // Rely entirely on prefab name (ac.name)
            var sortedAircraft = allAircraftList
                .Where(a => a != null && !string.IsNullOrEmpty(a.name))
                .GroupBy(a => a.name).Select(g => g.First()) 
                .OrderBy(a => a.name.Contains("UFO") || a.name.Contains("???") ? 1 : 0) // Push UFO to bottom visually
                .ThenBy(a => a.name) 
                .ToList();

            // Sort Ships dynamically by Mass (descending) to logically group Heavy Carriers > Destroyers > Patrol Ships
            var ships = validUnits
                .Where(def => def is ShipDefinition || 
                              def.unitPrefab.GetComponentInChildren<Ship>(true) != null ||
                              def.unitName.ToLower().Contains("carrier") || 
                              def.unitName.ToLower().Contains("destroyer") || 
                              def.unitName.ToLower().Contains("frigate") || 
                              def.unitName.ToLower().Contains("corvette") || 
                              def.unitName.ToLower().Contains("cutter") || 
                              def.unitName.ToLower().Contains("cruiser") || 
                              def.unitName.ToLower().Contains("supply ship") ||
                              def.unitName.ToLower().Contains("supplyship") ||
                              def.name.ToLower().Contains("carrier") || 
                              def.name.ToLower().Contains("destroyer") || 
                              def.name.ToLower().Contains("frigate") || 
                              def.name.ToLower().Contains("corvette") || 
                              def.name.ToLower().Contains("cutter") || 
                              def.name.ToLower().Contains("cruiser") || 
                              def.name.ToLower().Contains("supply ship") ||
                              def.name.ToLower().Contains("supplyship"))
                .OrderByDescending(def => def.mass)
                .ThenBy(def => def.unitName)
                .ToList();

            // Sort Ground structures alphabetically
            var grounds = validUnits
                .Where(def => !ships.Contains(def))
                .OrderBy(def => def.unitName)
                .ToList();

            List<GlobalHangarInfo> allLocations = new List<GlobalHangarInfo>();

            // Local method to process dynamic categories cleanly
            void ProcessUnitCategory(List<UnitDefinition> unitList, string categoryName, int baseSortOrder)
            {
                for (int uIndex = 0; uIndex < unitList.Count; uIndex++)
                {
                    var def = unitList[uIndex];
                    string unitName = GetUnitDisplayName(string.IsNullOrEmpty(def.unitName) ? def.name : def.unitName);

                    // Register robust aliases for runtime mapping to handle map-placed clones perfectly
                    if (!string.IsNullOrEmpty(def.name)) UnitNameAliases[def.name] = unitName;
                    if (!string.IsNullOrEmpty(def.unitName)) UnitNameAliases[def.unitName] = unitName;
                    
                    string sanitizedName = SanitizeGameObjectName(def.name);
                    if (!string.IsNullOrEmpty(sanitizedName)) UnitNameAliases[sanitizedName] = unitName;
                    
                    string sanitizedUnitName = SanitizeGameObjectName(def.unitName);
                    if (!string.IsNullOrEmpty(sanitizedUnitName)) UnitNameAliases[sanitizedUnitName] = unitName;

                    var airbase = def.unitPrefab.GetComponentInChildren<Airbase>(true);
                    List<Hangar> baseHangars = def.unitPrefab.GetComponentsInChildren<Hangar>(true).ToList();

                    if (baseHangars.Count == 0) continue; // Final safety net

                    int unitSortOrder = baseSortOrder + uIndex;
                    string categoryPrefix = $"{categoryName} - {uIndex + 1:D2}. ";

                    var elevatorField = typeof(Hangar).GetField("elevator", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var doorsField = typeof(Hangar).GetField("doors", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

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
                        bool isHelipad = name.Contains("helipad") || goName.Contains("helipad");

                        string relativePath = GetRelativePath(hangar.transform, def.unitPrefab.transform);

                        // Look up friendly naval spawn description
                        string friendlyDesc = null;
                        if (NavalFriendlyDescriptions.TryGetValue(unitName, out var pathMap) &&
                            pathMap.TryGetValue(relativePath, out string desc))
                        {
                            friendlyDesc = desc;
                        }

                        string hangarType = "";
                        string configName = "";

                        if (isElevator)
                        {
                            elevatorCount++;
                            hangarType = "elevator";
                            configName = friendlyDesc ?? $"elevator_{elevatorCount}";
                        }
                        else if (isHelipad)
                        {
                            helipadCount++;
                            hangarType = "helipad";
                            configName = friendlyDesc ?? $"helipad_{helipadCount}";
                        }
                        else
                        {
                            bool hasDoors = doorsField != null && (doorsField.GetValue(hangar) as Array)?.Length > 0;

                            if (!hasDoors)
                            {
                                deckspawnCount++;
                                hangarType = "deckspawn";
                                configName = friendlyDesc ?? $"deckspawn_{deckspawnCount}";
                            }
                            else
                            {
                                hangarCount++;
                                hangarType = "hangar";
                                configName = friendlyDesc ?? $"hangar_{hangarCount}";
                            }
                        }

                        var info = new ShipHangarInfo { HangarType = hangarType, ConfigName = configName };
                        HangarMetadataByPath[unitName][relativePath] = info;

                        // Log this location to be flat-mapped against every aircraft later
                        allLocations.Add(new GlobalHangarInfo {
                            UnitName = unitName,
                            RelativePath = relativePath,
                            DisplayName = $"{categoryPrefix}{unitName} - {i + 1:D2}. {configName}",
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
                string acKey = ac.name; // Rely entirely on prefab name!
                string friendlyName = PrefabToDisplayName.TryGetValue(acKey, out string fn) ? fn : ac.unitName;
                if (string.IsNullOrEmpty(friendlyName)) friendlyName = acKey;
                
                // Prefix with 1. Aircraft or 9. UFO to force them below 0. Global Overrides
                string sectionName = acKey.Contains("UFO") || acKey.Contains("???") ? $"9. UFO - {acKey}" : $"1. Aircraft - {acIndex:D2}. {friendlyName} ({acKey})";

                int locationOrderCounter = sortedLocations.Count; // High number = renders at the top of the category

                foreach (var location in sortedLocations)
                {
                    bool isShip = location.UnitName.ToLower().Contains("carrier") || 
                                  location.UnitName.ToLower().Contains("destroyer") || 
                                  location.UnitName.ToLower().Contains("frigate") || 
                                  location.UnitName.ToLower().Contains("corvette") || 
                                  location.UnitName.ToLower().Contains("cutter") || 
                                  location.UnitName.ToLower().Contains("cruiser") || 
                                  location.UnitName.ToLower().Contains("supply ship") ||
                                  location.UnitName.ToLower().Contains("supplyship") ||
                                  location.UnitName.ToLower().Contains("landing craft") ||
                                  location.UnitName.ToLower().Contains("otb-31");

                    bool isHelipad = location.DisplayName.ToLower().Contains("helipad");

                    bool defaultAllowed = IsPredesignatedPlace(acKey, location.UnitName, location.RelativePath, isShip, isHelipad, ac);

                    string keyName = SanitizeConfigKey(location.DisplayName);

                    var configEntry = Instance.Config.Bind(
                        sectionName, 
                        keyName, 
                        defaultAllowed, 
                        new ConfigDescription($"Allow {friendlyName} ({acKey}) to spawn at {location.DisplayName}.", null, 
                        new ConfigurationManagerAttributes { DispName = keyName, Order = locationOrderCounter })
                    );

                    locationOrderCounter--; // Decrement ensures perfectly grouped display for hangars

                    // Insert deep into our lookup dictionary
                    if (!UnitHangarConfigs.ContainsKey(location.UnitName))
                        UnitHangarConfigs[location.UnitName] = new Dictionary<string, Dictionary<string, ConfigEntry<bool>>>();
                    
                    if (!UnitHangarConfigs[location.UnitName].ContainsKey(location.RelativePath))
                        UnitHangarConfigs[location.UnitName][location.RelativePath] = new Dictionary<string, ConfigEntry<bool>>();
                        
                    UnitHangarConfigs[location.UnitName][location.RelativePath][acKey] = configEntry;
                }
                acIndex++;
            }

            // 4. FACTION RESTRICTIONS BINDING: One slider per unique Aircraft
            for (int i = 0; i < sortedAircraft.Count; i++)
            {
                var ac = sortedAircraft[i];
                string acKey = ac.name; // Rely entirely on prefab name!
                string friendlyName = PrefabToDisplayName.TryGetValue(acKey, out string fn) ? fn : ac.unitName;
                if (string.IsNullOrEmpty(friendlyName)) friendlyName = acKey;

                var factionEntry = Instance.Config.Bind(
                    "3. Faction Restrictions",
                    $"{friendlyName} ({acKey}) Faction Restriction",
                    0,
                    new ConfigDescription(
                        $"Faction restriction for {friendlyName} ({acKey}). 0 = Both, 1 = PALA Only, 2 = BDF Only",
                        new AcceptableValueRange<int>(0, 2)
                    )
                );

                AircraftFactionRestrictions[acKey] = factionEntry;
            }

            SpawnControlPlugin.allAircraft = sortedAircraft;
            Log.LogInfo($"SpawnControl: Configuration pre-generation complete. Bound {sortedAircraft.Count} aircraft against {sortedLocations.Count} locations.");
        }

        public static string GetRelativePath(Transform t, Transform root)
        {
            if (t == null || root == null) return "";
            if (t == root) return t.name;

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
            int idx1 = n.IndexOf(" (");
            if (idx1 > 0) return n.Substring(0, idx1).Trim();
            int idx2 = n.IndexOf("(");
            if (idx2 > 0) return n.Substring(0, idx2).Trim();
            return n.Trim();
        }

        private static bool IsVTOL(AircraftDefinition def)
        {
            if (def == null) return false;
            string acKey = def.name; // Rely entirely on prefab name!
            if (string.IsNullOrEmpty(acKey)) acKey = def.unitName;

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
                string unitNameLower = (def.unitName ?? "").ToLower();
                if (lower.Contains("chicane") || lower.Contains("ibis") || lower.Contains("tarantula") || lower.Contains("medusa") || lower.Contains("vortex") || lower.Contains("lighthelicopter") ||
                    unitNameLower.Contains("chicane") || unitNameLower.Contains("ibis") || unitNameLower.Contains("tarantula") || unitNameLower.Contains("medusa") || unitNameLower.Contains("vortex"))
                {
                    result = true;
                }
            }

            VTOLCache[acKey] = result;
            return result;
        }

        // =========================================================================
        // BAKED MODDED AIRCRAFT SPAWNER RULES
        // =========================================================================

        private static readonly HashSet<string> VanillaPrefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "COIN",
            "AttackHelo1",
            "CAS1",
            "FastBomber1",
            "Trainer",
            "Fighter1",
            "EW1",
            "UtilityHelo1",
            "SmallFighter1",
            "Multirole1",
            "QuadVTOL1",
            "Darkreach",
            "SFB",
        };

        public static bool IsVanillaAircraft(AircraftDefinition ac)
        {
            if (ac == null) return false;
            if (!string.IsNullOrEmpty(ac.name) && VanillaPrefabNames.Contains(ac.name)) return true;
            return false;
        }
        public static readonly Dictionary<string, string> ShipPrefabToDisplayName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Aryx_StrikeCarrier1", "Aryx Strike Carrier" },
            { "Aryx_SupplyShip1", "Aryx Supply Ship" },
            { "Aryx_EscortCarrier1", "Aryx Escort Carrier" },
            { "Aryx_HeavyFrigate1", "Aryx Heavy Frigate" }
        };

        public static readonly Dictionary<string, Dictionary<string, string>> NavalFriendlyDescriptions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            {
                "Annex Class Carrier",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "hangar_M", "Mid Elevator" },
                    { "hull_RL/hull_RR/hull_wellDeck/hull_RRL/hangar_R1", "Well Deck Elevator" },
                    { "hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R2", "Well Deck Deckspawn 1" },
                    { "hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R3", "Well Deck Deckspawn 2" },
                    { "hull_L/hull_FR/hangar_F", "Bow Elevator" }
                }
            },
            {
                "Hyperion Class Carrier",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "hull_R/hull_R2/hull_RRRL/hangar_R2", "Aft Deckspawn 1" },
                    { "hull_R/hull_R2/hull_RRRR/hangar_R3", "Aft Deckspawn 2" },
                    { "hull_R/hull_RRR/hangar_R1", "Main Hangar" },
                    { "hull_F/hangar_F", "Bow Hangar" }
                }
            },
            {
                "Devotion Class Light Carrier",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_BR/Hangar_H", "Heli Elevator" },
                    { "Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_F", "Bow Elevator" },
                    { "Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_R_01", "Rear Hangar 1" },
                    { "Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_R_02", "Rear Hangar 2" }
                }
            },
            {
                "Andromeda class Cruiser",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Aryx_StrikeCarrier1_Hull_Rear/Hangar_Main", "Main Hangar" },
                    { "Aryx_StrikeCarrier1_Hull_Rear/Hangar_Heli", "Heli Hangar" },
                    { "Hangar_Deck_H1", "Helipad Deck" },
                    { "Hangar_Deck_F1", "Front Helipad 1" },
                    { "Hangar_Deck_F2", "Front Helipad 2" }
                }
            },
            {
                "Atlas Class Supply Ship",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Aryx_SupplyShip1_Rear/Aryx_SupplyShip1_Stern/Hangar_R", "Stern Hangar" },
                    { "Aryx_SupplyShip1_Hangar_Floor/Hangar_F", "Main Hangar" }
                }
            },
            {
                "Ironside Class Frigate",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Aryx_Frigate2_Hangar/Hangar", "Hangar" }
                }
            },
            {
                "Argus Class Frigate",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "frigate1_hull_R/frigate1_hangar", "Hangar" }
                }
            },
            {
                "Cursor Class LFD",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "hangar", "Hangar" }
                }
            },
            {
                "Dynamo Class Destroyer",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Hull_CR/Hull_hangarFloor", "Hangar" }
                }
            }
        };

        public static string GetUnitDisplayName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return "";
            string clean = SanitizeGameObjectName(rawName);
            if (ShipPrefabToDisplayName.TryGetValue(clean, out string friendly)) return friendly;
            if (ShipPrefabToDisplayName.TryGetValue(rawName, out friendly)) return friendly;
            return rawName;
        }

        private static readonly HashSet<string> VanillaShipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Argus Class Frigate",
            "Cursor Class LFD",
            "Shard Class Corvette",
            "Annex Class Carrier",
            "Dynamo Class Destroyer",
            "Hyperion Class Carrier",
            "OTB-31 landing craft",
            // Modded ships (raw names)
            "Aryx_StrikeCarrier1",
            "Aryx_SupplyShip1",
            "Aryx_EscortCarrier1",
            "Aryx_HeavyFrigate1",
            // Modded ships (friendly names)
            "Aryx Strike Carrier",
            "Aryx Supply Ship",
            "Aryx Escort Carrier",
            "Aryx Heavy Frigate"
        };

        public static bool IsVanillaShip(string unitName)
        {
            if (string.IsNullOrEmpty(unitName)) return false;
            string clean = SanitizeGameObjectName(unitName);
            return VanillaShipNames.Contains(unitName) || VanillaShipNames.Contains(clean);
        }

        public static bool GetBakedDefaultAllowed(string acPrefabName, string unitName, string displayName, bool isShip, bool isHelipad, AircraftDefinition ac, AircraftDefinition[] nativeAllowed)
        {
            string acLower = acPrefabName.ToLower();
            string unitLower = unitName.ToLower();
            string displayLower = displayName.ToLower();

            // 1. Helipad rules: Helipads only allow VTOL aircraft by default
            if (isHelipad)
            {
                return IsVTOL(ac);
            }

            // 2. Check if this is a known modded aircraft
            bool isModded = !IsVanillaAircraft(ac);

            if (isModded)
            {
                // Do not default enable modded aircraft on modded ships
                if (isShip && !IsVanillaShip(unitName))
                {
                    return false;
                }
                // A. MiG-15
                if (acLower.Contains("mig-15") || acLower.Contains("aryx_mig-15"))
                {
                    if (unitLower.Contains("revetment")) return true;
                    if (unitLower.Contains("shelter")) return true;
                    if (unitLower.Contains("hangar_med") || unitLower.Contains("medium aircraft hangar")) return true;
                    if (isShip && (displayLower.Contains("hangar_r1") || displayLower.Contains("hangar r1") || displayLower.Contains("elevator") || displayLower.Contains("deckspawn"))) return true;
                    return false;
                }

                // B. F-16M King Viper
                if (acLower.Contains("f-16") || acLower.Contains("kingviper") || acLower.Contains("aryx_f16m"))
                {
                    if (unitLower.Contains("revetment")) return true;
                    if (unitLower.Contains("shelter")) return true;
                    if (unitLower.Contains("hangar_med") || unitLower.Contains("medium aircraft hangar")) return true;
                    if (isShip && (displayLower.Contains("hangar_r1") || displayLower.Contains("hangar r1") || displayLower.Contains("elevator") || displayLower.Contains("deckspawn"))) return true;
                    return false;
                }

                // C. FQ-106 Kestrel
                if (acLower.Contains("fq-106") || acLower.Contains("kestrel"))
                {
                    if (unitLower.Contains("revetment")) return true;
                    if (unitLower.Contains("shelter")) return true;
                    if (unitLower.Contains("hangar_med") || unitLower.Contains("medium aircraft hangar")) return true;
                    if (isShip && (displayLower.Contains("hangar_r1") || displayLower.Contains("hangar r1") || displayLower.Contains("elevator") || displayLower.Contains("deckspawn"))) return true;
                    return false;
                }

                // D. FS-3 Ternion
                if (acLower.Contains("fs-3") || acLower.Contains("ternion") || acLower.Contains("p_trisurface1"))
                {
                    if (unitLower.Contains("hangar_med") || unitLower.Contains("medium aircraft hangar")) return true;
                    return false;
                }

                // E. MC-260 Chimera
                if (acLower.Contains("mc-260") || acLower.Contains("chimera") || acLower.Contains("mc260") || acLower.Contains("cargoplane"))
                {
                    if (unitLower.Contains("hangar_med") || unitLower.Contains("medium aircraft hangar")) return true;
                    return false;
                }

                // F. RAH-72 Knockout (LightHelicopter)
                if (acLower.Contains("knockout") || acLower.Contains("lighthelicopter"))
                {
                    if (isShip)
                    {
                        return isHelipad || displayLower.Contains("elevator") || displayLower.Contains("deckspawn");
                    }
                    return isHelipad;
                }

                // G. Default fallback for other modded aircraft
                if (IsVTOL(ac))
                {
                    if (isShip)
                    {
                        return isHelipad || displayLower.Contains("elevator") || displayLower.Contains("deckspawn");
                    }
                    return isHelipad;
                }

                if (isShip)
                {
                    return displayLower.Contains("elevator") || displayLower.Contains("deckspawn");
                }
                else
                {
                    return unitLower.Contains("hangar_med") || unitLower.Contains("medium aircraft hangar") ||
                           unitLower.Contains("revetment") || unitLower.Contains("shelter");
                }
            }

            // 3. Vanilla aircraft rules: Default to the spawner's native allowed aircraft list
            if (nativeAllowed != null)
            {
                return nativeAllowed.Any(nativeAc => nativeAc != null && string.Equals(nativeAc.name, acPrefabName, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        public static string SanitizeConfigKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Unknown";
            s = s.Replace("[", "(").Replace("]", ")").Replace("=", "-").Replace("\\", "/")
                 .Replace("'", "").Replace("\"", "").Replace("\n", " ").Replace("\t", " ");
            return s.Trim();
        }

        public static bool ResolveHangarConfigContext(Hangar hangar, out string resolvedUnitName, out Transform resolvedRootTransform)
        {
            resolvedUnitName = "";
            resolvedRootTransform = null;

            Unit attachedUnit = hangar.attachedUnit ?? hangar.GetComponentInParent<Unit>();
            if (attachedUnit != null && attachedUnit.definition != null)
            {
                resolvedUnitName = attachedUnit.definition.unitName;
                if (string.IsNullOrEmpty(resolvedUnitName)) resolvedUnitName = attachedUnit.definition.name;
                resolvedUnitName = GetUnitDisplayName(resolvedUnitName);
                resolvedRootTransform = attachedUnit.transform;
                
                if (UnitHangarConfigs.ContainsKey(resolvedUnitName)) return true;
            }

            Transform t = hangar.transform;
            Airbase parentAirbase = hangar.GetComponentInParent<Airbase>();
            Transform stopAt = parentAirbase != null ? parentAirbase.transform.parent : null;

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

            t = hangar.transform;
            while (t != null && t != stopAt)
            {
                string rawName = t.gameObject.name.ToLower();
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

        public static readonly Dictionary<string, HashSet<string>> BakedAllowedSpawns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            {
                "Aryx_MiG-15_AircraftDefinition",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hull_RRL/hangar_R1",
                    "Medium Aircraft Hangar|hangar_med",
                    "Aircraft Revetment|revetment1",
                    "Hardened Aircraft Shelter|shelter1",
                    "Hyperion Class Carrier|hull_R/hull_RRR/hangar_R1",
                }
            },
            {
                "SmallFighter1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Cursor Class LFD|hangar",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hull_RRL/hangar_R1",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R2",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R3",
                    "Medium Aircraft Hangar|hangar_med",
                    "Helipad|Helipad",
                    "Hardened Aircraft Shelter|shelter1",
                    "Andromeda class Cruiser|Hangar_Deck_F1",
                    "Andromeda class Cruiser|Hangar_Deck_F2",
                    "Andromeda class Cruiser|Aryx_StrikeCarrier1_Hull_Rear/Hangar_Main",
                }
            },
            {
                "Trainer",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Medium Aircraft Hangar|hangar_med",
                    "Aircraft Revetment|revetment1",
                    "Hardened Aircraft Shelter|shelter1",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRL/hangar_R2",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRR/hangar_R3",
                    "Hyperion Class Carrier|hull_R/hull_RRR/hangar_R1",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_F",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_R_01",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_R_02",
                    "Andromeda class Cruiser|Aryx_StrikeCarrier1_Hull_Rear/Hangar_Main",
                }
            },
            {
                "P_Trisurface1_definition",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Medium Aircraft Hangar|hangar_med",
                }
            },
            {
                "Multirole1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Medium Aircraft Hangar|hangar_med",
                    "Hardened Aircraft Shelter|shelter1",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRL/hangar_R2",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRR/hangar_R3",
                    "Hyperion Class Carrier|hull_R/hull_RRR/hangar_R1",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_F",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_R_01",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_R_02",
                }
            },
            {
                "FastBomber1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Medium Aircraft Hangar|hangar_med",
                }
            },
            {
                "EW1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Annex Class Carrier|hangar_M",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R3",
                    "Medium Aircraft Hangar|hangar_med",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRL/hangar_R2",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRR/hangar_R3",
                    "Hyperion Class Carrier|hull_R/hull_RRR/hangar_R1",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hull_RRL/hangar_R1",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R2",
                    "Helipad|Helipad",
                }
            },
            {
                "Aryx_MC260_Chimera_Definition",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Medium Aircraft Hangar|hangar_med",
                }
            },
            {
                "CI-22",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hull_RRL/hangar_R1",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R2",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R3",
                    "Medium Aircraft Hangar|hangar_med",
                    "Aircraft Revetment|revetment1",
                    "Hardened Aircraft Shelter|shelter1",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRL/hangar_R2",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRR/hangar_R3",
                    "Hyperion Class Carrier|hull_R/hull_RRR/hangar_R1",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_F",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_R_01",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_R_02",
                    "Andromeda class Cruiser|Aryx_StrikeCarrier1_Hull_Rear/Hangar_Main",
                }
            },
            {
                "COIN",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hull_RRL/hangar_R1",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R2",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R3",
                    "Medium Aircraft Hangar|hangar_med",
                    "Aircraft Revetment|revetment1",
                    "Hardened Aircraft Shelter|shelter1",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRL/hangar_R2",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRR/hangar_R3",
                    "Hyperion Class Carrier|hull_R/hull_RRR/hangar_R1",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_F",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_R_01",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_Rear_R/Hangar_R_02",
                    "Andromeda class Cruiser|Aryx_StrikeCarrier1_Hull_Rear/Hangar_Main",
                }
            },
            {
                "Aryx_F16M_KingViper_AircraftDefinition",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hull_RRL/hangar_R1",
                    "Medium Aircraft Hangar|hangar_med",
                    "Aircraft Revetment|revetment1",
                    "Hardened Aircraft Shelter|shelter1",
                    "Hyperion Class Carrier|hull_R/hull_RRR/hangar_R1",
                }
            },
            {
                "AttackHelo1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Argus Class Frigate|frigate1_hull_R/frigate1_hangar",
                    "Cursor Class LFD|hangar",
                    "Annex Class Carrier|hangar_M",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R3",
                    "Annex Class Carrier|hull_L/hull_FR/hangar_F",
                    "Helipad|Helipad",
                    "Dynamo Class Destroyer|Hull_CR/Hull_hangarFloor",
                    "Hyperion Class Carrier|hull_R/hull_RRR/hangar_R1",
                    "Hyperion Class Carrier|hull_F/hangar_F",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_BR/Hangar_H",
                    "Andromeda class Cruiser|Hangar_Deck_H1",
                    "Andromeda class Cruiser|Aryx_StrikeCarrier1_Hull_Rear/Hangar_Heli",
                    "Atlas Class Supply Ship|Aryx_SupplyShip1_Rear/Aryx_SupplyShip1_Stern/Hangar_R",
                    "Field Deployable Airpad|Aryx_MC260_Airpad_1",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hull_RRL/hangar_R1",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R2",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRL/hangar_R2",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRR/hangar_R3",
                }
            },
            {
                "Aryx_LightHelicopter1_Definition",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Helipad|Helipad",
                }
            },
            {
                "kestrel_definition",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hull_RRL/hangar_R1",
                    "Medium Aircraft Hangar|hangar_med",
                    "Aircraft Revetment|revetment1",
                    "Hardened Aircraft Shelter|shelter1",
                    "Hyperion Class Carrier|hull_R/hull_RRR/hangar_R1",
                }
            },
            {
                "CAS1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Medium Aircraft Hangar|hangar_med",
                    "Aircraft Revetment|revetment1",
                    "Hardened Aircraft Shelter|shelter1",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hull_RRL/hangar_R1",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R2",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R3",
                }
            },
            {
                "SFB",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Medium Aircraft Hangar|hangar_med",
                }
            },
            {
                "UtilityHelo1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Helipad|Helipad",
                    "Dynamo Class Destroyer|Hull_CR/Hull_hangarFloor",
                    "Hyperion Class Carrier|hull_R/hull_RRR/hangar_R1",
                    "Hyperion Class Carrier|hull_F/hangar_F",
                    "Devotion Class Light Carrier|Aryx_EscortCarrier_Hull_B/Aryx_EscortCarrier_Hull_BR/Hangar_H",
                    "Ironside Class Frigate|Aryx_Frigate2_Hangar/Hangar",
                    "Andromeda class Cruiser|Hangar_Deck_H1",
                    "Andromeda class Cruiser|Aryx_StrikeCarrier1_Hull_Rear/Hangar_Heli",
                    "Atlas Class Supply Ship|Aryx_SupplyShip1_Rear/Aryx_SupplyShip1_Stern/Hangar_R",
                    "Atlas Class Supply Ship|Aryx_SupplyShip1_Hangar_Floor/Hangar_F",
                    "Field Deployable Airpad|Aryx_MC260_Airpad_1",
                    "Argus Class Frigate|frigate1_hull_R/frigate1_hangar",
                    "Cursor Class LFD|hangar",
                    "Annex Class Carrier|hangar_M",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hull_RRL/hangar_R1",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R2",
                    "Annex Class Carrier|hull_RL/hull_RR/hull_wellDeck/hangar_RR/hangar_R3",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRL/hangar_R2",
                    "Hyperion Class Carrier|hull_R/hull_R2/hull_RRRR/hangar_R3",
                }
            },
            {
                "QuadVTOL1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Annex Class Carrier|hangar_M",
                    "Helipad|Helipad",
                    "Atlas Class Supply Ship|Aryx_SupplyShip1_Rear/Aryx_SupplyShip1_Stern/Hangar_R",
                    "Atlas Class Supply Ship|Aryx_SupplyShip1_Hangar_Floor/Hangar_F",
                }
            },
            {
                "FS-12",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Medium Aircraft Hangar|hangar_med",
                    "Aircraft Revetment|revetment1",
                    "Hardened Aircraft Shelter|shelter1",
                }
            },
            {
                "Fighter1",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Medium Aircraft Hangar|hangar_med",
                    "Aircraft Revetment|revetment1",
                    "Hardened Aircraft Shelter|shelter1",
                }
            },
        };

        // Checks if an aircraft is natively/default predesignated to spawn at a specific location
        public static bool IsPredesignatedPlace(string acPrefabName, string unitName, string relativePath, bool isShip, bool isHelipad, AircraftDefinition ac)
        {
            // 1. Check the global NativeAllowedCache populated from all prefabs at startup
            if (NativeAllowedCache.TryGetValue(unitName, out var pathDict))
            {
                if (pathDict.TryGetValue(relativePath, out var allowedSet))
                {
                    if (allowedSet.Contains(acPrefabName))
                    {
                        return true;
                    }
                }
            }

            // 2. Check the prebaked allowed spawns database
            if (BakedAllowedSpawns.TryGetValue(acPrefabName, out var bakedSet))
            {
                string key = $"{unitName}|{relativePath}";
                if (bakedSet.Contains(key))
                {
                    return true;
                }
            }

            // 3. Fallback to baked rules (for modded aircraft defaults or when not in cache)
            return GetBakedDefaultAllowed(acPrefabName, unitName, relativePath, isShip, isHelipad, ac, null);
        }

        public static bool IsAircraftAllowed(Hangar hangar, AircraftDefinition definition)
        {
            if (hangar == null || definition == null) return false;

            string acKey = definition.name; // Rely entirely on prefab name!
            if (string.IsNullOrEmpty(acKey)) acKey = definition.unitName;

            // Check Faction Restrictions first (global aircraft cheap reject)
            if (AircraftFactionRestrictions.TryGetValue(acKey, out var factionEntry))
            {
                int restriction = factionEntry.Value;
                if (restriction != 0)
                {
                    FactionHQ hq = null;
                    GameManager.GetLocalHQ(out hq);
                    if (hq != null && hq.faction != null)
                    {
                        string factionName = hq.faction.factionName;
                        if (factionName != null)
                        {
                            if (restriction == 1 && (factionName.IndexOf("boscali", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                                     factionName.IndexOf("bdf", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                return false;
                            }
                            if (restriction == 2 && (factionName.IndexOf("primeva", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                                     factionName.IndexOf("pala", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                return false;
                            }
                        }
                    }
                }
            }

            bool isShip = false;
            Unit attachedUnit = hangar.attachedUnit ?? hangar.GetComponentInParent<Unit>();
            if (attachedUnit != null)
            {
                var shipComp = attachedUnit.GetComponent<Ship>() ?? attachedUnit.GetComponentInChildren<Ship>(true);
                if (shipComp != null || attachedUnit is Ship)
                {
                    isShip = true;
                }
                else
                {
                    string attachedName = attachedUnit.name;
                    isShip = attachedName != null && (attachedName.IndexOf("carrier", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      attachedName.IndexOf("destroyer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      attachedName.IndexOf("frigate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      attachedName.IndexOf("corvette", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      attachedName.IndexOf("cutter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      attachedName.IndexOf("cruiser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      attachedName.IndexOf("supply ship", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      attachedName.IndexOf("supplyship", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      attachedName.IndexOf("landing craft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      attachedName.IndexOf("otb-31", StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }

            if (ResolveHangarConfigContext(hangar, out string unitName, out Transform configRootTransform))
            {
                string relativePath = GetRelativePath(hangar.transform, configRootTransform);
                string hangarName = hangar.name;
                bool isHelipad = hangarName != null && hangarName.IndexOf("helipad", StringComparison.OrdinalIgnoreCase) >= 0;

                // 1. If Debug Auditor Mode is active:
                if (DebugAuditorMode.Value)
                {
                    return IsPredesignatedPlace(acKey, unitName, relativePath, isShip, isHelipad, definition);
                }

                // 2. Process God-Mode Toggles
                if (AllowAllEverywhere.Value) return true;
                if (isShip && AllowShipsToSpawnAll.Value) return true;
                if (!isShip && AllowLandBasesToSpawnAll.Value) return true;

                if (UnitHangarConfigs.TryGetValue(unitName, out var unitDict))
                {
                    // Strict match check
                    if (unitDict.TryGetValue(relativePath, out var hangarDict) && 
                        hangarDict.TryGetValue(acKey, out var configEntry))
                    {
                        return configEntry.Value;
                    }

                    // Fuzzy Match 1
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

                    // Fuzzy Match 2
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

                    // DYNAMIC CONFIG BINDING: Safely bind configuration with exact runtime predesignated outcome as default!
                    if (!unitDict.TryGetValue(relativePath, out var targetHangarDict))
                    {
                        targetHangarDict = new Dictionary<string, ConfigEntry<bool>>();
                        unitDict[relativePath] = targetHangarDict;
                    }

                    if (!targetHangarDict.TryGetValue(acKey, out configEntry))
                    {
                        string friendlyName = PrefabToDisplayName.TryGetValue(acKey, out string fn) ? fn : definition.unitName;
                        if (string.IsNullOrEmpty(friendlyName)) friendlyName = acKey;
                        string sectionName = acKey.Contains("UFO") || acKey.Contains("???") ? $"9. UFO - {acKey}" : $"1. Aircraft - {friendlyName} ({acKey})";

                        string displayName = $"{unitName} - {relativePath}";
                        if (HangarMetadataByPath.TryGetValue(unitName, out var pathDict) && pathDict.TryGetValue(relativePath, out var info))
                        {
                            displayName = $"{unitName} - {info.ConfigName}";
                        }
                        string keyName = SanitizeConfigKey(displayName);

                        bool defaultAllowed = IsPredesignatedPlace(acKey, unitName, relativePath, isShip, isHelipad, definition);

                        configEntry = Instance.Config.Bind(
                            sectionName,
                            keyName,
                            defaultAllowed, 
                            new ConfigDescription($"Allow {friendlyName} ({acKey}) to spawn at {displayName}.", null)
                        );

                        targetHangarDict[acKey] = configEntry;
                        Log.LogInfo($"[AUDIT-LIVE-SPAWN] Dynamically bound config entry for '{acKey}' at spawner '{displayName}' -> Default: {defaultAllowed}");
                    }

                    return configEntry.Value;
                }
            }

            // 1b. Debug Auditor Mode fallback if context not resolved
            if (DebugAuditorMode.Value)
            {
                AircraftDefinition[] origList = HangarAvailableAircraftRef(hangar);
                if (origList != null)
                {
                    return origList.Any(nativeAc => nativeAc != null && string.Equals(nativeAc.name, acKey, StringComparison.OrdinalIgnoreCase));
                }
                return false;
            }

            // 2b. God-Mode Toggles fallback
            if (AllowAllEverywhere.Value) return true;

            // 3. Global VTOL Settings
            string hangarName2 = hangar.name;
            bool isHelipad2 = hangarName2 != null && hangarName2.IndexOf("helipad", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isHelipad2 && AllowAllVTOLsOnHelipads.Value && IsVTOL(definition))
            {
                return true;
            }

            // 4. Absolute Final Fallback: Vanilla Game Rules
            AircraftDefinition[] origList2 = HangarAvailableAircraftRef(hangar);
            if (origList2 != null)
            {
                return origList2.Any(nativeAc => nativeAc != null && string.Equals(nativeAc.name, acKey, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        // Filters a vanilla-provided aircraft array through our permission config.
        public static AircraftDefinition[] FilterAllowedAircraft(Hangar hangar, AircraftDefinition[] vanillaResult)
        {
            if (hangar == null || vanillaResult == null) return vanillaResult;
            if (AllowAllEverywhere.Value && !DebugAuditorMode.Value) return vanillaResult;

            List<AircraftDefinition> allowed = new List<AircraftDefinition>(vanillaResult.Length);
            for (int i = 0; i < vanillaResult.Length; i++)
            {
                var def = vanillaResult[i];
                if (def == null) continue;
                if (IsAircraftAllowed(hangar, def)) allowed.Add(def);
            }
            return allowed.ToArray();
        }

        // Builds the full permitted list for a hangar from the global aircraft pool.
        public static AircraftDefinition[] GetAllAllowedAircraftForHangar(Hangar hangar)
        {
            if (hangar == null) return new AircraftDefinition[0];

            var aircraftList = allAircraft.Count > 0 ? allAircraft : Resources.FindObjectsOfTypeAll<AircraftDefinition>().ToList();
            if (AllowAllEverywhere.Value && !DebugAuditorMode.Value) return aircraftList.ToArray();

            List<AircraftDefinition> allowed = new List<AircraftDefinition>(aircraftList.Count);
            for (int i = 0; i < aircraftList.Count; i++)
            {
                var def = aircraftList[i];
                if (def == null || string.IsNullOrEmpty(def.name)) continue;
                if (IsAircraftAllowed(hangar, def)) allowed.Add(def);
            }
            return allowed.ToArray();
        }

        // =========================================================================
        // ACTIVE HARMONY OVERRIDE PATCHES
        // =========================================================================

        [HarmonyPatch(typeof(Hangar), "SpawnAircraft")]
        [HarmonyPostfix]
        static void Hangar_SpawnAircraft_Postfix(Hangar __instance, AircraftDefinition definition)
        {
            if (__instance == null || definition == null) return;

            string acKey = definition.name;
            if (string.IsNullOrEmpty(acKey)) acKey = definition.unitName;

            string friendlyName = PrefabToDisplayName.TryGetValue(acKey, out string fn) ? fn : definition.unitName;
            if (string.IsNullOrEmpty(friendlyName)) friendlyName = acKey;

            string spawnUnitName = "";
            Unit attachedUnit = __instance.attachedUnit ?? __instance.GetComponentInParent<Unit>();
            if (attachedUnit != null && attachedUnit.definition != null)
            {
                spawnUnitName = attachedUnit.definition.unitName;
                if (string.IsNullOrEmpty(spawnUnitName)) spawnUnitName = attachedUnit.definition.name;
                spawnUnitName = GetUnitDisplayName(spawnUnitName);
            }

            Log.LogInfo($"[AUDITOR-SPAWN-SCAN] Spawned '{friendlyName}' ({acKey}) at hangar '{__instance.name}' under parent '{spawnUnitName}'");
            Log.LogInfo($"[AUDITOR-SPAWN-SCAN] Allowed spawn locations for '{friendlyName}' ({acKey}):");

            int count = 0;
            foreach (var unitKvp in NativeAllowedCache)
            {
                string unitName = unitKvp.Key;
                foreach (var pathKvp in unitKvp.Value)
                {
                    string path = pathKvp.Key;

                    bool isShip = unitName.ToLower().Contains("carrier") || 
                                  unitName.ToLower().Contains("destroyer") || 
                                  unitName.ToLower().Contains("frigate") || 
                                  unitName.ToLower().Contains("corvette") || 
                                  unitName.ToLower().Contains("cutter") || 
                                  unitName.ToLower().Contains("cruiser") || 
                                  unitName.ToLower().Contains("supply ship") ||
                                  unitName.ToLower().Contains("supplyship") ||
                                  unitName.ToLower().Contains("landing craft") ||
                                  unitName.ToLower().Contains("otb-31");

                    bool isHelipad = path.ToLower().Contains("helipad");
                    if (!isHelipad)
                    {
                        if (HangarMetadataByPath.TryGetValue(unitName, out var pathDict) && pathDict.TryGetValue(path, out var info))
                        {
                            isHelipad = info.HangarType == "helipad" || info.ConfigName.ToLower().Contains("helipad");
                        }
                    }

                    if (IsPredesignatedPlace(acKey, unitName, path, isShip, isHelipad, definition))
                    {
                        string displayName = path;
                        if (HangarMetadataByPath.TryGetValue(unitName, out var pathDict) && pathDict.TryGetValue(path, out var info))
                        {
                            displayName = info.ConfigName;
                        }
                        Log.LogInfo($"  - {unitName} -> {displayName} (path: {path})");
                        count++;
                    }
                }
            }

            if (count == 0)
            {
                Log.LogInfo("You suck lmao");
            }
        }


        [HarmonyPatch(typeof(Hangar), nameof(Hangar.CanSpawnAircraft))]
        [HarmonyPostfix]
        static void Hangar_CanSpawnAircraft_Postfix(Hangar __instance, AircraftDefinition definition, ref bool __result)
        {
            if (definition == null || __instance == null) return;

            // If the hangar itself is not available (occupied, spawning, or disabled),
            // no aircraft of any kind can spawn here!
            if (!__instance.Available)
            {
                __result = false;
                return;
            }

            __result = IsAircraftAllowed(__instance, definition);
        }

        [HarmonyPatch(typeof(Hangar), nameof(Hangar.GetAvailableAircraft))]
        [HarmonyPostfix]
        static void Hangar_GetAvailableAircraft_Postfix(Hangar __instance, ref AircraftDefinition[] __result)
        {
            if (__instance == null) return;

            // If the hangar itself is not available (occupied, spawning, or disabled),
            // no aircraft of any kind can spawn here!
            if (!__instance.Available)
            {
                __result = new AircraftDefinition[0];
                return;
            }

            // Filter vanilla result (which has occupancy applied) through our config.
            // Then union with any modded aircraft our config permits that vanilla omits.
            var filtered = FilterAllowedAircraft(__instance, __result);
            var fullList = GetAllAllowedAircraftForHangar(__instance);
            // Add modded aircraft not in vanilla result, but only if they are permitted
            var resultSet = new HashSet<AircraftDefinition>(filtered);
            for (int i = 0; i < fullList.Length; i++)
            {
                var ac = fullList[i];
                if (!resultSet.Contains(ac))
                    resultSet.Add(ac);
            }
            __result = System.Linq.Enumerable.ToArray(resultSet);
        }

        [HarmonyPatch(typeof(Airbase), nameof(Airbase.CanSpawnAircraft))]
        [HarmonyPostfix]
        static void Airbase_CanSpawnAircraft_Postfix(Airbase __instance, AircraftDefinition definition, ref bool __result)
        {
            if (__instance == null || definition == null) return;

            // If the airbase itself is disabled, no aircraft can spawn!
            if (__instance.disabled)
            {
                __result = false;
                return;
            }

            List<Hangar> baseHangars = AirbaseHangarsRef(__instance);
            if (baseHangars == null || baseHangars.Count == 0)
            {
                __result = false;
                return;
            }

            bool allowed = false;
            for (int i = 0; i < baseHangars.Count; i++)
            {
                var hangar = baseHangars[i];
                if (hangar == null) continue;

                // Hangar must be available (not occupied, spawning, or disabled)
                if (!hangar.Available) continue;

                if (IsAircraftAllowed(hangar, definition))
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
            if (__instance == null || __result == null) return;

            List<Hangar> baseHangars = AirbaseHangarsRef(__instance);
            if (baseHangars == null || baseHangars.Count == 0) return;

            // Filter the vanilla result through our config (preserves occupancy).
            // Then union with modded aircraft our config permits that vanilla omits.
            var allowedSet = new HashSet<AircraftDefinition>();
            for (int i = 0; i < __result.Count; i++)
            {
                var ac = __result[i];
                if (ac == null) continue;
                for (int j = 0; j < baseHangars.Count; j++)
                {
                    var hangar = baseHangars[j];
                    if (hangar != null && hangar.Available && IsAircraftAllowed(hangar, ac))
                    {
                        allowedSet.Add(ac);
                        break;
                    }
                }
            }
            // Add modded aircraft not in vanilla result, permitted by our config
            for (int i = 0; i < baseHangars.Count; i++)
            {
                var hangar = baseHangars[i];
                if (hangar == null || !hangar.Available) continue; // CRITICAL check for occupancy/availability!
                var modded = GetAllAllowedAircraftForHangar(hangar);
                for (int j = 0; j < modded.Length; j++)
                {
                    allowedSet.Add(modded[j]);
                }
            }
            __result = allowedSet.ToList();
        }
    }
}