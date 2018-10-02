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
using System.Text;
using Terraria;
using Terraria.ModLoader;
using TerrariaHooks;

public static class TerrariaHooksContext {

    private static readonly FieldInfo f_AssemblyManager_loadedAssemblies =
        typeof(Mod).Assembly
        .GetType("Terraria.ModLoader.AssemblyManager")
        .GetField("loadedAssemblies", BindingFlags.NonPublic | BindingFlags.Static);
    private static IDictionary<string, Assembly> LoadedAssemblies => f_AssemblyManager_loadedAssemblies.GetValue(null) as IDictionary<string, Assembly>;

    static bool IsOutdated = false;
    static Func<object, MethodBase, MethodBase, bool> OnRemoteDetour;
    static Func<object, bool> OnRemoteUndo;
    static Func<object, MethodBase, MethodBase> OnRemoteGenerateTrampoline;
    static Func<MethodBase, Delegate, bool> OnRemoteAdd;
    static Func<MethodBase, Delegate, bool> OnRemoteRemove;
    static Func<MethodBase, Delegate, bool> OnRemoteModify;
    static Func<MethodBase, Delegate, bool> OnRemoteUnmodify;
    static Func<object, bool> OnRemoteRemoveAllOwnedBy;

    static readonly HashSet<Mod> Mods = new HashSet<Mod>();
    static bool Initialized = false;
    static bool IsInstalled = false;
    static bool IsFirstAutoload = false;

    public static void Init(Mod mod) {
        if (Mods.Contains(mod))
            throw new InvalidOperationException("TerrariaHooksMini.Boot cannot run more than once per mod without unloading!");

        if (Initialized)
            return;
        Initialized = true;
        IsFirstAutoload = true;
        IsOutdated = false;

        if (mod is TerrariaHooksMod)
            IsInstalled = true;

        // Load the cecil module generator.
        // Adding this more than once shouldn't hurt.
        HookEndpointManager.OnGenerateCecilModule += GenerateCecilModule;

        // We need to perform some magic to get all loaded copies of TerrariaHooks in sync.
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
    }

    internal static void Dispose() {
        if (!Initialized)
            return;
        Initialized = false;

        HookEndpointManager.OnGenerateCecilModule -= GenerateCecilModule;

        HookOnAutoload.Dispose();
        HookOnUnloadContent.Dispose();
        HookOnUnloadAll.Dispose();

        if (IsOutdated) {
            IsOutdated = false;
            Detour.OnDetour -= OnRemoteDetour;
            Detour.OnUndo -= OnRemoteUndo;
            Detour.OnGenerateTrampoline -= OnRemoteGenerateTrampoline;
            HookEndpointManager.OnAdd -= OnRemoteAdd;
            HookEndpointManager.OnRemove -= OnRemoteRemove;
            HookEndpointManager.OnModify -= OnRemoteModify;
            HookEndpointManager.OnUnmodify -= OnRemoteUnmodify;
            HookEndpointManager.OnRemoveAllOwnedBy -= OnRemoteRemoveAllOwnedBy;
        }
    }

    static void Upgrade(
        Assembly newestAsm,
        Func<object, MethodBase, MethodBase, bool> onRemoteDetour,
        Func<object, bool> onRemoteUndo,
        Func<object, MethodBase, MethodBase> onRemoteGenerateTrampoline,
        Func<MethodBase, Delegate, bool> onRemoteAdd,
        Func<MethodBase, Delegate, bool> onRemoteRemove,
        Func<MethodBase, Delegate, bool> onRemoteModify,
        Func<MethodBase, Delegate, bool> onRemoteUnmodify,
        Func<object, bool> onRemoteRemoveAllOwnedBy
    ) {
        Assembly selfAsm = Assembly.GetExecutingAssembly();
        IsOutdated = true;

        if (IsInstalled) {
            Console.WriteLine("The installed version of TerrariaHooks might be conflicting with a version bundled in another mod.");
        } else {
            Console.WriteLine("The following mods are using an outdated / conflicting version of TerrariaHooks:");
            foreach (Mod mod in Mods)
                Console.WriteLine(mod.Name);
        }

        Console.WriteLine($"Upgrading this context from {selfAsm.GetName().Version} to {newestAsm.GetName().Version}");

        Detour.OnDetour += OnRemoteDetour = onRemoteDetour;
        Detour.OnUndo += OnRemoteUndo = onRemoteUndo;
        Detour.OnGenerateTrampoline += OnRemoteGenerateTrampoline = onRemoteGenerateTrampoline;
        HookEndpointManager.OnAdd += OnRemoteAdd = onRemoteAdd;
        HookEndpointManager.OnRemove += OnRemoteRemove = onRemoteRemove;
        HookEndpointManager.OnModify += OnRemoteModify = onRemoteModify;
        HookEndpointManager.OnUnmodify += OnRemoteUnmodify = onRemoteUnmodify;
        HookEndpointManager.OnRemoveAllOwnedBy += OnRemoteRemoveAllOwnedBy = onRemoteRemoveAllOwnedBy;
    }

