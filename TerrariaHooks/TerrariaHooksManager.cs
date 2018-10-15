using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.RuntimeDetour.HookGen;
using MonoMod.Utils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Terraria;
using Terraria.ModLoader;
using TerrariaHooks;

namespace TerrariaHooks {
    static partial class TerrariaHooksManager {

        static bool Initialized = false;

        static DetourModManager Manager;

        public static void Init() {
            if (Initialized)
                return;
            Initialized = true;

            Manager = new DetourModManager();
            Manager.Ignored.Add(Assembly.GetExecutingAssembly());

            // Load the cecil module generator.
            // Adding this more than once shouldn't hurt.
            HookEndpointManager.OnGenerateCecilModule += GenerateCecilModule;

            // Some mods might forget to undo their hooks.
            HookOnUnloadContent = new Hook(
                typeof(Mod).GetMethod("UnloadContent", BindingFlags.NonPublic | BindingFlags.Instance),
                typeof(TerrariaHooksManager).GetMethod("OnUnloadContent", BindingFlags.NonPublic | BindingFlags.Static)
            );

            // All of our own hooks need to be undone last.
            HookOnUnloadAll = new Hook(
                typeof(ModLoader).GetMethod("Unload", BindingFlags.NonPublic | BindingFlags.Static),
                typeof(TerrariaHooksManager).GetMethod("OnUnloadAll", BindingFlags.NonPublic | BindingFlags.Static)
            );

            // Try to hook the logger to avoid logging "silent" exceptions thrown by TerrariaHooks.
            MethodBase m_LogSilentException =
                typeof(Mod).Assembly.GetType("Terraria.ModLoader.ModCompile+<>c")
                ?.GetMethod("<ActivateExceptionReporting>b__15_0", BindingFlags.NonPublic | BindingFlags.Instance);
            if (m_LogSilentException != null) {
                HookOnLogSilentException = new Hook(
                    m_LogSilentException,
                    typeof(TerrariaHooksManager).GetMethod("OnLogSilentException", BindingFlags.NonPublic | BindingFlags.Static)
                );
            }
        }

        internal static void Dispose() {
            if (!Initialized)
                return;
            Initialized = false;

            Manager.Dispose();

            HookEndpointManager.OnGenerateCecilModule -= GenerateCecilModule;

            HookOnUnloadContent.Dispose();
            HookOnUnloadAll.Dispose();
        }

        static Hook HookOnUnloadContent;
        static void OnUnloadContent(Action<Mod> orig, Mod mod) {
            orig(mod);
            Manager.Unload(mod.Code);
        }

        static Hook HookOnUnloadAll;
        static void OnUnloadAll(Action orig) {
            orig();

            // Dispose the context.
            Dispose();
        }

        static Hook HookOnLogSilentException;
        static void OnLogSilentException(Action<object, object, FirstChanceExceptionEventArgs> orig, object self, object sender, FirstChanceExceptionEventArgs exceptionArgs) {
            Exception e = exceptionArgs.Exception;
            if (e.TargetSite.Name == "CreateDelegateNoSecurityCheck" ||
                e.TargetSite.Name == "_GetMethodBody" ||
                e.TargetSite.Name == "GetMethodBody")
                return;

            orig(self, sender, exceptionArgs);
        }

        internal static ModuleDefinition GenerateCecilModule(AssemblyName name) {
            AssemblyName UnwrapName(AssemblyName other) {
                int underscore = other.Name.LastIndexOf('_');
                if (underscore == -1)
                    return other;

                other.Name = other.Name.Substring(0, underscore);
                return other;
            }

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
                    ReflectionHelper.AssemblyCache[UnwrapName(mod.Code.GetName()).ToString()] = mod.Code;

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

    }
}
