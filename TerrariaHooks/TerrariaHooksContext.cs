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
using System.Text;
using Terraria;
using Terraria.ModLoader;
using TerrariaHooks;

static partial class TerrariaHooksContext {

    static readonly HashSet<Mod> Mods = new HashSet<Mod>();
    static bool Initialized = false;

    static readonly Dictionary<Assembly, List<IDetour>> OwnedDetourLists = new Dictionary<Assembly, List<IDetour>>();

    public static void Init(Mod mod) {
        if (Mods.Contains(mod))
            throw new InvalidOperationException("TerrariaHooksContext.Init cannot run more than once per mod without unloading!");

        if (Initialized)
            return;
        Initialized = true;

        // Load the cecil module generator.
        // Adding this more than once shouldn't hurt.
        HookEndpointManager.OnGenerateCecilModule += GenerateCecilModule;

        // Some mods might forget to undo their hooks.
        HookOnUnloadContent = new Hook(
            typeof(Mod).GetMethod("UnloadContent", BindingFlags.NonPublic | BindingFlags.Instance),
            typeof(TerrariaHooksContext).GetMethod("OnUnloadContent", BindingFlags.NonPublic | BindingFlags.Static)
        );

        // All of our own hooks need to be undone last.
        HookOnUnloadAll = new Hook(
            typeof(ModLoader).GetMethod("Unload", BindingFlags.NonPublic | BindingFlags.Static),
            typeof(TerrariaHooksContext).GetMethod("OnUnloadAll", BindingFlags.NonPublic | BindingFlags.Static)
        );

        // Keep track of all NativeDetours, Detours and (indirectly) Hooks.
        Detour.OnDetour += RegisterDetour;
        Detour.OnUndo += UnregisterDetour;
        NativeDetour.OnDetour += RegisterNativeDetour;
        NativeDetour.OnUndo += UnregisterDetour;
    }

    internal static void Dispose() {
        if (!Initialized)
            return;
        Initialized = false;
        Mods.Clear();
        OwnedDetourLists.Clear();

        HookEndpointManager.OnGenerateCecilModule -= GenerateCecilModule;

        Detour.OnDetour -= RegisterDetour;
        Detour.OnUndo -= UnregisterDetour;
        NativeDetour.OnDetour -= RegisterNativeDetour;
        NativeDetour.OnUndo -= UnregisterDetour;

        HookOnUnloadContent.Dispose();
        HookOnUnloadAll.Dispose();
    }
    
    static Hook HookOnUnloadContent;
    static void OnUnloadContent(Action<Mod> orig, Mod mod) {
        orig(mod);

        if (mod.Code == null || mod is TerrariaHooksMod)
            return;

        // Unload any HookGen hooks after unloading the mod.
        HookEndpointManager.RemoveAllOwnedBy(mod.Code);
        if (OwnedDetourLists.TryGetValue(mod.Code, out List<IDetour> list)) {
            OwnedDetourLists.Remove(mod.Code);
            foreach (IDetour detour in list)
                detour.Dispose();
        }
    }

    static Hook HookOnUnloadAll;
    static void OnUnloadAll(Action orig) {
        orig();

        // Dispose the context.
        Dispose();
    }

    internal static List<IDetour> GetOwnedDetourList(StackTrace stack = null, bool add = true) {
        // I deserve to be murdered for this.
        if (stack == null)
            stack = new StackTrace();
        Assembly owner = null;
        int frameCount = stack.FrameCount;
        int state = 0;
        for (int i = 0; i < frameCount; i++) {
            StackFrame frame = stack.GetFrame(i);
            MethodBase caller = frame.GetMethod();
            if (caller?.DeclaringType == null)
                continue;
            switch (state) {
                // Skip until we've reached a method in Detour or Hook.
                case 0:
                    if (caller.DeclaringType.FullName != "MonoMod.RuntimeDetour.NativeDetour" &&
                        caller.DeclaringType.FullName != "MonoMod.RuntimeDetour.Detour" &&
                        caller.DeclaringType.FullName != "MonoMod.RuntimeDetour.Hook") {
                        continue;
                    }
                    state++;
                    continue;

                // Skip until we're out of Detour and / or Hook.
                case 1:
                    if (caller.DeclaringType.FullName == "MonoMod.RuntimeDetour.NativeDetour" ||
                        caller.DeclaringType.FullName == "MonoMod.RuntimeDetour.Detour" ||
                        caller.DeclaringType.FullName == "MonoMod.RuntimeDetour.Hook") {
                        continue;
                    }
                    owner = caller?.DeclaringType.Assembly;
                    break;
            }
            break;
        }

        if (owner == null)
            return null;

        if (!OwnedDetourLists.TryGetValue(owner, out List<IDetour> list) && add)
            OwnedDetourLists[owner] = list = new List<IDetour>();
        return list;
    }

    internal static bool RegisterDetour(object _detour, MethodBase from, MethodBase to) {
        GetOwnedDetourList()?.Add(_detour as IDetour);
        return true;
    }

    internal static bool RegisterNativeDetour(object _detour, MethodBase method, IntPtr from, IntPtr to) {
        StackTrace stack = new StackTrace();

        // Don't register NativeDetours created by higher level Detours.
        int frameCount = stack.FrameCount;
        for (int i = 0; i < frameCount; i++) {
            StackFrame frame = stack.GetFrame(i);
            MethodBase caller = frame.GetMethod();
            if (caller?.DeclaringType == null)
                continue;
            if (caller.DeclaringType.FullName.StartsWith("MonoMod.RuntimeDetour.") &&
                caller.DeclaringType.FullName != "MonoMod.RuntimeDetour.NativeDetour")
                return true;
        }

        GetOwnedDetourList(stack: stack)?.Add(_detour as IDetour);
        return true;
    }

    internal static bool UnregisterDetour(object _detour) {
        GetOwnedDetourList(add: false)?.Remove(_detour as IDetour);
        return true;
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

}
