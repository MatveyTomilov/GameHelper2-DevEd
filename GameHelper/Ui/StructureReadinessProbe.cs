namespace GameHelper.Ui
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Natives;
    using GameOffsets.Objects.Components;
    using GameOffsets.Objects.States.InGameState;
    using GameOffsets.Objects.UiElement;

    // These probes reuse intact roots and related fields, but do not read or rank by
    // the configured offset of the field being recovered.
    internal static class StructureReadinessProbe
    {
        private const int MaxEntityRoots = 32;
        private sealed record Candidate(int Offset, int SupportingRoots);
        private sealed record Finding(string Field, int SampledRoots, string Evidence, Candidate[] Candidates);

        internal static object Collect()
        {
            var state = Core.States.InGameStateObject;
            var area = state.CurrentAreaInstance;
            var awake = area.AwakeEntities.Where(pair => pair.Key.id != 0 && pair.Value.Address != IntPtr.Zero)
                .OrderBy(pair => pair.Key.id).Take(512).ToArray();
            var entityIdRoots = awake.Take(MaxEntityRoots).ToArray();
            var entities = awake.Select(pair => pair.Value).Prepend(area.Player).DistinctBy(entity => entity.Address).ToArray();
            var actorRoots = ComponentRoots(entities, "Actor").Take(10).ToArray();
            var lifeRoots = ComponentRoots(entities, "Life").Take(MaxEntityRoots).ToArray();
            var playerLife = ComponentRoots(new[] { area.Player }, "Life").FirstOrDefault();
            int.TryParse(Environment.GetEnvironmentVariable("GH_STRUCTURE_MAX_LIFE"), out var maxLife);
            var renderRoots = RenderRoots(entities).Take(MaxEntityRoots).ToArray();
            var uiRoots = UiRoots(state.GameUi.Address);
            var terrainAddress = area.Address + Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.TerrainMetadata)).ToInt32();
            var tileDimensions = area.TerrainMetadata.TotalTiles;
            var results = new[]
            {
                Scan("EntityOffsets.Id", entityIdRoots, 0x200, sizeof(uint), Math.Max(4, entityIdRoots.Length - 1),
                    "uint matches the independent awake-entity tree key",
                    (sample, offset) => Core.Process.Handle.TryReadMemory<uint>(sample.Value.Address + offset, out var id) && id == sample.Key.id),
                Scan("ActorOffset.ActiveSkillsPtr", actorRoots, 0x1200, IntPtr.Size, 1,
                    "nonempty native vector, skill rows decode to named granted effects",
                    (address, offset) => HasSkillVector(address + offset)),
                Scan("LifeOffset.Health", lifeRoots, 0x500, IntPtr.Size, Math.Max(1, (lifeRoots.Length + 1) / 2),
                    "vital block with backlink to this Life component and positive maximum",
                    (address, offset) => HasVital(address, offset) &&
                        (maxLife <= 0 || (playerLife != IntPtr.Zero && HasVital(playerLife, offset, maxLife)))),
                Scan("RenderOffsets.CurrentWorldPosition", renderRoots, 0x400, sizeof(float), Math.Max(3, (renderRoots.Length + 1) / 2),
                    "finite tuple with XY independently mirrored by the same entity's Positioned component",
                    (sample, offset) => HasRenderPosition(sample, offset)),
                Scan("UiElementBaseOffset.Self", uiRoots, 0x300, IntPtr.Size, Math.Max(4, uiRoots.Length - 1),
                    "candidate pointer equals each distinct UI node's own address",
                    (address, offset) => Core.Process.Handle.TryReadMemory<IntPtr>(address + offset, out var self) && self == address),
                Scan("TerrainStruct.TileDetailsPtr", new[] { terrainAddress }, 0x280, IntPtr.Size, 1,
                    "vector stride matches independent tile dimensions and sampled records resolve real TGT paths",
                    (address, offset) => HasTerrainTiles(address + offset, tileDimensions)),
            };
            return new { areaId = state.CurrentWorldInstance.AreaDetails.Id, areaHash = area.AreaHash, results };
        }

        private static Finding Scan<T>(string field, T[] roots, int end, int stride, int minimum,
            string evidence, Func<T, int, bool> matches)
        {
            var candidates = new List<Candidate>();
            if (roots.Length >= minimum)
            {
                for (int offset = 0; offset < end; offset += stride)
                {
                    int count = roots.Count(root => matches(root, offset));
                    if (count >= minimum)
                    {
                        candidates.Add(new Candidate(offset, count));
                    }
                }
            }

            return new Finding(field, roots.Length, evidence, candidates.OrderByDescending(c => c.SupportingRoots)
                .ThenBy(c => c.Offset).ToArray());
        }

        private static IEnumerable<IntPtr> ComponentRoots(IEnumerable<Entity> entities, string name) =>
            entities.SelectMany(entity => entity.GetComponentAddressPairs())
                .Where(pair => pair.Key == name && pair.Value != IntPtr.Zero).Select(pair => pair.Value).Distinct();

        private static bool HasSkillVector(IntPtr candidate)
        {
            if (!TryGetVector(candidate, Marshal.SizeOf<ActiveSkillStructure>(), 250, out var vector, out int count) || count < 2)
            {
                return false;
            }

            var reader = Core.Process.Handle;
            int named = 0;
            var samples = reader.ReadMemoryArray<ActiveSkillStructure>(vector.First, Math.Min(count, 12));
            foreach (var skill in samples)
            {
                if (!reader.TryReadMemory<ActiveSkillDetails>(skill.ActiveSkillPtr, out var details) ||
                    !reader.TryReadMemory<IntPtr>(details.GrantedEffectsPerLevelDatRow, out var nameAddress))
                {
                    continue;
                }

                var name = reader.ReadUnicodeString(nameAddress);
                if (name.Length is >= 3 and <= 96 && char.IsAsciiLetter(name[0]) &&
                    name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
                {
                    named++;
                }
            }

            return named >= 2 && named * 4 >= samples.Length * 3;
        }

        private static bool HasVital(IntPtr root, int offset, int expectedTotal = 0) =>
            Core.Process.Handle.TryReadMemory<VitalStruct>(root + offset, out var vital) &&
            vital.PtrToLifeComponent == root && vital.Total is > 0 and <= 100_000_000 &&
            vital.Current >= 0 && vital.Current <= vital.Total &&
            (expectedTotal == 0 || vital.Total == expectedTotal) &&
            vital.ReservedPercent is >= 0 and <= 10000 && float.IsFinite(vital.Regeneration);

        private sealed record RenderRoot(IntPtr Address, HashSet<(float X, float Y)> PositionedCoordinates);

        private static IEnumerable<RenderRoot> RenderRoots(IEnumerable<Entity> entities)
        {
            foreach (var entity in entities)
            {
                var components = entity.GetComponentAddressPairs().ToDictionary(pair => pair.Key, pair => pair.Value);
                if (!components.TryGetValue("Render", out var render) || render == IntPtr.Zero ||
                    !components.TryGetValue("Positioned", out var positioned) || positioned == IntPtr.Zero)
                {
                    continue;
                }

                var values = Core.Process.Handle.ReadMemoryArray<float>(positioned, 0x400 / sizeof(float));
                var coordinates = new HashSet<(float, float)>();
                for (int i = 0; i + 1 < values.Length; i++)
                {
                    if (WorldCoordinate(values[i]) && WorldCoordinate(values[i + 1]))
                    {
                        coordinates.Add((values[i], values[i + 1]));
                    }
                }

                if (coordinates.Count != 0) { yield return new RenderRoot(render, coordinates); }
            }
        }

        private static bool WorldCoordinate(float value) => value is > 50f and < 250_000f;

        private static bool HasRenderPosition(RenderRoot sample, int offset) =>
            Core.Process.Handle.TryReadMemory<StdTuple3D<float>>(sample.Address + offset, out var point) &&
            sample.PositionedCoordinates.Contains((point.X, point.Y)) && float.IsFinite(point.Z) && Math.Abs(point.Z) < 100_000f;

        private static IntPtr[] UiRoots(IntPtr address)
        {
            var reader = Core.Process.Handle;
            if (!reader.TryReadMemory<UiElementBaseOffset>(address, out var root)) { return Array.Empty<IntPtr>(); }
            var childCount = root.ChildrensPtr.TotalElements(IntPtr.Size);
            if (childCount is < 4 or > 512) { return Array.Empty<IntPtr>(); }
            return reader.ReadStdVector<IntPtr>(root.ChildrensPtr).Distinct()
                .Where(child => reader.TryReadMemory<UiElementBaseOffset>(child, out var childNode) && childNode.ParentPtr == address)
                .Take(16).ToArray();
        }

        private static bool HasTerrainTiles(IntPtr candidate, StdTuple2D<long> dimensions)
        {
            var reader = Core.Process.Handle;
            var size = Marshal.SizeOf<TileStructure>();
            if (dimensions.X is < 1 or > 1000 || dimensions.Y is < 1 or > 1000 ||
                !TryGetVector(candidate, size, 100_000, out var vector, out int count) ||
                count != dimensions.X * dimensions.Y || count < 4)
            {
                return false;
            }

            int realTiles = 0;
            for (int sample = 0; sample < 12; sample++)
            {
                var tileAddress = vector.First + ((count - 1) * sample / 11) * size;
                if (reader.TryReadMemory<TileStructure>(tileAddress, out var tile) &&
                    reader.TryReadMemory<TgtFileStruct>(tile.TgtFilePtr, out var tgt) &&
                    reader.ReadStdWString(tgt.TgtPath).StartsWith("Metadata/Terrain/", StringComparison.Ordinal))
                {
                    realTiles++;
                }
            }

            return realTiles >= 2;
        }

        private static bool TryGetVector(IntPtr address, int size, int maxCount, out StdVector vector, out int count)
        {
            count = 0;
            if (!Core.Process.Handle.TryReadMemory<StdVector>(address, out vector) ||
                vector.First.ToInt64() <= 0x10000 || vector.Last.ToInt64() < vector.First.ToInt64() ||
                vector.End.ToInt64() < vector.Last.ToInt64())
            {
                return false;
            }

            long bytes = vector.Last.ToInt64() - vector.First.ToInt64();
            if (bytes <= 0 || bytes % size != 0 || bytes / size > maxCount) { return false; }
            count = (int)(bytes / size);
            return true;
        }
    }
}