    static readonly Dictionary<object, Detour> RemoteDetours = new Dictionary<object, Detour>();
    static readonly Func<object, MethodBase, MethodBase, bool> OnLocalDetour = (remote, from, to) => {
        RemoteDetours[remote] = new Detour(from, to);
        return false;
    };
    static readonly Func<object, bool> OnLocalUndo = (remote) => {
        RemoteDetours[remote].Undo();
        RemoteDetours.Remove(remote);
        return false;
    };
    static readonly Func<object, MethodBase, MethodBase> OnLocalGenerateTrampoline = (remote, signature) => {
        return RemoteDetours[remote].GenerateTrampoline(signature);
    };
    static readonly Func<MethodBase, Delegate, bool> OnLocalAdd = (method, hookDelegate) => {
        HookEndpointManager.Add(method, hookDelegate);
        return false;
    };
    static readonly Func<MethodBase, Delegate, bool> OnLocalRemove = (method, hookDelegate) => {
        HookEndpointManager.Remove(method, hookDelegate);
        return false;
    };
    static readonly Func<MethodBase, Delegate, bool> OnLocalModify = (method, callback) => {
        HookEndpointManager.Modify(method, callback);
        return false;
    };
    static readonly Func<MethodBase, Delegate, bool> OnLocalUnmodify = (method, callback) => {
        HookEndpointManager.Unmodify(method, callback);
        return false;
    };
    static readonly Func<object, bool> OnLocalRemoveAllOwnedBy = (owner) => {
        HookEndpointManager.RemoveAllOwnedBy(owner);
        return false;
    };

    static Hook HookOnAutoload;
    static void OnAutoload(Action<Mod> orig, Mod mod) {
        if (IsFirstAutoload) {
            IsFirstAutoload = false;
            if (!IsOutdated) {
                // The first autoload in this context. Let's check if this is the newest version.
                Assembly selfAsm = Assembly.GetExecutingAssembly();
                Version selfVer = selfAsm.GetName().Version;
                List<Assembly> olderAsm = new List<Assembly>();
                Assembly newestAsm = selfAsm;
                Version newestVer = selfVer;

                foreach (Assembly otherAsm in LoadedAssemblies.Values) {
                    if (otherAsm == selfAsm)
                        continue;
                    if (!otherAsm.GetName().Name.StartsWith("TerrariaHooks") &&
                        !otherAsm.GetName().Name.Contains("_TerrariaHooks_"))
                        continue;
                    // Check if the other TerrariaHooks is initialized.
                    if (false.Equals(otherAsm.GetType("TerrariaHooksContext").GetField("Initialized", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null)))
                        continue;

                    // Check the other TerrariaHooks version.
                    Version otherVer = otherAsm.GetName().Version;
                    if (otherVer > newestVer) {
                        // There's a newer copy of TerrariaHooks loaded.
                        newestAsm = otherAsm;
                        newestVer = otherVer;
                    } else {
                        // There's an older copy of TerrariaHooks loaded.
                        olderAsm.Add(otherAsm);
                    }
                }

                if (newestAsm == selfAsm) {
                    // We're the newest version. Upgrade everything else.
                    object[] args = {
                    selfAsm,
                    OnLocalDetour, OnLocalUndo, OnLocalGenerateTrampoline,
                    OnLocalAdd, OnLocalRemove, OnLocalModify, OnLocalUnmodify, OnLocalRemoveAllOwnedBy
                };
                    foreach (Assembly otherAsm in olderAsm) {
                        Type otherT = otherAsm.GetType("TerrariaHooksContext");
                        otherT.GetMethod("Upgrade", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
                    }
                }
            }
        }

        orig(mod);
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

        // Dispose the context.
        Dispose();
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
