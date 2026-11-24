using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Newtonsoft.Json;

namespace DynamicMapsExtended
{
    [HarmonyPatch]
    internal static class ExtensionMapLoaderPatch
    {
        // Extension map definitions are deliberately .jsonc only. The Maps folder also contains
        // JSON manifests/provenance files; treating those as MapDef files would make DynamicMaps
        // attempt to deserialize asset metadata as maps.
        private static readonly HashSet<string> AcceptableExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jsonc" };

        private static readonly HashSet<string> SupportedArtworkExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".svg", ".png" };

        private static FieldInfo _writeTimesField;
        private static FieldInfo _mapDefsField;
        private static FieldInfo _nameFilterField;
        private static MethodInfo _filteredMethod;
        private static MethodInfo _changeAvailableMethod;
        private static MethodInfo _registerPreferencesMethod;
        private static Type _mapDefType;
        private static readonly HashSet<string> WarnedUnavailableDefinitions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex ImagePathRegex =
            new Regex(@"""ImagePath""\s*:\s*""(?<path>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // DynamicMaps forgets every definition it did not discover on its own paths, so extension
        // entries are re-registered after each scan. Reusing the instance keeps an already displayed
        // MapView untouched: MapView.LoadMap treats the same instance as "already loaded", which is
        // what preserves live player/quest markers across a map-screen reopen.
        private static readonly Dictionary<string, CachedDefinition> Definitions =
            new Dictionary<string, CachedDefinition>(StringComparer.OrdinalIgnoreCase);

        private sealed class CachedDefinition
        {
            internal DateTime WriteTime;
            internal object MapDef;
        }

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Controls.MapSelectDropdown")
                ?? throw new MissingMemberException("DynamicMaps.UI.Controls.MapSelectDropdown not found");

            return AccessTools.Method(type, "LoadMapDefsFromPath", new[] { typeof(string) })
                ?? throw new MissingMethodException(type.FullName, "LoadMapDefsFromPath");
        }

        private static void Postfix(object __instance)
        {
            if (__instance == null) return;

            try
            {
                LoadExtensionDefinitions(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"Could not load DynamicMaps Extended definitions: {e}");
            }
        }

        private static void EnsureReflection(object dropdown)
        {
            var dropdownType = dropdown.GetType();
            _writeTimesField ??= AccessTools.Field(dropdownType, "_writeTimes");
            _mapDefsField ??= AccessTools.Field(dropdownType, "_mapDefs");
            _nameFilterField ??= AccessTools.Field(dropdownType, "_nameFilter");
            _filteredMethod ??= AccessTools.Method(dropdownType, "FilteredMapDefs");
            _changeAvailableMethod ??= AccessTools.Method(dropdownType, "ChangeAvailableMapDefs");
            _registerPreferencesMethod ??= AccessTools.Method(dropdownType, "RegisterMapPreferences");

            _mapDefType ??= AccessTools.TypeByName("DynamicMaps.Data.MapDef")
                ?? throw new MissingMemberException("DynamicMaps.Data.MapDef not found");
        }

        private static void LoadExtensionDefinitions(object dropdown)
        {
            var mapsRoot = Plugin.MapsRoot;
            if (string.IsNullOrEmpty(mapsRoot) || !Directory.Exists(mapsRoot))
            {
                Plugin.Debug($"Extension Maps folder not found: {mapsRoot}");
                return;
            }

            EnsureReflection(dropdown);

            if (!(_writeTimesField?.GetValue(dropdown) is IDictionary writeTimes) ||
                !(_mapDefsField?.GetValue(dropdown) is IDictionary mapDefs))
                throw new MissingMemberException("DynamicMaps map-definition dictionaries were not found.");

            var changed = false;
            foreach (var path in Directory.EnumerateFiles(mapsRoot, "*.*", SearchOption.AllDirectories)
                         .Where(p => AcceptableExtensions.Contains(Path.GetExtension(p))))
            {
                if (!ShouldLoadDefinition(path))
                    continue;

                var writeTime = File.GetLastWriteTimeUtc(path);
                // DynamicMaps drops entries it cannot find on its own paths, so an extension
                // definition is current only while both dictionaries still hold it.
                if (mapDefs.Contains(path) &&
                    writeTimes.Contains(path) && writeTimes[path] is DateTime previous && previous == writeTime)
                    continue;

                var mapDef = GetOrLoadDefinition(path, writeTime);
                if (mapDef == null)
                    continue;

                // Alias definitions ship without duplicated labels/markers. Copying the native
                // DynamicMaps lists keeps room labels and static markers identical to the map the
                // alias replaces, instead of asking users to maintain two copies.
                CopyNativePresentationMetadata(mapDef, mapDefs);

                writeTimes[path] = writeTime;
                mapDefs[path] = mapDef;
                changed = true;

                var displayName = AccessTools.Property(mapDef.GetType(), "DisplayName")?.GetValue(mapDef) as string ?? Path.GetFileName(path);
                Plugin.Debug($"Extension MapDef registered: {displayName} ({path})");
            }

            if (!changed) return;

            RefreshDropdown(dropdown);
            Plugin.Debug("DynamicMaps dropdown refreshed with extension MapDefs.");
        }

        private static object GetOrLoadDefinition(string path, DateTime writeTime)
        {
            if (Definitions.TryGetValue(path, out var cached) &&
                cached.WriteTime == writeTime && cached.MapDef != null)
                return cached.MapDef;

            Definitions.Remove(path);

            try
            {
                var mapDef = JsonConvert.DeserializeObject(File.ReadAllText(path), _mapDefType);
                if (mapDef == null)
                    throw new InvalidDataException("The definition deserialized to nothing.");

                ValidateDefinition(mapDef, path);
                Definitions[path] = new CachedDefinition { WriteTime = writeTime, MapDef = mapDef };
                return mapDef;
            }
            catch (Exception e)
            {
                if (WarnedUnavailableDefinitions.Add(path))
                    Plugin.Log?.LogWarning($"DynamicMaps Extended could not load MapDef {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }

        // DynamicMaps 1.3.1 validates and normalizes definitions inside MapDef.LoadFromPath, and
        // extension files are deliberately loaded without that method so their artwork resolves
        // against this plugin instead of DynamicMaps' folder. Replicate the same contract here.
        private static void ValidateDefinition(object mapDef, string path)
        {
            var type = mapDef.GetType();
            var bounds = AccessTools.Property(type, "Bounds")?.GetValue(mapDef);
            var layers = AccessTools.Property(type, "Layers")?.GetValue(mapDef) as IDictionary;
            var internalNames = AccessTools.Property(type, "MapInternalNames")?.GetValue(mapDef);
            var displayName = AccessTools.Property(type, "DisplayName")?.GetValue(mapDef) as string;

            if (bounds == null || layers == null || layers.Count == 0 ||
                internalNames == null || string.IsNullOrWhiteSpace(displayName))
                throw new InvalidDataException("Incomplete map definition.");

            ResolveLayerPaths(mapDef);

            foreach (DictionaryEntry entry in layers)
            {
                var layerDef = entry.Value;
                if (layerDef == null)
                    throw new InvalidDataException($"Layer '{entry.Key}' is empty.");

                var layerType = layerDef.GetType();
                var imagePath = AccessTools.Property(layerType, "ImagePath")?.GetValue(layerDef) as string;
                var imageBounds = AccessTools.Property(layerType, "ImageBounds")?.GetValue(layerDef);
                var gameBounds = AccessTools.Property(layerType, "GameBounds")?.GetValue(layerDef);

                if (string.IsNullOrWhiteSpace(imagePath) || imageBounds == null || gameBounds == null)
                    throw new InvalidDataException($"Layer '{entry.Key}' is incomplete.");

                if (!SupportedArtworkExtensions.Contains(Path.GetExtension(imagePath)))
                    throw new InvalidDataException($"Layer '{entry.Key}' uses unsupported artwork: {imagePath}");

                if (!File.Exists(imagePath) || new FileInfo(imagePath).Length == 0)
                    throw new InvalidDataException($"Layer '{entry.Key}' artwork is missing: {imagePath}");
            }

            var selectionIdProperty = AccessTools.Property(type, "SelectionId");
            if (selectionIdProperty != null && selectionIdProperty.CanWrite)
                selectionIdProperty.SetValue(mapDef, SelectionIdFor(path));

            // Extension definitions are companions to DynamicMaps, not Addons: IsAddon is what makes
            // DynamicMaps skip their precache and rename their display name.
            AccessTools.Property(type, "IsAddon")?.SetValue(mapDef, false);
        }

        // DynamicMaps orders selectable definitions by IsAddon then SelectionId and shows the id when
        // two definitions share a display name, so extension ids stay stable, unique and sorted
        // behind the native definition of the same map.
        private static string SelectionIdFor(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetFullPath(Plugin.ExtensionRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                var relative = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? full.Substring(root.Length)
                    : Path.GetFileName(full);

                return "dmext/" + relative.Replace('\\', '/');
            }
            catch
            {
                return "dmext/" + Path.GetFileName(path);
            }
        }

        private static bool ShouldLoadDefinition(string path)
        {
            var p = path.Replace('\\', '/').ToLowerInvariant();

            if (p.Contains("/factory_classic/") && !PluginDetection.Loaded(Plugin.FactoryClassicGuid))
                return false;
            if (p.Contains("/interchange_backport/") && !PluginDetection.Loaded(Plugin.ManimalInterchangeGuid))
                return false;
            if (p.Contains("/labs_backport/") && !PluginDetection.Loaded(Plugin.ManimalLabsGuid))
                return false;
            if (p.Contains("/icebreaker/") && !PluginDetection.Loaded(Plugin.ManimalIcebreakerGuid))
                return false;
            if (p.Contains("/labyrinth_fallback/") && NativeLabyrinthDefinitionExists())
                return false;

            // Extension-only maps are useful only when every artwork file referenced by their
            // MapDef is actually present. Missing optional third-party packs must never create
            // blank/duplicate Character -> Maps entries or throw while DynamicMaps opens.
            return DefinitionAssetsAvailable(path);
        }

        private static bool NativeLabyrinthDefinitionExists()
        {
            try
            {
                var expected = Path.Combine(DynamicMapsBridge.PluginPath, "Maps", "Labyrinth", "Labyrinth_TarkovData.jsonc");
                return File.Exists(expected) && new FileInfo(expected).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void CopyNativePresentationMetadata(object extensionMapDef, IDictionary mapDefs)
        {
            if (extensionMapDef == null || mapDefs == null)
                return;

            string nativeInternalName = null;
            if (HasInternalName(extensionMapDef, MapAliasPatch.InterchangeAlias))
                nativeInternalName = "Interchange";
            else if (HasInternalName(extensionMapDef, MapAliasPatch.LabsAlias))
                nativeInternalName = "Laboratory";
            else if (HasInternalName(extensionMapDef, MapAliasPatch.ReserveSatelliteAlias))
                nativeInternalName = "RezervBase";

            if (string.IsNullOrEmpty(nativeInternalName))
                return;

            object native = null;
            foreach (DictionaryEntry pair in mapDefs)
            {
                if (pair.Value != null && HasInternalName(pair.Value, nativeInternalName))
                {
                    native = pair.Value;
                    break;
                }
            }

            if (native == null)
                return;

            foreach (var propertyName in new[] { "Labels", "StaticMarkers" })
            {
                var sourceProperty = AccessTools.Property(native.GetType(), propertyName);
                var targetProperty = AccessTools.Property(extensionMapDef.GetType(), propertyName);
                if (sourceProperty == null || targetProperty == null || !targetProperty.CanWrite)
                    continue;

                targetProperty.SetValue(extensionMapDef, sourceProperty.GetValue(native));
            }

            Plugin.Debug($"Copied native DynamicMaps labels/static markers into alias for {nativeInternalName}.");
        }

        private static bool DefinitionAssetsAvailable(string definitionPath)
        {
            try
            {
                var text = File.ReadAllText(definitionPath);
                foreach (Match match in ImagePathRegex.Matches(text))
                {
                    var imagePath = match.Groups["path"].Value;
                    if (string.IsNullOrWhiteSpace(imagePath))
                        continue;

                    string absolute;
                    const string dmRootPrefix = "DMROOT/";
                    if (imagePath.StartsWith(dmRootPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        absolute = Path.GetFullPath(Path.Combine(
                            DynamicMapsBridge.PluginPath,
                            imagePath.Substring(dmRootPrefix.Length).Replace('/', Path.DirectorySeparatorChar)));
                    }
                    else if (imagePath.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase))
                    {
                        absolute = Path.GetFullPath(Path.Combine(
                            Plugin.ExtensionRoot,
                            imagePath.Replace('/', Path.DirectorySeparatorChar)));
                    }
                    else
                    {
                        // Native DynamicMaps relative artwork is not owned/validated here.
                        continue;
                    }

                    if (File.Exists(absolute) && new FileInfo(absolute).Length > 0)
                        continue;

                    if (WarnedUnavailableDefinitions.Add(definitionPath))
                    {
                        Plugin.Log?.LogWarning(
                            $"Skipping optional DynamicMaps Extended map definition because bundled artwork is missing: " +
                            $"{Path.GetFileName(definitionPath)} -> {absolute}");
                    }
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                if (WarnedUnavailableDefinitions.Add(definitionPath))
                    Plugin.Log?.LogWarning($"Skipping unreadable DynamicMaps Extended map definition {definitionPath}: {e.Message}");
                return false;
            }
        }

        internal static void RefreshDropdown(object dropdown)
        {
            EnsureReflection(dropdown);

            if (_filteredMethod == null || _changeAvailableMethod == null)
                throw new MissingMemberException("DynamicMaps dropdown refresh methods were not found.");

            // The extension map list is the authoritative one whenever it changed, so DynamicMaps'
            // per-location preference entries must learn the extension choices too. That is what
            // lets a Manimal backport or Satellite definition be selected inside a raid.
            if (_registerPreferencesMethod != null)
            {
                try
                {
                    _registerPreferencesMethod.Invoke(dropdown, null);
                }
                catch (Exception e)
                {
                    Plugin.Debug("Could not register extension map preferences: " + e.Message);
                }
            }

            var filtered = _filteredMethod.Invoke(dropdown, null);
            var nameFilter = _nameFilterField?.GetValue(dropdown) as string;

            // Character -> Maps hides implementation-only aliases, except Manimal Interchange:
            // normal Interchange and Manimal Interchange are intentionally separate user choices.
            if (string.IsNullOrEmpty(nameFilter))
                filtered = RemoveAliasOnlyDefinitions(filtered);

            _changeAvailableMethod.Invoke(dropdown, new[] { filtered });
        }

        private static object RemoveAliasOnlyDefinitions(object enumerable)
        {
            if (!(enumerable is IEnumerable source) || _mapDefType == null)
                return enumerable;

            var listType = typeof(List<>).MakeGenericType(_mapDefType);
            var list = (IList)Activator.CreateInstance(listType);

            foreach (var mapDef in source)
            {
                if (mapDef == null)
                    continue;

                // Most extension alias MapDefs remain implementation details and stay hidden.
                // Manimal Interchange and Factory Classic are deliberate user-facing variants, so
                // both remain visible as separate selectable Character -> Maps rows.
                if (IsAliasOnly(mapDef) &&
                    !HasInternalName(mapDef, MapAliasPatch.InterchangeAlias) &&
                    !HasInternalName(mapDef, MapAliasPatch.FactoryClassicAlias))
                    continue;

                list.Add(mapDef);
            }

            return list;
        }

        private static bool HasInternalName(object mapDef, string expected)
        {
            var namesObj = AccessTools.Property(mapDef.GetType(), "MapInternalNames")?.GetValue(mapDef);
            if (!(namesObj is IEnumerable names))
                return false;

            foreach (var value in names)
            {
                if (value is string name && string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsAliasOnly(object mapDef)
        {
            var namesObj = AccessTools.Property(mapDef.GetType(), "MapInternalNames")?.GetValue(mapDef);
            if (!(namesObj is IEnumerable names))
                return false;

            var sawName = false;
            foreach (var value in names)
            {
                var name = value as string;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                sawName = true;
                if (!name.StartsWith("dmext_", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return sawName;
        }

        private static void ResolveLayerPaths(object mapDef)
        {
            var layersProperty = AccessTools.Property(mapDef.GetType(), "Layers");
            if (!(layersProperty?.GetValue(mapDef) is IDictionary layers))
                return;

            foreach (DictionaryEntry entry in layers)
            {
                var layerDef = entry.Value;
                if (layerDef == null) continue;

                var imagePathProperty = AccessTools.Property(layerDef.GetType(), "ImagePath");
                if (imagePathProperty == null || !imagePathProperty.CanWrite) continue;

                var imagePath = imagePathProperty.GetValue(layerDef) as string;
                if (string.IsNullOrWhiteSpace(imagePath) || Path.IsPathRooted(imagePath))
                    continue;

                // DMROOT/ is an explicit reference to artwork already shipped by DynamicMaps.
                // This is how Manimal Interchange reuses the verified Tarkov.dev SVGs exactly,
                // instead of copying/splitting them into a second potentially broken version.
                const string dmRootPrefix = "DMROOT/";
                if (imagePath.StartsWith(dmRootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var nativeRelative = imagePath.Substring(dmRootPrefix.Length)
                        .Replace('/', Path.DirectorySeparatorChar);
                    var nativeAbsolute = Path.GetFullPath(Path.Combine(DynamicMapsBridge.PluginPath, nativeRelative));

                    var dmRoot = Path.GetFullPath(DynamicMapsBridge.PluginPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;

                    if (!nativeAbsolute.StartsWith(dmRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"DynamicMaps artwork path escapes DynamicMaps folder: {imagePath}");

                    if (!File.Exists(nativeAbsolute))
                        throw new FileNotFoundException($"Referenced DynamicMaps artwork does not exist: {nativeAbsolute}");

                    imagePathProperty.SetValue(layerDef, nativeAbsolute);
                    continue;
                }

                var normalized = imagePath.Replace('/', Path.DirectorySeparatorChar);
                var absolute = Path.GetFullPath(Path.Combine(Plugin.ExtensionRoot, normalized));

                var root = Path.GetFullPath(Plugin.ExtensionRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                // Every extension definition stores its own artwork under Maps/. Anything else is
                // DynamicMaps-relative, exactly like a native definition, so resolve it there.
                if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Extension image path escapes its plugin folder: {imagePath}");

                imagePathProperty.SetValue(layerDef, absolute);
            }
        }
    }

    [HarmonyPatch]
    internal static class MapDropdownLogicalGroupingPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Controls.MapSelectDropdown")
                ?? throw new MissingMemberException("DynamicMaps.UI.Controls.MapSelectDropdown not found");
            return AccessTools.Method(type, "ClearFilter")
                ?? throw new MissingMethodException(type.FullName, "ClearFilter");
        }

        private static void Postfix(object __instance)
        {
            if (__instance == null) return;

            try
            {
                ExtensionMapLoaderPatch.RefreshDropdown(__instance);
            }
            catch (Exception e)
            {
                Plugin.Debug("Logical map dropdown grouping failed: " + e.Message);
            }
        }
    }
}
