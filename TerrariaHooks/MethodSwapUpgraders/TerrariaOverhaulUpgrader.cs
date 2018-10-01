using Mono.Cecil.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.RuntimeDetour.HookGen;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Terraria.ModLoader;

namespace TerrariaHooks.MethodSwapUpgraders {
    class TerrariaOverhaulUpgrader : MethodSwapUpgrader {

        readonly static Version TargetVersion = new Version(2, 1, 9, 1);
        const BindingFlags MethodFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Assembly Assembly;
        private static Type t_MethodSwapping;

        private static readonly List<Detour> Swaps = new List<Detour>();
        private static readonly List<Detour> Upgrades = new List<Detour>();

        public override void Load(Mod mod, bool late) {
            if (mod.Version != TargetVersion)
                return;

            Type t = typeof(TerrariaOverhaulUpgrader);

            Assembly = mod.GetType().Assembly;
            t_MethodSwapping = Assembly.GetType("TerrariaOverhaul.MethodSwapping");
            Type t_Debug = Assembly.GetType("TerrariaOverhaul.Debug");

            // Patch MethodSwapping.Unload
            using (DynamicMethodDefinition dmd = new DynamicMethodDefinition(t_MethodSwapping.GetMethod("Unload"), HookEndpointManager.GenerateCecilModule)) {
                new HookIL(dmd.Definition).Invoke(il => {
                    HookILCursor c = il.At(0);

                    // Replace the SwapMethods call on Unload with our custom UndoSwaps.
                    if (c.GotoNext(
                        i => i.MatchCall("TerrariaOverhaul.MethodSwapping", "SwapMethods")
                    )) {
                        c.Remove();
                        c.Emit(OpCodes.Call, typeof(TerrariaOverhaulUpgrader).GetMethod("UndoSwaps"));
                    }
                });

                Upgrades.Add(new Detour(dmd.Method, dmd.Generate()));
            }

            // Replace MethodSwapUnsafe and MethodSwap
            Upgrades.Add(new Detour(
                t_MethodSwapping.GetMethod("MethodSwapUnsafe"),
                t.GetMethod("MethodSwapUnsafe")
            ));
            Upgrades.Add(new Detour(
                t_MethodSwapping.GetMethod("MethodSwap"),
                t.GetMethod("MethodSwap")
            ));

            // While we're at it, detour Log to Debug.Log
            Upgrades.Add(new Detour(
                t.GetMethod("Log"),
                t_Debug.GetMethod("Log")
            ));
        }
        
        public override void Unload(Mod mod) {
            foreach (Detour detour in Upgrades)
                detour.Dispose();
            Upgrades.Clear();
        }

        public static void UndoSwaps() {
            foreach (Detour detour in Swaps)
                detour.Dispose();
            Swaps.Clear();
        }

        public static void MethodSwapUnsafe(Type type, string original, string injection) {
            // This doesn't seem to get used, but let's just upgrade it as well.
            Swaps.Add(new Detour(
                type.GetMethod(original, MethodFlags),
                t_MethodSwapping.GetMethod(injection, MethodFlags)
            ));
        }

        public static void MethodSwap(Type type, string original, string injection, bool nonStatic = false, Type[] parameters = null) {
            MethodBase from, to;
            if (parameters == null) {
                from = type.GetMethod(original, MethodFlags);
                to = t_MethodSwapping.GetMethod(injection, MethodFlags);

            } else {
                from = type.GetMethod(original, MethodFlags, null, parameters, null);
                if (nonStatic) {
                    // Non-static methods pass "this" as a hidden first parameter, shifting all other params by one.
                    Type[] parametersOrig = parameters;
                    parameters = new Type[parameters.Length + 1];
                    parameters[0] = type;
                    parametersOrig.CopyTo(parameters, 1);
                }
                to = t_MethodSwapping.GetMethod(injection, MethodFlags, null, parameters, null);
            }

            Detour detour = new Detour(from, to);
            Swaps.Add(detour);

            // Method swaps use a janky method to invoke the original method.
            // Patch them to use the orig trampoline instead, hooking them up into the RuntimeDetour detour chain.
            MethodBase trampoline = detour.GenerateTrampoline();
            using (DynamicMethodDefinition dmd = new DynamicMethodDefinition(to, HookEndpointManager.GenerateCecilModule)) {
                new HookIL(dmd.Definition).Invoke(il => {
                    HookILCursor c = il.At(0);

                    // Replace any call*s to the "from" method to a call to the trampoline.
                    while (c.GotoNext(
                        i => i.MatchCall(from) || i.MatchCallvirt(from)
                    )) {
                        c.Remove();
                        c.Emit(OpCodes.Call, trampoline);
                    }
                });

                Upgrades.Add(new Detour(dmd.Method, dmd.Generate()));
            }

            Log($"Swapped {type.Name}.{original} with {t_MethodSwapping.Name}.{injection} using RuntimeDetour");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Log(object text, bool removeCopies = false, bool toChat = false, int stackframeOffset = 0) {
            throw new InvalidProgramException("This method was supposed to get detoured to Debug.Log!");
        }

    }
}
