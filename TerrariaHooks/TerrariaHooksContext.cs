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
    static bool IsInstalled = false;
    static bool IsFirstAutoload = false;

    static readonly Dictionary<Assembly, List<IDetour>> OwnedDetourLists = new Dictionary<Assembly, List<IDetour>>();

    public static void Init(Mod mod) {
        if (Mods.Contains(mod))
            throw new InvalidOperationException("TerrariaHooksContext.Init cannot run more than once per mod without unloading!");

        if (mod is TerrariaHooksMod)
            IsInstalled = true;

        if (Initialized)
            return;
        Initialized = true;
        IsFirstAutoload = true;
        IsOutdated = false;

        // Load the cecil module generator.
        // Adding this more than once shouldn't hurt.
        HookEndpointManager.OnGenerateCecilModule += GenerateCecilModule;

        // We need to perform some magic to get all loaded copies of TerrariaHooks in sync.
        // OnAutoload is located in .Upgrade.cs
        HookOnAutoload = new Hook(
            typeof(Mod).GetMethod("Autoload", BindingFlags.NonPublic | BindingFlags.Instance),
            typeof(TerrariaHooksContext).GetMethod("OnAutoload", BindingFlags.NonPublic | BindingFlags.Static)
        );

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
        OwnedDetourLists.Clear();

        HookEndpointManager.OnGenerateCecilModule -= GenerateCecilModule;

        Detour.OnDetour -= RegisterDetour;
        Detour.OnUndo -= UnregisterDetour;
        NativeDetour.OnDetour -= RegisterNativeDetour;
        NativeDetour.OnUndo -= UnregisterDetour;

        HookOnAutoload.Dispose();
        HookOnUnloadContent.Dispose();
        HookOnUnloadAll.Dispose();

        if (IsOutdated) {
            IsOutdated = false;
            Detour.OnDetour -= OnRemoteDetour;
            Detour.OnUndo -= OnRemoteUndo;
            Detour.OnGenerateTrampoline -= OnRemoteGenerateTrampoline;
            NativeDetour.OnDetour -= OnRemoteNativeDetour;
            NativeDetour.OnUndo -= OnRemoteNativeUndo;
            NativeDetour.OnGenerateTrampoline -= OnRemoteNativeGenerateTrampoline;
            HookEndpointManager.OnAdd -= OnRemoteAdd;
            HookEndpointManager.OnRemove -= OnRemoteRemove;
            HookEndpointManager.OnModify -= OnRemoteModify;
            HookEndpointManager.OnUnmodify -= OnRemoteUnmodify;
            HookEndpointManager.OnRemoveAllOwnedBy -= OnRemoteRemoveAllOwnedBy;
        }
    }
    
    static Hook HookOnUnloadContent;
    static void OnUnloadContent(Action<Mod> orig, Mod mod) {
        orig(mod);

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

    internal static List<IDetour> GetOwnedDetourList(bool add = true) {
        // Find .ctor call on stack. Whatever called that is the detour's owner.
        StackTrace stack = new StackTrace(0);
        Assembly owner = null;
        int frameCount = stack.FrameCount;
        for (int i = 0; i < frameCount - 1; i++) {
            StackFrame frame = stack.GetFrame(i);
            MethodBase caller = frame.GetMethod();
            if (caller == null || !caller.IsConstructor)
                continue;
            owner = stack.GetFrame(i + 1).GetMethod()?.DeclaringType?.Assembly;
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
        GetOwnedDetourList()?.Add(_detour as IDetour);
        return true;
    }

    internal static bool UnregisterDetour(object _detour) {
        GetOwnedDetourList(false)?.Remove(_detour as IDetour);
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
