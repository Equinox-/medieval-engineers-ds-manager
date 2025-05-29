using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Meds.Watchdog.Save
{
    public static class SaveFileGeoSearch
    {
        public class LodSearch
        {
            public readonly List<ChunkId> Chunks;
            public readonly BoundingBox WorldBox;
            public readonly BoundingBox[] LodCellBox;
            public readonly int[] LodCellCount;

            public LodSearch(GridDatabaseConfig config, SaveFileIndex index, BoundingBox worldBox)
            {
                Chunks = new List<ChunkId>();
                WorldBox = worldBox;
                LodCellCount = new int[config.MaxLod + 1];
                LodCellBox = new BoundingBox[config.MaxLod + 1];
                for (byte lod = 0; lod <= config.MaxLod; lod++)
                {
                    var cellBounds = config.FromWorld(lod, worldBox);
                    var minX = (int)Math.Floor(cellBounds.Min.X);
                    var minY = (int)Math.Floor(cellBounds.Min.Y);
                    var minZ = (int)Math.Floor(cellBounds.Min.Z);
                    var maxX = (int)Math.Ceiling(cellBounds.Max.X);
                    var maxY = (int)Math.Ceiling(cellBounds.Max.Y);
                    var maxZ = (int)Math.Ceiling(cellBounds.Max.Z);
                    LodCellBox[lod] = new BoundingBox
                    {
                        Min = new Vector3(minX, minY, minZ),
                        Max = new Vector3(maxX, maxY, maxZ)
                    };
                    for (var x = minX; x < maxX; x++)
                    for (var y = minY; y < maxY; y++)
                    for (var z = minZ; z < maxZ; z++)
                    {
                        var id = new ChunkId(lod, x, y, z);
                        if (index.HasChunk(id))
                        {
                            Chunks.Add(id);
                            LodCellCount[lod]++;
                        }
                    }
                }
            }
        }

        private static IEnumerable<ChunkAccessor> Chunks(SaveFileAccessor save, LodSearch search)
        {
            foreach (var id in search.Chunks)
                if (save.TryGetChunk(id, out var chunk))
                    yield return chunk;
        }

        private static void Collect(
            SaveFileAccessor save, LodSearch search,
            HashSet<GroupId> groups, HashSet<EntityId> entities)
        {
            // Always fill in the group list since it's required to get the full entity list.
            groups ??= new HashSet<GroupId>();
            foreach (var chunk in Chunks(save, search))
            {
                foreach (var group in chunk.Groups)
                    groups.Add(group);
                if (entities == null)
                    continue;
                foreach (var entity in chunk.Entities)
                    entities.Add(entity);
            }

            if (entities == null)
                return;

            // Load all groups to fill in entities
            Parallel.ForEach(groups, groupId =>
            {
                if (!save.TryGetGroup(groupId, out var group))
                    return;
                lock (entities)
                {
                    foreach (var entity in group.TopLevelEntities)
                        entities.Add(entity);
                }
            });
        }

        public static HashSet<EntityId> Entities(SaveFileAccessor save, LodSearch search)
        {
            var entities = new HashSet<EntityId>();
            Collect(save, search, null, entities);
            return entities;
        }

        public static HashSet<GroupId> Groups(SaveFileAccessor save, LodSearch search)
        {
            var groups = new HashSet<GroupId>();
            Collect(save, search, groups, null);
            return groups;
        }

        public static HashSet<SteamId> Players(SaveFileAccessor save, LodSearch search)
        {
            return save.Players().Where(player =>
            {
                var pos = player.EntityAccessor.PositionOptional?.Position;
                return pos.HasValue && search.WorldBox.Includes(pos.Value);
            }).Select(x => x.Id).ToHashSet();
        }
    }
}