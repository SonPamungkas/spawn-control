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
    [BepInPlugin("com.spawncontrol", "SpawnControl", "1.1")]
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
            public AircraftDefinition[] AirbaseAllowed;
            public int UnitSortOrder;
            public int HangarIndex;
            public bool IsHelipad;
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

        // Key: Aircraft Key (unitName)
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

        // Caches VTOL reflection checks by Aircraft unitName for maximum performance
        public static Dictionary<string, bool> VTOLCache = new Dictionary<string, bool>();

        public static HashSet<Hangar> CachedHangars = new HashSet<Hangar>();
        private static List<AircraftDefinition> allAircraft = new List<AircraftDefinition>();

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

        private IEnumerator InitSpawnControlConfigs()
        {
            Log.LogInfo("SpawnControl: Waiting for Blueprinter and all aircraft definitions to stabilize...");

            // 1. Wait until AircraftDefinition count stabilizes (ensuring all Blueprinter assets are fully loaded)
            int lastCount = 0;
            int stableTicks = 0;
            while (stableTicks < 5)
            {
                var currentAircraft = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
                int currentCount = currentAircraft != null ? currentAircraft.Length : 0;
                if (currentCount > 0 && currentCount == lastCount)
                {
                    stableTicks++;
                }
                else
                {
                    lastCount = currentCount;
                    stableTicks = 0;
                }
                yield return new WaitForSeconds(1.0f);
            }

            Log.LogInfo($"SpawnControl: Aircraft definitions stabilized at {lastCount}. Pre-generating hangar options...");

            var allUnits = Resources.FindObjectsOfTypeAll<UnitDefinition>();
            var allAircraft = Resources.FindObjectsOfTypeAll<AircraftDefinition>();

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
                    
                    // Direct component gather from prefab to prevent state re-initialization corruption
                    baseHangars = def.unitPrefab.GetComponentsInChildren<Hangar>(true).ToList();

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
                        bool isHelipad = name.Contains("helipad") || goName.Contains("helipad");

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

                        // Query and evaluate natively allowed defaults with parent airbase check
                        AircraftDefinition[] airbaseAllowed = null;
                        if (airbase != null)
                        {
                            var airbaseListField = typeof(Airbase).GetField("availableAircraft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (airbaseListField != null)
                            {
                                var list = airbaseListField.GetValue(airbase) as List<AircraftDefinition>;
                                if (list != null)
                                {
                                    airbaseAllowed = list.ToArray();
                                }
                            }
                        }

                        // Log this location to be flat-mapped against every aircraft later
                        allLocations.Add(new GlobalHangarInfo {
                            UnitName = unitName,
                            RelativePath = relativePath,
                            DisplayName = $"{categoryPrefix}{unitName} - {i + 1:D2}. {configName}",
                            NativeAllowed = nativeAllowed,
                            AirbaseAllowed = airbaseAllowed,
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
                    bool isShip = location.UnitName.ToLower().Contains("carrier") || 
                                  location.UnitName.ToLower().Contains("destroyer") || 
                                  location.UnitName.ToLower().Contains("frigate") || 
                                  location.UnitName.ToLower().Contains("cutter") || 
                                  location.UnitName.ToLower().Contains("cruiser") || 
                                  location.UnitName.ToLower().Contains("supply ship");

                    bool isHelipad = location.DisplayName.ToLower().Contains("helipad");

                    bool defaultAllowed = GetBakedDefaultAllowed(acKey, location.UnitName, location.DisplayName, isShip, isHelipad, ac, location.NativeAllowed);

                    string keyName = SanitizeConfigKey(location.DisplayName);

                    var configEntry = Instance.Config.Bind(
                        sectionName, 
                        keyName, 
                        defaultAllowed, 
                        new ConfigDescription($"Allow {acKey} to spawn at {location.DisplayName}.", null, 
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
                string acKey = ac.unitName;
                if (string.IsNullOrEmpty(acKey)) acKey = ac.name;

                var factionEntry = Instance.Config.Bind(
                    "3. Faction Restrictions",
                    $"{acKey} Faction Restriction",
                    0,
                    new ConfigDescription(
                        $"Faction restriction for {acKey}. 0 = Both, 1 = PALA Only, 2 = BDF Only",
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

        // =========================================================================
        // BAKED MODDED AIRCRAFT SPAWNER RULES
        // =========================================================================

        private static readonly HashSet<string> VanillaAircraftKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CI-22 Cricket",
            "SAH-46 Chicane",
            "A-19 Brawler",
            "Alkyon AB-4",
            "T/A-30 Compass",
            "FS-12 Revoker",
            "EW-25 Medusa",
            "UH-90 Ibis",
            "FS-20 Vortex",
            "KR-67 Ifrit",
            "VL-49 Tarantula",
            "SFB-81 Darkreach"
        };

        private static readonly HashSet<string> VanillaShipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Argus Class Frigate",
            "Cursor Class LFD",
            "Shard Class Corvette",
            "Annex Class Carrier",
            "Dynamo Class Destroyer",
            "Hyperion Class Carrier",
            "OTB-31 landing craft"
        };

        public static bool IsVanillaShip(string unitName)
        {
            if (string.IsNullOrEmpty(unitName)) return false;
            string clean = SanitizeGameObjectName(unitName);
            return VanillaShipNames.Contains(unitName) || VanillaShipNames.Contains(clean);
        }

        public static bool GetBakedDefaultAllowed(string acKey, string unitName, string displayName, bool isShip, bool isHelipad, AircraftDefinition ac, AircraftDefinition[] nativeAllowed)
        {
            string acLower = acKey.ToLower();
            string unitLower = unitName.ToLower();
            string displayLower = displayName.ToLower();

            // 1. Helipad rules: Helipads only allow VTOL aircraft by default
            if (isHelipad)
            {
                return IsVTOL(ac);
            }

            // 2. Check if this is a known modded aircraft
            bool isModded = !VanillaAircraftKeys.Contains(acKey);

            if (isModded)
            {
                // Do not default enable modded aircraft on modded ships
                if (isShip && !IsVanillaShip(unitName))
                {
                    return false;
                }
                // A. MiG-15
                if (acLower.Contains("mig-15"))
                {
                    if (unitLower.Contains("revetment")) return true;
                    if (unitLower.Contains("shelter")) return true;
                    if (unitLower.Contains("hangar_med") || unitLower.Contains("medium aircraft hangar")) return true;
                    if (isShip && (displayLower.Contains("hangar_r1") || displayLower.Contains("hangar r1") || displayLower.Contains("elevator") || displayLower.Contains("deckspawn"))) return true;
                    return false;
                }

                // B. F-16M King Viper (and any generic F-16)
                if (acLower.Contains("f-16") || acLower.Contains("king viper"))
                {
                    if (unitLower.Contains("revetment")) return true;
                    if (unitLower.Contains("shelter")) return true;
                    if (unitLower.Contains("hangar_med") || unitLower.Contains("medium aircraft hangar")) return true;
                    if (isShip && (displayLower.Contains("hangar_r1") || displayLower.Contains("hangar r1") || displayLower.Contains("elevator") || displayLower.Contains("deckspawn"))) return true;
                    return false;
                }

                // C. FQ-106 Kestrel (and any generic FQ-106)
                if (acLower.Contains("fq-106") || acLower.Contains("kestrel"))
                {
                    if (unitLower.Contains("revetment")) return true;
                    if (unitLower.Contains("shelter")) return true;
                    if (unitLower.Contains("hangar_med") || unitLower.Contains("medium aircraft hangar")) return true;
                    if (isShip && (displayLower.Contains("hangar_r1") || displayLower.Contains("hangar r1") || displayLower.Contains("elevator") || displayLower.Contains("deckspawn"))) return true;
                    return false;
                }

                // D. FS-3 Ternion (and any generic FS-3)
                if (acLower.Contains("fs-3") || acLower.Contains("ternion"))
                {
                    if (unitLower.Contains("hangar_med") || unitLower.Contains("medium aircraft hangar")) return true;
                    return false;
                }

                // E. MC-260 Chimera (and any generic MC-260)
                if (acLower.Contains("mc-260") || acLower.Contains("chimera"))
                {
                    if (unitLower.Contains("hangar_med") || unitLower.Contains("medium aircraft hangar")) return true;
                    return false;
                }

                // F. Default fallback for other modded aircraft
                if (IsVTOL(ac)) return true;
                return !isShip;
            }

            // 3. Vanilla aircraft rules: Default to the spawner's native allowed aircraft list
            return nativeAllowed != null && nativeAllowed.Any(nativeAc => nativeAc != null && string.Equals(nativeAc.unitName, acKey, StringComparison.OrdinalIgnoreCase));
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

        public static bool IsAircraftAllowed(Hangar hangar, AircraftDefinition definition)
        {
            if (hangar == null || definition == null) return false;
            if (AllowAllEverywhere.Value) return true;

            string acKey = definition.unitName;
            if (string.IsNullOrEmpty(acKey)) acKey = definition.name;

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
                    isShip = attachedName != null && attachedName.IndexOf("carrier", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }

            if (ResolveHangarConfigContext(hangar, out string unitName, out Transform configRootTransform))
            {
                string relativePath = GetRelativePath(hangar.transform, configRootTransform);

                // Process God-Mode Toggles
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

                    // DYNAMIC CONFIG BINDING: Safely bind configuration with exact runtime vanilla outcome as default!
                    if (!unitDict.TryGetValue(relativePath, out var targetHangarDict))
                    {
                        targetHangarDict = new Dictionary<string, ConfigEntry<bool>>();
                        unitDict[relativePath] = targetHangarDict;
                    }

                    if (!targetHangarDict.TryGetValue(acKey, out configEntry))
                    {
                        string sectionName = acKey.Contains("???") ? $"9. UFO - {acKey}" : $"1. Aircraft - {acKey}";

                        string displayName = $"{unitName} - {relativePath}";
                        if (HangarMetadataByPath.TryGetValue(unitName, out var pathDict) && pathDict.TryGetValue(relativePath, out var info))
                        {
                            displayName = $"{unitName} - {info.ConfigName}";
                        }
                        string keyName = SanitizeConfigKey(displayName);

                        // Resolve variables for dynamic baked defaults
                        string hangarName2 = hangar.name;
                        bool isHelipad2 = hangarName2 != null && hangarName2.IndexOf("helipad", StringComparison.OrdinalIgnoreCase) >= 0;

                        AircraftDefinition[] nativeAllowed = HangarAvailableAircraftRef(hangar);
                        bool defaultAllowed = GetBakedDefaultAllowed(acKey, unitName, displayName, isShip, isHelipad2, definition, nativeAllowed);

                        configEntry = Instance.Config.Bind(
                            sectionName,
                            keyName,
                            defaultAllowed, 
                            new ConfigDescription($"Allow {acKey} to spawn at {displayName}.", null)
                        );

                        targetHangarDict[acKey] = configEntry;
                        Log.LogInfo($"[AUDIT-LIVE-SPAWN] Dynamically bound config entry for '{acKey}' at spawner '{displayName}' -> Default: {defaultAllowed}");
                    }

                    return configEntry.Value;
                }
            }

            // 2. Global VTOL Settings
            string hangarName = hangar.name;
            bool isHelipad = hangarName != null && hangarName.IndexOf("helipad", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isHelipad && AllowAllVTOLsOnHelipads.Value && IsVTOL(definition))
            {
                return true;
            }

            // 3. Absolute Final Fallback: Vanilla Game Rules
            AircraftDefinition[] origList = HangarAvailableAircraftRef(hangar);
            if (origList != null)
            {
                return origList.Any(nativeAc => nativeAc != null && string.Equals(nativeAc.unitName, acKey, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        public static AircraftDefinition[] GetAllowedAircraftForHangar(Hangar hangar)
        {
            if (hangar == null) return new AircraftDefinition[0];
            
            var aircraftList = allAircraft.Count > 0 ? allAircraft : Resources.FindObjectsOfTypeAll<AircraftDefinition>().ToList();
            if (AllowAllEverywhere.Value) return aircraftList.ToArray();

            List<AircraftDefinition> allowed = new List<AircraftDefinition>();
            for (int i = 0; i < aircraftList.Count; i++)
            {
                var def = aircraftList[i];
                if (def == null || string.IsNullOrEmpty(def.unitName)) continue;
                if (IsAircraftAllowed(hangar, def)) allowed.Add(def);
            }
            return allowed.ToArray();
        }

        // =========================================================================
        // ACTIVE HARMONY OVERRIDE PATCHES
        // =========================================================================

        [HarmonyPatch(typeof(Hangar), nameof(Hangar.CanSpawnAircraft))]
        [HarmonyPostfix]
        static void Hangar_CanSpawnAircraft_Postfix(Hangar __instance, AircraftDefinition definition, ref bool __result)
        {
            if (definition == null || __instance == null) return;
            __result = IsAircraftAllowed(__instance, definition);
        }

        [HarmonyPatch(typeof(Hangar), nameof(Hangar.GetAvailableAircraft))]
        [HarmonyPostfix]
        static void Hangar_GetAvailableAircraft_Postfix(Hangar __instance, ref AircraftDefinition[] __result)
        {
            if (__instance == null) return;
            __result = GetAllowedAircraftForHangar(__instance);
        }

        [HarmonyPatch(typeof(Airbase), nameof(Airbase.CanSpawnAircraft))]
        [HarmonyPostfix]
        static void Airbase_CanSpawnAircraft_Postfix(Airbase __instance, AircraftDefinition definition, ref bool __result)
        {
            if (__instance == null || definition == null) return;

            List<Hangar> baseHangars = AirbaseHangarsRef(__instance);
            if (baseHangars == null || baseHangars.Count == 0) return;

            bool allowed = false;
            for (int i = 0; i < baseHangars.Count; i++)
            {
                var hangar = baseHangars[i];
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
            if (__instance == null || __result == null) return;

            List<Hangar> baseHangars = AirbaseHangarsRef(__instance);
            if (baseHangars == null || baseHangars.Count == 0) return;

            var allowedSet = new HashSet<AircraftDefinition>();
            for (int i = 0; i < baseHangars.Count; i++)
            {
                var hangar = baseHangars[i];
                if (hangar == null) continue;
                var allowed = GetAllowedAircraftForHangar(hangar);
                for (int j = 0; j < allowed.Length; j++)
                {
                    var ac = allowed[j];
                    allowedSet.Add(ac);
                }
            }
            __result = allowedSet.ToList();
        }
    }
}