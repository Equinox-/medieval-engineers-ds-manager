using System;
using System.Collections.Concurrent;
using System.Reflection;
using Medieval.Entities.Components.Planet;
using Medieval.GameSystems;
using Meds.Wrapper.Audit;
using Sandbox.Definitions.Equipment;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Entity.Stats;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.EntityComponents.Character;
using Sandbox.Game.Players;
using Sandbox.Game.World;
using VRage.Collections;
using VRage.Components;
using VRage.Components.Entity.CubeGrid;
using VRage.Definitions;
using VRage.Engine;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;

namespace Meds.Wrapper.Utils
{
    public struct PackagePayload
    {
        public string Name;
        public ulong? ModId;

        public PackagePayload(IApplicationPackage package)
        {
            Name = package.Name;
            ModId = (package as MyModContext)?.WorkshopItem?.Id;
        }

        public PackagePayload(Type type) : this(PackageForAssembly(type?.Assembly))
        {
        }

        private static readonly ConcurrentDictionary<Assembly, IApplicationPackage> PackageForAssemblyCache =
            new ConcurrentDictionary<Assembly, IApplicationPackage>();

        public static IApplicationPackage PackageForAssembly(Assembly asm)
        {
            var mods = MySession.Static?.ModManager?.Assemblies ?? DictionaryReader<MyModContext, Assembly>.Empty;
            if (asm == null || mods.Count == 0)
                return MyModContext.BaseGame;
            return PackageForAssemblyCache.GetOrAdd(asm, asmVal =>
            {
                foreach (var mod in mods)
                    if (mod.Value == asmVal)
                        return mod.Key;
                return MyModContext.BaseGame;
            });
        }
    }


    public struct MemberPayload
    {
        public PackagePayload Package;
        public string Type;
        public string Member;

        public MemberPayload(MemberInfo member) : this(member.DeclaringType, member.Name)
        {
        }

        public MemberPayload(Type declaringType, string member)
        {
            Package = new PackagePayload(declaringType);
            Type = declaringType?.Name;
            Member = member;
        }
    }

    public struct DefinitionPayload
    {
        public PackagePayload Package;
        public string Type;
        public string Subtype;

        public DefinitionPayload(MyObjectBuilder_DefinitionBase ob)
        {
            Package = new PackagePayload(ob.Package);
            Type = ob.Id.TypeIdString;
            Subtype = ob.SubtypeName;
        }

        public DefinitionPayload(MyDefinitionBase ob)
        {
            Package = new PackagePayload(ob.Package);
            Type = ob.Id.TypeId.ShortName;
            Subtype = ob.Id.SubtypeName;
        }
    }

    public struct DefinitionLoadingPayload
    {
        public PackagePayload Package;
        public string Type;
        public string Subtype;
        public string Path;

        public DefinitionLoadingPayload(MyDefinitionLoader.LogMessage ob)
        {
            Package = new PackagePayload(ob.Package);
            Type = ob.Id.TypeIdString;
            Subtype = ob.Id.SubtypeName;
            Path = ob.DefinitionPath;
        }
    }

    public struct ComponentPayload
    {
        public PackagePayload Package;
        public string Type;
        public string Method;

        public ComponentPayload(IComponent comp, string method = null)
        {
            Package = new PackagePayload(comp.GetType());
            Type = comp.GetType().Name;
            Method = method;
        }
    }

    public class EntityPayload
    {
        public long? EntityId;
        public long? EntityRootId;
        public string EntitySubtype;
        public PositionPayload Position;
        public int? BlockCount;

        public EntityPayload(MyEntity entity)
        {
            EntityId = entity.EntityId;
            EntityRootId = entity.GetTopMostParent()?.EntityId;
            EntitySubtype = entity.DefinitionId?.SubtypeName;
            PositionPayload.TryCreate(entity.GetPosition(), out Position);
            if (entity.Components.TryGet(out MyGridDataComponent grid))
                BlockCount = grid.BlockCount;
        }
    }

    public class EntityComponentPayload
    {
        public PackagePayload Package;
        public string Type;
        public string Method;
        public long? EntityId;
        public long? EntityRootId;
        public string EntitySubtype;
        public DefinitionPayload? Definition;
        public PlayerPayload? Player;

