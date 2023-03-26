using System;
using System.Collections.Concurrent;
using System.Reflection;
using Sandbox.Definitions.Equipment;
using Sandbox.Game.EntityComponents.Character;
using VRage.Components;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Components.Session;
using VRage.Session;

namespace Meds.Wrapper.Utils
{
    public static class DefinitionForObject
    {
        private static readonly ConcurrentDictionary<Type, Func<object, MyDefinitionBase>> DefinitionForType =
            new ConcurrentDictionary<Type, Func<object, MyDefinitionBase>>();

        private static readonly string[] LikelyNames = { "_definition", "m_definition", "_def", "m_def", "Definition" };

        public static bool TryGet(object obj, out MyDefinitionBase def)
        {
            if (obj == null)
            {
                def = null;
                return false;
            }

            var type = obj.GetType();
            var getter = DefinitionForType.GetOrAdd(type, captured =>
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var definitionType = DefinitionTypeFor(type);
                foreach (var name in LikelyNames)
                {
                    var field = captured.GetField(name, flags);
                    if (field != null && definitionType.IsAssignableFrom(field.FieldType))
                        return val => (MyDefinitionBase)field.GetValue(val);
                    var prop = captured.GetProperty(name, flags)?.GetMethod;
                    if (prop != null && definitionType.IsAssignableFrom(prop.ReturnType))
                        return val => (MyDefinitionBase)prop.Invoke(val, Array.Empty<object>());
                }

                return val => null;
            });

            def = getter(obj);
            return def != null;
        }

        private static Type DefinitionTypeFor(Type type)
        {
            if (typeof(MyEntityComponent).IsAssignableFrom(type))
                return typeof(MyEntityComponentDefinition);
            if (typeof(MyHandItemBehaviorBase).IsAssignableFrom(type))
                return typeof(MyHandItemBehaviorDefinition);
            // ReSharper disable once ConvertIfStatementToReturnStatement
            if (typeof(MySessionComponent).IsAssignableFrom(type))
                return typeof(MySessionComponentDefinition);
            return typeof(MyDefinitionBase);
        }
    }
}