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
using TerrariaHooks.MethodSwapUpgraders;

namespace TerrariaHooks {
    class TerrariaHooksMod : Mod {

        readonly static Dictionary<string, MethodSwapUpgrader> Upgraders = new Dictionary<string, MethodSwapUpgrader>();

        static TerrariaHooksConfig Config;

        public TerrariaHooksMod() {
            Properties = new ModProperties() {
            };

            // Load the configuration.

            string configPath = Path.Combine(Main.SavePath, "Mod Configs", "TerrariaHooks.json");

            if (System.IO.File.Exists(configPath)) {
                try {
                    Config = JsonConvert.DeserializeObject<TerrariaHooksConfig>(System.IO.File.ReadAllText(configPath));
                } catch {
                }
            }
            if (Config == null) {
                Config = new TerrariaHooksConfig();
                string configDirPath = Path.GetDirectoryName(configPath);
                if (!Directory.Exists(configDirPath));
                    Directory.CreateDirectory(configDirPath);
                if (System.IO.File.Exists(configPath))
                    System.IO.File.Delete(configPath);
                System.IO.File.WriteAllText(configPath, JsonConvert.SerializeObject(Config));
            }

            // Set up any upgraders.

            if (Config.UpgradeTerrariaOverhaul)
                Upgraders["TerrariaOverhaul"] = new TerrariaOverhaulUpgrader();

            // Load the cecil module generator.
            HookEndpointManager.OnGenerateCecilModule += GenerateCecilModule;

            // All of our loader hooks need to be applied as early as possible.

            // TerrariaHooks is responsible for "upgrading" a few mods with custom method swapping code.
            HookOnAutoload = new Hook(
                typeof(Mod).GetMethod("Autoload", BindingFlags.NonPublic | BindingFlags.Instance),
                typeof(TerrariaHooksMod).GetMethod("OnAutoload", BindingFlags.NonPublic | BindingFlags.Static)
            );

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

        public override void Load() {
            // Autoload is patched before any mod.Load can run.
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

        private static void Upgrade(Mod mod, bool late) {
            if (Upgraders.TryGetValue(mod.Name, out MethodSwapUpgrader upgrader))
                upgrader.Load(mod, late);
        }

        static Hook HookOnAutoload;
        static void OnAutoload(Action<Mod> orig, Mod mod) {
            Upgrade(mod, false);
            orig(mod);
        }

        static Hook HookOnUnloadContent;
        static void OnUnloadContent(Action<Mod> orig, Mod mod) {
            orig(mod);

            // Undo any upgrades performed by TerrariaHooks.
            if (Upgraders.TryGetValue(mod.Name, out MethodSwapUpgrader upgrader))
                upgrader.Unload(mod);

            // Unload any HookGen hooks after unloading the mod.
            HookEndpointManager.RemoveAllOwnedBy(mod.Code);
        }

        static Hook HookOnUnloadAll;
        static void OnUnloadAll(Action orig) {
            orig();

            HookOnAutoload.Dispose();
            HookOnUnloadContent.Dispose();
            HookOnUnloadAll.Dispose();

            HookEndpointManager.OnGenerateCecilModule += GenerateCecilModule;

            Upgraders.Clear();
        }

    }
}