        public EntityComponentPayload(MyEntityComponent comp, string method = null,
            MyEntityComponentContainer container = null)
        {
            container ??= comp.Container;
            Package = new PackagePayload(comp.GetType());
            Type = comp.GetType().Name;
            Method = method;
            EntityId = container?.Entity?.EntityId;
            EntityRootId = container?.Entity?.GetTopMostParent()?.EntityId;
            EntitySubtype = container?.Entity?.DefinitionId?.SubtypeName;
            if (DefinitionForObject.TryGet(comp, out var def))
                Definition = new DefinitionPayload(def);
            else
                Definition = null;
            var holdingPlayer = MyPlayers.Static?.GetControllingPlayer(comp.Entity);
            Player = holdingPlayer != null ? (PlayerPayload?)PlayerPayload.Create(holdingPlayer) : null;
        }
    }

    public class HandItemBehaviorPayload
    {
        public PackagePayload Package;
        public string Type;
        public string Method;
        public long? EntityId;
        public string EntitySubtype;
        public DefinitionPayload? Definition;
        public PlayerPayload? Player;

        public HandItemBehaviorPayload(MyHandItemBehaviorBase tool, string method = null)
        {
            Package = new PackagePayload(tool.GetType());
            Type = tool.GetType().Name;
            Method = method;
            EntityId = tool.Holder?.EntityId;
            EntitySubtype = tool.Holder?.DefinitionId?.SubtypeName;
            if (DefinitionForObject.TryGet(tool, out var def))
                Definition = new DefinitionPayload(def);
            else
                Definition = null;
            var holdingPlayer = MyPlayers.Static?.GetControllingPlayer(tool.Holder);
            Player = holdingPlayer != null ? (PlayerPayload?)PlayerPayload.Create(holdingPlayer) : null;
        }
    }

    public struct PositionPayload
    {
        public byte Face;
        public double X;
        public double Y;
        public bool? Permitted;

        public double Lng;
        public double Lat;
        public double Elevation;

        public static bool TryCreate(MyPlayer player, out PositionPayload payload)
        {
            payload = default;
            return player.ControlledEntity != null &&
                   TryCreate(player.ControlledEntity.GetPosition(), out payload, player.Identity?.Id);
        }

        public static bool TryCreate(Vector3D position, out PositionPayload payload, long? identity = null)
        {
            var planet = MyGamePruningStructureSandbox.GetClosestPlanet(position);
            var areas = planet?.Get<MyPlanetAreasComponent>();
            payload = default;
            if (areas == null)
                return false;
            var localPos = position - planet.GetPosition();

            payload.Elevation = localPos.Normalize() - planet.AverageRadius;
            payload.Lng = MathHelper.ToDegrees(Math.Atan2(-localPos.X, -localPos.Z));
            payload.Lat = MathHelper.ToDegrees(Math.Atan2(localPos.Y,
                Math.Sqrt(localPos.X * localPos.X + localPos.Z * localPos.Z)));
            MyEnvironmentCubemapHelper.ProjectToCube(ref localPos, out var face, out var tex);
            payload.Face = (byte)face;
            var normXy = (tex + 1.0) * 0.5;
            if (normXy.X >= 1.0)
                normXy.X = 0.99999999;
            if (normXy.Y >= 1.0)
                normXy.Y = 0.99999999;
            payload.X = normXy.X * areas.AreaCount;
            payload.Y = normXy.Y * areas.AreaCount;
            var ownership = planet.Get<MyPlanetAreaOwnershipComponent>();
            if (ownership != null && identity != null)
            {
                var areaId = areas.GetArea(localPos);
                var accessor = ownership.GetAreaPermissions(areaId);
                payload.Permitted = accessor.HasPermission(identity.Value);
            }

            return true;
        }
    }

    public interface IPayloadConsumer
    {
        void Consume<T>(in T payload);
    }

    public static class LoggingPayloads
    {
        public static void VisitPayload<T>(Delegate target, T consumer) where T : IPayloadConsumer
        {
            VisitPayload(target.Target, consumer, target.Method.Name);
        }

        public static void VisitPayload<T>(object target, T consumer, string method = null) where T : IPayloadConsumer
        {
            switch (target)
            {
                case MyEntityStatComponent.DelayedEffect delayed:
                    consumer.Consume(new EntityComponentPayload(delayed.StatComponent, method));
                    return;
                case MyEntityComponent ec:
                    consumer.Consume(new EntityComponentPayload(ec, method));
                    return;
                case IComponent c:
                    consumer.Consume(new ComponentPayload(c, method));
                    return;
                case MyHandItemBehaviorBase hib:
                    consumer.Consume(new HandItemBehaviorPayload(hib, method));
                    return;
                default:
                    consumer.Consume(new MemberPayload(target?.GetType(), method));
                    return;
            }
        }
    }
}