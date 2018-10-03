using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

public static class TerrariaHooksBoot {

    private static readonly FieldInfo f_AssemblyManager_loadedMods =
        typeof(Mod).Assembly
        .GetType("Terraria.ModLoader.AssemblyManager")
        .GetField("loadedMods", BindingFlags.NonPublic | BindingFlags.Static);
    private static IDictionary LoadedMods =>
        f_AssemblyManager_loadedMods.GetValue(null) as IDictionary;

    private static readonly FieldInfo f_AssemblyManager_loadedAssemblies =
        typeof(Mod).Assembly
        .GetType("Terraria.ModLoader.AssemblyManager")
        .GetField("loadedAssemblies", BindingFlags.NonPublic | BindingFlags.Static);
    private static IDictionary<string, Assembly> LoadedAssemblies =>
        f_AssemblyManager_loadedAssemblies.GetValue(null) as IDictionary<string, Assembly>;

    private static readonly FieldInfo f_LoadedMod_modFile =
        typeof(Mod).Assembly
        .GetType("Terraria.ModLoader.AssemblyManager+LoadedMod")
        .GetField("modFile", BindingFlags.Public | BindingFlags.Instance);
    private static TmodFile GetModFile(object loadedMod) =>
        f_LoadedMod_modFile.GetValue(loadedMod) as TmodFile;

    private static readonly MethodInfo m_LoadedMod_EncapsulateReferences =
        typeof(Mod).Assembly
        .GetType("Terraria.ModLoader.AssemblyManager+LoadedMod")
        .GetMethod("EncapsulateReferences", BindingFlags.NonPublic | BindingFlags.Instance);
    private static byte[] EncapsulateReferences(object loadedMod, byte[] code) =>
        m_LoadedMod_EncapsulateReferences.Invoke(loadedMod, new object[] { code }) as byte[];

    private static readonly MethodInfo m_AssemblyManager_LoadAssembly =
        typeof(Mod).Assembly
        .GetType("Terraria.ModLoader.AssemblyManager")
        .GetMethod("LoadAssembly", BindingFlags.NonPublic | BindingFlags.Static);
    private static Assembly LoadAssembly(byte[] code, byte[] pdb = null) =>
        m_AssemblyManager_LoadAssembly.Invoke(null, new object[] { code, pdb }) as Assembly;

    public static void Init(Mod mod) {
        // Check if the dependency is already met.
        string prefixEncapsulated = $"{mod.Name}_TerrariaHooks_";
        Assembly asm = LoadedAssemblies.FirstOrDefault(kvp => kvp.Key.StartsWith(prefixEncapsulated)).Value;

        if (asm == null) {
            // Dependency not met - load it.
            object loadedMod = null;
            foreach (object value in LoadedMods.Values) {
                if (GetModFile(value).name == mod.Name) {
                    loadedMod = value;
                    break;
                }
            }

            foreach (string path in new string[] {
                $"lib/TerrariaHooks.{(ModLoader.windows ? "Windows" : "Mono")}.dll",
                "lib/TerrariaHooks.dll"
            }) {
                byte[] code = mod.File.GetFile(path);
                if (code == null)
                    continue;

                LoadAssembly(EncapsulateReferences(loadedMod, code));
                break;
            }
        }

        // Invoke TerrariaHooksContext.Init(mod) in the proper assembly.
        asm.GetType("TerrariaHooksContext").GetMethod("Init").Invoke(null, new object[] { mod });
    }

}
