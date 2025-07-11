using System.Collections.Generic;
using Medieval.Definitions.Crafting;
using Medieval.Entities.Components.Crafting;
using Meds.Metrics;
using Meds.Shared;
using Meds.Wrapper.Shim;
using VRage.Game;
using VRage.Utils;

namespace Meds.Wrapper.Metrics
{
    public class CraftingMetrics
    {
        private static bool _allCraftingCategories;
        private static readonly HashSet<MyStringHash> Categories = new HashSet<MyStringHash>(MyStringHash.Comparer);

        private static bool _allCraftingComponents;
        private static readonly HashSet<MyStringHash> Components = new HashSet<MyStringHash>(MyStringHash.Comparer);


        public static void Register(MetricConfig config)
        {
            _allCraftingCategories = config.AllCraftingCategories;
            if (config.CraftingCategories != null)
                foreach (var category in config.CraftingCategories)
                    Categories.Add(MyStringHash.GetOrCompute(category));

            _allCraftingComponents = config.AllCraftingComponents;
            if (config.CraftingComponents != null)
                foreach (var component in config.CraftingComponents)
                    Components.Add(MyStringHash.GetOrCompute(component));

            if ((_allCraftingCategories || Categories.Count > 0) && (_allCraftingComponents || Components.Count > 0))
            {
                MyCraftingComponent.OnCraftingFinished += CraftingFinished;
            }
        }

        private static bool ShouldTrack(MyCraftingComponent block)
        {
            if (block == null)
                return false;
            return _allCraftingComponents || Components.Contains(block.SubtypeId);
        }

        private static bool ShouldTrack(MyCraftingRecipeDefinition definition)
        {
            if (definition == null)
                return false;
            if (_allCraftingCategories)
                return true;
            foreach (var category in definition.Categories)
                if (Categories.Contains(category))
                    return true;
            return false;
        }

        private static void CraftingFinished(MyCraftingComponent craftingComponent, MyCraftingRecipeDefinition recipe, CraftingAuthor? author)
        {
            if (!ShouldTrack(craftingComponent) || !ShouldTrack(recipe))
                return;
            var crafterId = PatchHelper.SubtypeOrDefault(craftingComponent.DefinitionId);
            var recipeId = PatchHelper.SubtypeOrDefault(recipe.Id);
            var name = MetricName.Of(
                "me.crafting",
                "crafter", crafterId,
                "recipe", recipeId);

            void LogItem(MyDefinitionId item, string type, int delta) =>
                MetricRegistry.Group(name.WithTag("item", PatchHelper.SubtypeOrDefault(item))).Counter(type).Inc(delta);

            foreach (var input in recipe.Prerequisites)
                LogItem(input.Id, "input", input.Amount);

            foreach (var output in recipe.Results)
                LogItem(output.Id, "output", output.Amount);

            MetricRegistry.Group(name).Counter("count").Inc();
        }
    }
}