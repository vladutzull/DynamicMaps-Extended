using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DynamicMapsExtended
{
    // Artwork can carry different authoritative world bounds from the SVG it replaces. A live F12
    // change therefore needs one controlled geometry update so DynamicMaps recalculates map root
    // size, min/max zoom and clamping from the newly calibrated bounds. This is user-triggered
    // configuration work, never minimap-open work.
    internal static class MapDefinitionRefreshManager
    {
        private static MethodInfo _filterMethod;
        private static MethodInfo _loadFirstMethod;

        internal static void OnArtworkSettingChanged()
        {
            if (Plugin.Instance != null) RefreshArtworkNow();
        }

        private static void RefreshArtworkNow()
        {
            try
            {
                var realLocationId = DynamicMapsBridge.GetCurrentLocationOrNull();
                var reserveLocationId = realLocationId;

                // Character -> Maps has no live EFT location ID. If Reserve is the map currently
                // displayed there, derive the canonical ID from the active DynamicMaps MapView so
                // F12 can still swap between native Reserve and the calibrated Satellite MapDef.
                if (string.IsNullOrWhiteSpace(reserveLocationId))
                    reserveLocationId = TryGetActiveReserveLocationId();

                var normalized = reserveLocationId?.Trim().ToLowerInvariant();
                if (normalized == "rezervbase" || normalized == "reserve")
                {
                    if (TryRefreshReserveViaDropdown(reserveLocationId))
                        return;
                }

                ReloadActiveMapViews();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Could not refresh map after artwork change: {e.Message}");
            }
        }

        private static string TryGetActiveReserveLocationId()
        {
            try
            {
                var views = MapScreenBridge.MapViews();
                if (views.Length == 0)
                    return null;

                foreach (var candidate in views)
                {
                    if (!(candidate is Component view) || !view.gameObject.activeInHierarchy)
                        continue;

                    var mapDef = MapScreenBridge.GetMapDef(view);
                    if (mapDef == null)
                        continue;

                    var mapType = mapDef.GetType();
                    var names = AccessTools.Property(mapType, "MapInternalNames")?.GetValue(mapDef) as IEnumerable;
                    if (names != null)
                    {
                        foreach (var value in names)
                        {
                            var name = value as string;
                            if (string.Equals(name, "RezervBase", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Reserve", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, MapAliasPatch.ReserveSatelliteAlias, StringComparison.OrdinalIgnoreCase))
                                return "RezervBase";
                        }
                    }

                    var display = AccessTools.Property(mapType, "DisplayName")?.GetValue(mapDef) as string;
                    if (!string.IsNullOrWhiteSpace(display) &&
                        display.StartsWith("Reserve", StringComparison.OrdinalIgnoreCase))
                        return "RezervBase";
                }
            }
            catch (Exception e)
            {
                Plugin.Debug("Could not identify active Reserve map: " + e.Message);
            }

            return null;
        }

        // Reserve has both a native DynamicMaps definition and the calibrated Satellite one.
        // Re-running the dropdown selection is the only way to swap MapDefs, so DynamicMaps reloads
        // layers and rebuilds its viewport from the new definition.
        private static bool TryRefreshReserveViaDropdown(string realLocationId)
        {
            if (string.IsNullOrWhiteSpace(realLocationId))
                return false;

            var screens = MapScreenBridge.Screens();
            if (screens.Length == 0)
                return false;

            foreach (var candidate in screens)
            {
                if (!(candidate is Component screen) || !screen.gameObject.activeInHierarchy)
                    continue;

                var dropdown = MapScreenBridge.GetDropdown(candidate);
                if (dropdown == null)
                    continue;

                var dropdownType = dropdown.GetType();
                _filterMethod ??= AccessTools.Method(dropdownType, "FilterByInternalMapName", new[] { typeof(string) });
                _loadFirstMethod ??= AccessTools.Method(dropdownType, "LoadFirstAvailableMap");
                if (_filterMethod == null || _loadFirstMethod == null)
                    continue;

                _filterMethod.Invoke(dropdown, new object[] { realLocationId });
                _loadFirstMethod.Invoke(dropdown, null);

                // LoadFirstAvailableMap only fires when the filtered list changed, so the newly
                // selected definition still needs the calibrated geometry committed.
                var view = MapScreenBridge.GetMapView(candidate);
                var mapDef = MapScreenBridge.GetMapDef(view);
                if (mapDef != null)
                {
                    RasterCalibrationManager.PrepareMapDef(mapDef);
                    ApplyMapRootSize(view, mapDef);
                }

                MapScreenBridge.TryRelayout(candidate);
                Plugin.Debug($"Reserve MapDef refreshed live after artwork change: {realLocationId}.");
                return true;
            }

            return false;
        }

        private static void ReloadActiveMapViews()
        {
            var refreshed = 0;
            foreach (var candidate in MapScreenBridge.Screens())
            {
                if (!(candidate is Component screen) || !screen.gameObject.activeInHierarchy)
                    continue;

                var view = MapScreenBridge.GetMapView(candidate);
                if (view == null)
                    continue;

                var mapDef = MapScreenBridge.GetMapDef(view);
                if (mapDef == null)
                    continue;

                RasterCalibrationManager.PrepareMapDef(mapDef);
                // UnloadMap destroys every marker, including the live player and provider-owned
                // trail. Update geometry in place instead, then let DynamicMaps recompute its own
                // zoom limits and clamping from the calibrated bounds.
                ApplyMapRootSize(view, mapDef);
                MapScreenBridge.TryRelayout(candidate);

                ArtworkDisplayRefresh.Request(view);
                refreshed++;
            }

            if (refreshed > 0)
                Plugin.Debug($"Updated {refreshed} DynamicMaps view(s) in place after artwork/bounds change; live markers retained.");
        }

        // Mirrors MapView.LoadMap's root sizing: the rotated footprint of MapDef.Bounds. DynamicMaps
        // reads this rectangle for content extents, so it has to be updated before the relayout.
        private static void ApplyMapRootSize(Component view, object mapDef)
        {
            var bounds = AccessTools.Property(mapDef.GetType(), "Bounds")?.GetValue(mapDef);
            if (bounds == null || !(view.transform is RectTransform rect))
                return;

            var min = (Vector2)AccessTools.Property(bounds.GetType(), "Min").GetValue(bounds);
            var max = (Vector2)AccessTools.Property(bounds.GetType(), "Max").GetValue(bounds);
            var angle = MapScreenBridge.GetCoordinateRotation(view) * Mathf.Deg2Rad;
            var c = Mathf.Abs(Mathf.Cos(angle));
            var s = Mathf.Abs(Mathf.Sin(angle));
            var size = max - min;
            var desired = new Vector2(size.x * c + size.y * s, size.x * s + size.y * c);

            if ((rect.sizeDelta - desired).sqrMagnitude > 0.0001f)
                rect.sizeDelta = desired;
        }
    }
}
