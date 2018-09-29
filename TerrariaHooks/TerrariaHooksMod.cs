using Mono.Cecil;
using MonoMod.RuntimeDetour;
using MonoMod.RuntimeDetour.HookGen;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaHooks {
    class TerrariaHooksMod : Mod {

        static TerrariaHooksConfig Config;

        public TerrariaHooksMod() {
            Properties = new ModProperties() {
            };

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

            // Some mods ship with their own detour ("method swapping") implementations.
            // Those conflict with each other, and with RuntimeDetour in TerrariaHooks.
            // TODO
        }

        static Hook HookOnUnloadContent;
        static void OnUnloadContent(Action<Mod> orig, Mod mod) {
            // Undo any fixes performed by TerrariaHooks.
            // TODO

            orig(mod);

            // Unload any HookGen hooks after unloading the mod.
            HookEndpointManager.RemoveAllOwnedBy(mod.Code);
        }

    }
}
