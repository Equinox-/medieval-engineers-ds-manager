using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Medieval.GameSystems.Tools;
using Meds.Wrapper.Audit;
using Sandbox.Game.EntityComponents.Character;
using VRage.Components.Interfaces;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Utils;
using ZLogger;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    public static class PaxProjectileAttributionPatch
    {
        [ThreadStatic]
        private static WeakReference<MyEntity> _shooter;

        private static MyEntity Shooter
        {
            get => _shooter != null && _shooter.TryGetTarget(out var entity) ? entity : null;
            set
            {
                var shooter = _shooter;
                if (shooter == null) _shooter = new WeakReference<MyEntity>(value);
                else shooter.SetTarget(value);
            }
        }

        private static bool SharedPrepare(out List<MethodInfo> methods, Dictionary<string, string[]> typeToMethod,
            Predicate<MethodInfo> methodPredicate = null, bool isStatic = false, Dictionary<Type, string[]> vanillaMethods = null)
        {
            methods = typeToMethod.SelectMany(typeAndMethods =>
                    PatchHelper.ModTypes(typeAndMethods.Key).SelectMany(item => typeAndMethods.Value.SelectMany(name => MethodsInType(item.type, name))))
                .Concat((vanillaMethods ?? new Dictionary<Type, string[]>()).SelectMany(entry => entry.Value.SelectMany(name => MethodsInType(entry.Key, name))))
                .ToList();
            return methods.Count > 0;

            List<MethodInfo> MethodsInType(Type type, string name)
            {
                var methods = AccessTools.GetDeclaredMethods(type)
                    .Where(x => x.IsStatic == isStatic && name == x.Name && (methodPredicate == null || methodPredicate(x)))
                    .ToList();
                if (methods.Count == 0)
                    Entrypoint.LoggerFor(typeof(PaxProjectileAttributionPatch)).ZLogInformation(
                        "In type {0} the {1} method wasn't found", type, name);
                return methods;
            }
        }

        [HarmonyPatch]
        [AlwaysPatch(Late = true)]
        public static class ApplyAttributionComponent
        {
            private static List<MethodInfo> _methods;

            public static bool Prepare() =>
                SharedPrepare(out _methods, new Dictionary<string, string[]>
                {
                    ["Pax.Cannons.MyPAX_CustomProjectile"] = new[] { "OnAddedToScene" },
                    ["Pax.Cannons.MyPAX_MortarBomb"] = new[] { "OnAddedToScene" },
                    ["Pax.Cannons.MyPAX_MachineGun"] = new[] { "FIRE", "Use" },
                    ["Pax.Cannons.MyPAX_Cannon"] = new[] { "RemoteControl", "Use" },
                });

            public static IEnumerable<MethodBase> TargetMethods() => _methods;

            public static void Prefix(MyEntityComponent __instance)
            {
                var shooter = Shooter ?? AuditPayload.GetActingPlayer()?.ControlledEntity;
                if (shooter == null) return;
                MedsDamageAttributionComponent.Apply(__instance.Container, shooter);
            }
        }

        [HarmonyPatch]
        [AlwaysPatch(Late = true)]
        public static class PropagateFromEntityComponent
        {
            private static List<MethodInfo> _methods;

            public static bool Prepare() => SharedPrepare(out _methods,
                new Dictionary<string, string[]>
                {
                    ["Pax.Cannons.MyPAX_CustomProjectile"] = new[]
                    {
                        "DoCharacterDamage",
                        "DoGridBlockDamage",
                        "DoSpallDamage",
                        "PhysicsCalculation"
                    },
                    ["Pax.Cannons.MyPAX_MortarBomb"] = new[] { "Explode", "ShrapnelSimulation" },
                });

            public static IEnumerable<MethodBase> TargetMethods() => _methods;

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var find = AccessTools.Constructor(typeof(MyDamageInformation),
                    new[] { typeof(float), typeof(MyStringHash), typeof(MyEntity), typeof(MyHitInfo?) });
                var replace = AccessTools.Method(typeof(PropagateFromEntityComponent), nameof(CreateDamageInformationReplacement));
                foreach (var instruction in instructions)
                {
                    if (instruction.opcode == OpCodes.Newobj && instruction.operand as ConstructorInfo == find)
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return instruction.ChangeInstruction(OpCodes.Call, replace);
                        continue;
                    }

                    yield return instruction;
                }
            }

            public static MyDamageInformation CreateDamageInformationReplacement(float amount, MyStringHash type, MyEntity attacker, MyHitInfo? hitInfo,
                MyEntityComponent customProjectile)
            {
                var info = new MyDamageInformation(amount, type, attacker ?? customProjectile.Entity, hitInfo);
                return info;
            }
        }

        [HarmonyPatch]
        [AlwaysPatch(Late = true)]
        public static class AttributeFromItemBehavior
        {
            private static List<MethodInfo> _methods;

            public static bool Prepare() =>
                SharedPrepare(out _methods,
                    new Dictionary<string, string[]>
                    {
                        ["Pax.Cannons.MyPAX_HandheldGun"] = new[] { "Shoot" },
                        ["Pax.Cannons.MyPAX_ThrowableItem"] = new[] { "StartAction" },
                    },
                    vanillaMethods: new Dictionary<Type, string[]>
                    {
                        [typeof(MyEntityPlacerBehavior)] = new[] { "Hit" }
                    });

            public static IEnumerable<MethodBase> TargetMethods() => _methods;

            public static void Prefix(MyHandItemBehaviorBase __instance, out MyEntity __state)
            {
                __state = Shooter;
                Shooter = __instance.Holder;
            }

            public static void Postfix(MyEntity __state) => Shooter = __state;
        }

        [HarmonyPatch]
        [AlwaysPatch(Late = true)]
        public static class AttributeFromEntityComponent
        {
            private static List<MethodInfo> _methods;

            public static bool Prepare() => SharedPrepare(out _methods,
                new Dictionary<string, string[]>
                {
                    ["Pax.RangedDefenders.MyPAX_ShootingDefender"] = new[] { "ShootCustomProjectile" },
                    ["Pax.Cannons.MyPAX_MachineGun"] = new[] { "FireBullet" },
                    ["Pax.Cannons.MyPAX_Cannon"] = new[] { "ShootCustomProjectile" },
                });

            public static IEnumerable<MethodBase> TargetMethods() => _methods;

            public static void Prefix(MyEntityComponent __instance, out MyEntity __state)
            {
                __state = Shooter;
                Shooter = __instance.Entity;
            }

            public static void Postfix(MyEntity __state) => Shooter = __state;
        }
    }
}