using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DynamicMapsExtended
{
    // DynamicMaps 1.3.1 moved zoom, clamping and viewport state out of MapView into
    // MainMapController/MiniMapController, which the owning ModdedMapScreen drives every frame.
    // The extension therefore cannot write zoom fields any more; it has to update map geometry and
    // then ask that screen to recompute its layout. Everything that needs to know which viewport a
    // MapView belongs to, or how to re-run the layout, goes through this bridge.
    internal static class MapScreenBridge
    {
        private const int UnresolvedMode = int.MinValue;

        private static Type _screenType;
        private static Type _viewType;
        private static Type _modeType;
        private static FieldInfo _viewField;
        private static FieldInfo _changingModeField;
        private static FieldInfo _dropdownField;
        private static PropertyInfo _displayModeProperty;
        private static PropertyInfo _currentMapDefProperty;
        private static PropertyInfo _coordinateRotationProperty;
        private static MethodInfo _requestModeMethod;

        private static int _miniMode = UnresolvedMode;
        private static int _hiddenMode = UnresolvedMode;
        private static bool _prepared;
        private static bool _warned;

        // There is one map screen per raid/menu session and one MapView per screen, so a
        // single-entry cache is enough and keeps the per-frame viewport queries allocation-free.
        private static Component _cachedView;
        private static Object _cachedScreen;

        internal static Type ScreenType => _screenType ??= AccessTools.TypeByName("DynamicMaps.UI.ModdedMapScreen");
        internal static Type ViewType => _viewType ??= AccessTools.TypeByName("DynamicMaps.UI.Components.MapView");

        internal static Object[] Screens()
        {
            var screenType = ScreenType;
            return screenType == null ? new Object[0] : Resources.FindObjectsOfTypeAll(screenType);
        }

        internal static Object[] MapViews()
        {
            var viewType = ViewType;
            return viewType == null ? new Object[0] : Resources.FindObjectsOfTypeAll(viewType);
        }

        internal static Component GetMapView(Object screen)
        {
            if (screen == null || !Prepare()) return null;
            return _viewField?.GetValue(screen) as Component;
        }

        internal static object GetDropdown(Object screen)
        {
            var screenType = ScreenType;
            if (screen == null || screenType == null) return null;
            _dropdownField ??= AccessTools.Field(screenType, "_mapSelectDropdown");
            return _dropdownField?.GetValue(screen);
        }

        internal static object GetMapDef(Component view)
        {
            if (view == null || !Prepare()) return null;
            return _currentMapDefProperty?.GetValue(view);
        }

        internal static float GetCoordinateRotation(Component view)
        {
            if (view == null || !Prepare()) return 0f;
            return _coordinateRotationProperty?.GetValue(view) is float rotation ? rotation : 0f;
        }

        // Used by the tile refinement policy: high-resolution packs are only justified when the
        // viewport is a real map screen. An unresolvable screen fails closed, exactly like the
        // pre-1.3.1 minimap flag lookup did.
        internal static bool IsMainViewport(Component view)
        {
            var mode = GetDisplayMode(view);
            return mode.HasValue && mode.Value != _miniMode;
        }

        private static int? GetDisplayMode(Component view)
        {
            if (view == null || !Prepare()) return null;

            // Negative results are cached too: NeedsHighResolution asks this per visible layer
            // every frame, and Resources.FindObjectsOfTypeAll allocates on each search.
            var screen = ReferenceEquals(_cachedView, view) ? _cachedScreen : FindScreen(view);

            if (screen == null) return null;

            var value = _displayModeProperty?.GetValue(screen);
            if (value == null) return null;
            return Convert.ToInt32(value);
        }

        private static Object FindScreen(Component view)
        {
            foreach (var candidate in Screens())
            {
                if (candidate == null) continue;
                if (!ReferenceEquals(_viewField?.GetValue(candidate), view)) continue;

                _cachedView = view;
                _cachedScreen = candidate;
                return candidate;
            }

            _cachedView = view;
            _cachedScreen = null;
            return null;
        }

        // Re-runs the screen's own layout/viewport computation for the currently displayed mode.
        // DynamicMaps recomputes minimum zoom, clamping and the map root rectangle from MapDef
        // bounds inside MainMapController.Resize / MiniMapController.Open, and it keeps the player's
        // centre and normalized zoom while in a raid. Hidden screens are skipped: HideDisplay()
        // would tear the display down and reopening configures the viewport from the new bounds
        // anyway.
        internal static bool TryRelayout(Object screen)
        {
            if (screen == null || !Prepare()) return false;

            if (_changingModeField?.GetValue(screen) is true)
            {
                Plugin.Debug("Skipped viewport relayout: DynamicMaps is already changing display mode.");
                return false;
            }

            var mode = _displayModeProperty?.GetValue(screen);
            if (mode == null) return false;

            var value = Convert.ToInt32(mode);
            if (value == _hiddenMode) return false;

            try
            {
                _requestModeMethod.Invoke(screen, new[] { mode, false, true });
                return true;
            }
            catch (Exception e)
            {
                Plugin.Debug("DynamicMaps viewport relayout failed: " + e.Message);
                return false;
            }
        }

        private static bool Prepare()
        {
            if (_prepared) return true;

            var screenType = ScreenType;
            var viewType = ViewType;
            if (screenType == null || viewType == null)
                return Warn("DynamicMaps map screen/view types were not found.");

            _viewField ??= AccessTools.Field(screenType, "_mapView");
            _changingModeField ??= AccessTools.Field(screenType, "_changingMode");
            _displayModeProperty ??= AccessTools.Property(screenType, "DisplayMode");

            var modeType = _displayModeProperty?.PropertyType;
            if (_viewField == null || _displayModeProperty == null || modeType == null || !modeType.IsEnum)
                return Warn("DynamicMaps display-mode internals were not found.");

            _modeType = modeType;
            _miniMode = ParseMode("Mini");
            _hiddenMode = ParseMode("Hidden");
            if (_miniMode == UnresolvedMode || _hiddenMode == UnresolvedMode)
                return Warn("DynamicMaps display-mode values were not found.");

            _requestModeMethod ??= AccessTools.Method(
                screenType, "RequestMode", new[] { modeType, typeof(bool), typeof(bool) });
            if (_requestModeMethod == null)
                return Warn("DynamicMaps ModdedMapScreen.RequestMode was not found.");

            _currentMapDefProperty ??= AccessTools.Property(viewType, "CurrentMapDef");
            _coordinateRotationProperty ??= AccessTools.Property(viewType, "CoordinateRotation");
            if (_currentMapDefProperty == null || _coordinateRotationProperty == null)
                return Warn("DynamicMaps MapView definition/rotation members were not found.");

            _prepared = true;
            return true;
        }

        private static int ParseMode(string name)
        {
            try
            {
                return Convert.ToInt32(Enum.Parse(_modeType, name, true));
            }
            catch
            {
                return UnresolvedMode;
            }
        }

        private static bool Warn(string message)
        {
            if (!_warned)
            {
                _warned = true;
                Plugin.Log?.LogWarning(message + " Live map geometry updates are disabled.");
            }

            return false;
        }
    }
}
