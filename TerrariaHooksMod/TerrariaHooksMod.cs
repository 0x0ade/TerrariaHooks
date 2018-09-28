using Mono.Cecil;
using MonoMod.RuntimeDetour;
using MonoMod.RuntimeDetour.HookGen;
using System;
using System.IO;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaHooks {
    class TerrariaHooksMod : Mod {

        public TerrariaHooksMod() {
            Properties = new ModProperties() {
            };

            // Required because Terraria ships with some dependencies as embedded resources.
            HookEndpointManager.OnGenerateCecilModule += name => {
                string resourceName = name.Name + ".dll";
                resourceName = Array.Find(typeof(Program).Assembly.GetManifestResourceNames(), element => element.EndsWith(resourceName));
                if (resourceName == null)
                    return null;
                using (Stream stream = typeof(Program).Assembly.GetManifestResourceStream(resourceName))
                    // Read immediately, as the stream isn't open forever.
                    return ModuleDefinition.ReadModule(stream, new ReaderParameters(ReadingMode.Immediate));
            };

            // Required because TML allows unloading mods, and some mods might forget to undo their hooks.
            // Note that we can't use HookGen to hook onto TML, but we've still got Hook.
            HookOnUnloadContent = new Hook(
                typeof(Mod).GetMethod("UnloadContent", BindingFlags.NonPublic | BindingFlags.Instance),
                typeof(TerrariaHooksMod).GetMethod("OnUnloadContent", BindingFlags.NonPublic | BindingFlags.Static)
            );
        }

        static Hook HookOnUnloadContent;
        static void OnUnloadContent(Action<Mod> orig, Mod mod) {
            // Unload any HookGen hooks after unloading the mod.
            orig(mod);
            HookEndpointManager.RemoveAllOwnedBy(mod.Code);
        }

    }
}
