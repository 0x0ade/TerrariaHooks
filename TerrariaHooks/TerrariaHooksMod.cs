using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.RuntimeDetour.HookGen;
using MonoMod.Utils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaHooks {
    class TerrariaHooksMod : Mod {

        public TerrariaHooksMod() {
            Properties = new ModProperties() {
            };

            // Load the cecil module generator.
            HookEndpointManager.OnGenerateCecilModule += GenerateCecilModule;

            // All of our loader hooks need to be applied as early as possible.

            ModCompilerHook.Hook(typeof(Mod).Assembly);

            // Some mods might forget to undo their hooks.
            HookOnUnloadContent = new Hook(
                typeof(Mod).GetMethod("UnloadContent", BindingFlags.NonPublic | BindingFlags.Instance),
                typeof(TerrariaHooksMod).GetMethod("OnUnloadContent", BindingFlags.NonPublic | BindingFlags.Static)
            );

            // All of our own hooks need to be undone last.
            HookOnUnloadAll = new Hook(
                typeof(ModLoader).GetMethod("Unload", BindingFlags.NonPublic | BindingFlags.Static),
                typeof(TerrariaHooksMod).GetMethod("OnUnloadAll", BindingFlags.NonPublic | BindingFlags.Static)
            );
        }

        private static ModuleDefinition GenerateCecilModule(AssemblyName name) {
            // Terraria ships with some dependencies as embedded resources.
            string resourceName = name.Name + ".dll";
            resourceName = Array.Find(typeof(Program).Assembly.GetManifestResourceNames(), element => element.EndsWith(resourceName));
            if (resourceName != null) {
                using (Stream stream = typeof(Program).Assembly.GetManifestResourceStream(resourceName))
                    // Read immediately, as the stream isn't open forever.
                    return ModuleDefinition.ReadModule(stream, new ReaderParameters(ReadingMode.Immediate));
            }

            // Mod .dlls exist in the mod's .tmod containers.
            string nameStr = name.ToString();
            foreach (Mod mod in ModLoader.LoadedMods) {
                if (mod.Code == null || mod.File == null)
                    continue;

                // Check if it's the main assembly.
                if (mod.Code.GetName().ToString() == nameStr) {
                    // Let's unwrap the name and cache it for all DMDs as well.
                    // tModLoader changes the assembly name to allow mod updates, which breaks Assembly.Load
                    DynamicMethodDefinition.AssemblyCache[UnwrapName(mod.Code.GetName()).ToString()] = mod.Code;

                    using (MemoryStream stream = new MemoryStream(mod.File.GetMainAssembly()))
                        // Read immediately, as the stream isn't open forever.
                        return ModuleDefinition.ReadModule(stream, new ReaderParameters(ReadingMode.Immediate));
                }

                // Check if the assembly is possibly included in the .tmod
                if (!mod.Code.GetReferencedAssemblies().Any(other => UnwrapName(other).ToString() == nameStr))
                    continue;

                // Try to load lib/Name.dll
                byte[] data;
                if ((data = mod.File.GetFile($"lib/{name.Name}.dll")) != null)
                    using (MemoryStream stream = new MemoryStream(data))
                        // Read immediately, as the stream isn't open forever.
                        return ModuleDefinition.ReadModule(stream, new ReaderParameters(ReadingMode.Immediate));
            }

            return null;
        }

        private static AssemblyName UnwrapName(AssemblyName name) {
            int underscore = name.Name.LastIndexOf('_');
            if (underscore == -1)
                return name;

            name.Name = name.Name.Substring(0, underscore);
            return name;
        }

        static Hook HookOnUnloadContent;
        static void OnUnloadContent(Action<Mod> orig, Mod mod) {
            orig(mod);

            // Unload any HookGen hooks after unloading the mod.
            HookEndpointManager.RemoveAllOwnedBy(mod.Code);
        }

        static Hook HookOnUnloadAll;
        static void OnUnloadAll(Action orig) {
            orig();

            HookOnUnloadContent.Dispose();
            HookOnUnloadAll.Dispose();

            HookEndpointManager.OnGenerateCecilModule += GenerateCecilModule;
        }

    }
}
