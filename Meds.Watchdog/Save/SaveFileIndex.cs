using System.Collections.Generic;
using System.Linq;
using Meds.Watchdog.Utils;

namespace Meds.Watchdog.Save
{
    public sealed class SaveFileIndex
    {
        private readonly Dictionary<GroupId, HashSet<EntityId>> _entitiesByGroup = new Dictionary<GroupId, HashSet<EntityId>>();
        private readonly Dictionary<EntityId, HashSet<GroupId>> _groupsByEntity = new Dictionary<EntityId, HashSet<GroupId>>();
        private readonly HashSet<ChunkId> _chunks = new HashSet<ChunkId>();

        public SaveFileIndex(SaveFile file)
        {
            using var access = file.Open();
            foreach (var group in access.Groups())
            {
                var id = group.Id;
                foreach (var ent in group.TopLevelEntities)
                {
                    _entitiesByGroup.GetOrAdd(id).Add(ent);
                    _groupsByEntity.GetOrAdd(ent).Add(id);
                }
            }

            foreach (var chunk in access.Chunks())
            {
                _chunks.Add(chunk.Id);
            }
        }

        public bool HasChunk(ChunkId id) => _chunks.Contains(id);

        public IEnumerable<GroupId> GroupsForEntity(EntityId entity) =>
            _groupsByEntity.TryGetValue(entity, out var groups) ? groups : Enumerable.Empty<GroupId>();

        public IEnumerable<EntityId> EntitiesForGroup(GroupId group) =>
            _entitiesByGroup.TryGetValue(group, out var entities) ? entities : Enumerable.Empty<EntityId>();

        public bool TryGetClosureForEntity(EntityId entity, out Closure closure)
        {
            closure = new Closure(entity).Expand(this);
            return closure.Entities.Count != 1 || closure.Groups.Count != 0;
        }

        public bool TryGetClosureForGroup(GroupId group, out Closure closure)
        {
            closure = new Closure(group).Expand(this);
            return closure.Entities.Count != 0 || closure.Groups.Count != 1;
        }

        public sealed class Closure
        {
            public readonly HashSet<EntityId> Entities;
            public readonly HashSet<GroupId> Groups;

            public Closure(EntityId entity) : this(new HashSet<EntityId> { entity }, new HashSet<GroupId>())
            {
            }

            public Closure(GroupId group) : this(new HashSet<EntityId>(), new HashSet<GroupId> { group })
            {
            }

            public Closure(
                HashSet<EntityId> entities,
                HashSet<GroupId> groups)
            {
                Entities = entities;
                Groups = groups;
            }

            public Closure Expand(SaveFileIndex index)
            {
                var queue = new Queue<(bool entity, ulong id)>();
                foreach (var ent in Entities)
                    queue.Enqueue((true, ent.Value));
                foreach (var group in Groups)
                    queue.Enqueue((false, group.Value));
                while (queue.Count > 0)
                {
                    var (isEntity, id) = queue.Dequeue();
                    if (isEntity)
                    {
                        if (!index._groupsByEntity.TryGetValue(new EntityId(id), out var neighbors))
                            continue;
                        foreach (var group in neighbors)
                            if (Groups.Add(group))
                                queue.Enqueue((false, group.Value));
                    }
                    else
                    {
                        if (!index._entitiesByGroup.TryGetValue(new GroupId(id), out var neighbors))
                            continue;
                        foreach (var entity in neighbors)
                            if (Entities.Add(entity))
                                queue.Enqueue((true, entity.Value));
                    }
                }

                return this;
            }
        }
    }
}