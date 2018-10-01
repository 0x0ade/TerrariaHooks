using MonoMod.RuntimeDetour;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace TerrariaHooks {
    // This code is shared between TerrariaHooks and the server wrapper.
    // It this can't use HookIL to inject anyting inside of CompileMod...
    static class ModCompilerHook {

        public static bool Hooked {
            get => true.Equals(AppDomain.CurrentDomain.GetData("TerrariaHooks.ModCompilerHook.Hooked"));
            private set => AppDomain.CurrentDomain.SetData("TerrariaHooks.ModCompilerHook.Hooked", value);
        }

        public static void Hook(Assembly asm) {
            if (Hooked)
                return;
            Hooked = true;

            HookOnCompileMod = new Hook(
                asm.GetType("Terraria.ModLoader.ModCompile").GetMethod("CompileMod", BindingFlags.NonPublic | BindingFlags.Static),
                typeof(ModCompilerHook).GetMethod("OnCompileMod", BindingFlags.NonPublic | BindingFlags.Static)
            );
            HookOnRoslynCompile = new Hook(
                asm.GetType("Terraria.ModLoader.ModCompile").GetMethod("RoslynCompile", BindingFlags.NonPublic | BindingFlags.Static),
                typeof(ModCompilerHook).GetMethod("OnRoslynCompile", BindingFlags.NonPublic | BindingFlags.Static)
            );
        }

        public static void Unhook() {
            if (!Hooked)
                return;
            Hooked = false;

            HookOnCompileMod?.Dispose();
            HookOnCompileMod = null;
            HookOnRoslynCompile?.Dispose();
            HookOnRoslynCompile = null;
        }

        static bool CompilingForWindows;

        delegate void orig_CompileMod(object mod, object refMods, bool forWindows, ref byte[] dll, ref byte[] pdb);
        static Hook HookOnCompileMod;
        static void OnCompileMod(
            orig_CompileMod orig,
            object mod, object refMods, bool forWindows, ref byte[] dll, ref byte[] pdb
        ) {
            // Grab all relevant info, f.e. if the mod is compiled for Windows or not.
            CompilingForWindows = forWindows;

            orig(mod, refMods, forWindows, ref dll, ref pdb);
        }

        static Hook HookOnRoslynCompile;
        static CompilerResults OnRoslynCompile(
            Func<CompilerParameters, string[], CompilerResults> orig,
            CompilerParameters compileOptions, string[] files
        ) {
            // Modify ReferencedAssemblies to take .Mono / .Windows variants into account.

            string suffix = CompilingForWindows ? ".Windows.dll" : ".Mono.dll";

            string[] paths = new string[compileOptions.ReferencedAssemblies.Count];
            compileOptions.ReferencedAssemblies.CopyTo(paths, 0);
            compileOptions.ReferencedAssemblies.Clear();

            for (int i = 0; i < paths.Length; i++) {
                string path = paths[i];
                path = path.Substring(0, path.Length - 4) + suffix;
                if (File.Exists(path))
                    paths[i] = path;
            }

            compileOptions.ReferencedAssemblies.AddRange(paths);

            return orig(compileOptions, files);
        }

    }
}
