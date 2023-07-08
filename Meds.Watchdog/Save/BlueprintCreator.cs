using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Xml;

namespace Meds.Watchdog.Save
{
    public static class BlueprintCreator
    {
        public static XmlDocument CreateBlueprint(
            string name,
            SaveFileAccessor save,
            GridDatabaseConfig gridDatabase,
            IEnumerable<EntityId> entities,
            IEnumerable<GroupId> groups,
            List<EntityId> failedEntities,
            List<GroupId> failedGroups)
        {
            var doc = new XmlDocument();
            doc.CreateXmlDeclaration();
            var root = doc.CreateElement("Blueprint");
            doc.AppendChild(root);

            if (!save.TryGetConfig(out var config))
                config = null;

            doc.CreateElement(root, "Version", config?.Version ?? SaveConfigAccessor.DefaultVersion);
            doc.CreateElement(root, "Name", name);
            doc.CreateElement(root, "Author", save.Save.SaveName);
            doc.CreateElement(root, "Description", $"Saved at {save.Save.TimeUtc} UTC");
            doc.CreateElement(root, "WorkshopId", "0");
            doc.CreateElement(root, "CreatorId", "0");
            var subScene = doc.CreateElement(root, "SubScene");
            CreateScene(subScene, save, gridDatabase, entities, groups, failedEntities, failedGroups);
            return doc;
        }

        public static void CreateXmlDeclaration(this XmlDocument doc)
        {
            var declaration = doc.CreateXmlDeclaration("1.0", "", "");
            doc.AppendChild(declaration);
        }

        public static void CreateScene(
            XmlElement subScene,
            SaveFileAccessor save,
            GridDatabaseConfig gridDatabase,
            IEnumerable<EntityId> entities,
            IEnumerable<GroupId> groups,
            List<EntityId> failedEntities,
            List<GroupId> failedGroups)
        {
            var doc = subScene.OwnerDocument!;

            var bounds = new BoundingBox
            {
                Min = new Vector3(float.PositiveInfinity),
                Max = new Vector3(float.NegativeInfinity),
            };
            foreach (var entityId in entities)
                if (save.TryGetEntity(entityId, out var entity))
                {
                    subScene.AppendChild(doc.ImportNode(entity.Entity, true));
                    if (entity.ChunkData.HasValue)
                        bounds.Include(entity.ChunkData.Value.WorldBounds(gridDatabase));
                    bounds.Include(entity.Position.Position);
                }
                else
                {
                    failedEntities.Add(entityId);
                }

            foreach (var groupId in groups)
                if (save.TryGetGroup(groupId, out var group))
                {
                    subScene.AppendChild(doc.ImportNode(group.Group, true));
                    if (group.ChunkData.HasValue)
                        bounds.Include(group.ChunkData.Value.WorldBounds(gridDatabase));
                }
                else
                {
                    failedGroups.Add(groupId);
                }

            var xmlBounds = doc.CreateElement(subScene, "BoundingBox");
            xmlBounds.AppendChild(doc.CreateElement("Min").AttributesFrom(bounds.Min));
            xmlBounds.AppendChild(doc.CreateElement("Max").AttributesFrom(bounds.Max));
            var sphere = BoundingSphere.From(bounds);
            var xmlSphere = doc.CreateElement(subScene, "BoundingSphere");
            xmlSphere.AppendChild(doc.CreateElement("Center").AttributesFrom(sphere.Center));
            doc.CreateElement(xmlSphere, "Radius", sphere.Radius.ToString(CultureInfo.InvariantCulture));
        }
    }
}