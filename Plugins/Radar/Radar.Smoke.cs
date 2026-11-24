namespace Radar
{
    using System;
    using System.Linq;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;

    public sealed partial class Radar
    {
        private static readonly string[] TransitionWords = { "transition", "exit", "entrance", "portal" };
        private static readonly string[] PoiWords = { "waypoint", "checkpoint", "quest", "boss", "arena", "shrine", "altar", "ritual", "runestone", "waygate", "landmark", "reward", "chest" };
        private int mapDrawFrame = -1;

        object IPluginSmokeProbe.CollectSmokeData()
        {
            var state = Core.States.InGameStateObject;
            var area = state.CurrentAreaInstance;
            var areaId = state.CurrentWorldInstance.AreaDetails.Id;
            this.Settings.ImportantTgts.TryGetValue(areaId, out var areaRules);
            this.Settings.ImportantTgts.TryGetValue("common", out var commonRules);
            var terrain = area.TgtTilesLocations;
            var keys = terrain.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            var unlisted = keys.Where(key => !(
                areaRules?.ContainsKey(key) == true || commonRules?.ContainsKey(key) == true ||
                this.Settings.BossArenaTgts.ContainsKey(key) || this.Settings.StairsTgts.ContainsKey(key) ||
                ((key.StartsWith(TempleTgtPrefix, StringComparison.Ordinal) ||
                  key.StartsWith(RunestoneTgtPrefix, StringComparison.Ordinal)) &&
                 key.EndsWith(":1-y:1", StringComparison.Ordinal)))).ToArray();
            var drawn = this.mapDrawFrame == ImGui.GetFrameCount();

            object[] Samples(string[] terms) => unlisted
                .Where(key => terms.Length == 0 || terms.Any(term =>
                    key.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Take(48)
                .Select(key => (object)new
                {
                    key, occurrences = terrain[key].Count,
                    grid = terrain[key].Take(3).Select(point => new { x = point.X, y = point.Y }).ToArray(),
                }).ToArray();

            var transitions = area.AwakeEntities.Values
                .Where(entity => entity.IsValid &&
                    entity.Path.StartsWith("Metadata/MiscellaneousObjects/", StringComparison.Ordinal) &&
                    TransitionWords.Any(word => entity.Path.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(entity => entity.Path, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal).Take(48)
                .Select(group => new
                {
                    path = group.Key,
                    count = group.Count(),
                    grid = group.Take(3).Select(entity => entity.TryGetComponent<Render>(out var render)
                        ? new { x = render.GridPosition.X, y = render.GridPosition.Y } : null).ToArray(),
                }).ToArray();

            return new
            {
                areaInNameCatalog = WorldAreaNames.TryGetDisplayName(areaId, out _),
                areaPoiRules = areaRules?.Count ?? 0,
                areaPoiMatches = areaRules?.Keys.Count(terrain.ContainsKey) ?? 0,
                commonPoiMatches = commonRules?.Keys.Count(terrain.ContainsKey) ?? 0,
                tileKeys = keys.Length,
                unknownTileKeys = unlisted.Length,
                unknownTerrain = Samples(Array.Empty<string>()),
                possibleTerrainTransitions = Samples(TransitionWords),
                possibleTerrainPoi = Samples(PoiWords),
                transitionEntityCandidates = transitions,
                walkableBytes = area.GridWalkableData.Length,
                heightRows = area.GridHeightData.Length,
                bytesPerRow = area.TerrainMetadata.BytesPerRow,
                mapEnabled = this.Settings.DrawWalkableMap,
                textureReady = this.walkableMapTexture != IntPtr.Zero &&
                    this.observedAreaInstanceAddress == area.Address && this.observedAreaHash == area.AreaHash,
                textureWidth = this.walkableMapDimension.X,
                textureHeight = this.walkableMapDimension.Y,
                mapDrawnThisFrame = drawn,
                routeCount = drawn ? this.routeLegend.Count : 0,
                routes = drawn ? this.routeLegend.Keys.Take(32).Select(route => new { route.name, route.color }).ToArray() : null,
            };
        }
    }
}
