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

static partial class TerrariaHooksContext {

    private static readonly FieldInfo f_AssemblyManager_loadedAssemblies =
        typeof(Mod).Assembly
        .GetType("Terraria.ModLoader.AssemblyManager")
        .GetField("loadedAssemblies", BindingFlags.NonPublic | BindingFlags.Static);
    private static IDictionary<string, Assembly> LoadedAssemblies =>
        f_AssemblyManager_loadedAssemblies.GetValue(null) as IDictionary<string, Assembly>;

    static bool IsOutdated = false;
    static Func<object, MethodBase, MethodBase, bool> OnRemoteDetour;
    static Func<object, bool> OnRemoteUndo;
    static Func<object, MethodBase, MethodBase> OnRemoteGenerateTrampoline;
    static Func<object, MethodBase, IntPtr, IntPtr, bool> OnRemoteNativeDetour;
    static Func<object, bool> OnRemoteNativeUndo;
    static Func<object, MethodBase, MethodBase> OnRemoteNativeGenerateTrampoline;
    static Func<MethodBase, Delegate, bool> OnRemoteAdd;
    static Func<MethodBase, Delegate, bool> OnRemoteRemove;
    static Func<MethodBase, Delegate, bool> OnRemoteModify;
    static Func<MethodBase, Delegate, bool> OnRemoteUnmodify;
    static Func<object, bool> OnRemoteRemoveAllOwnedBy;

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

    static readonly Dictionary<object, NativeDetour> RemoteNativeDetours = new Dictionary<object, NativeDetour>();
    static readonly Func<object, MethodBase, IntPtr, IntPtr, bool> OnLocalNativeDetour = (remote, method, from, to) => {
        RemoteNativeDetours[remote] = new NativeDetour(method, from, to);
        return false;
    };
    static readonly Func<object, bool> OnLocalNativeUndo = (remote) => {
        RemoteNativeDetours[remote].Undo();
        RemoteNativeDetours.Remove(remote);
        return false;
    };
    static readonly Func<object, MethodBase, MethodBase> OnLocalNativeGenerateTrampoline = (remote, signature) => {
        return RemoteNativeDetours[remote].GenerateTrampoline(signature);
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

    static void Upgrade(
        Assembly newestAsm,
        Func<object, MethodBase, MethodBase, bool> onRemoteDetour,
        Func<object, bool> onRemoteUndo,
        Func<object, MethodBase, MethodBase> onRemoteGenerateTrampoline,
        Func<object, MethodBase, IntPtr, IntPtr, bool> onRemoteNativeDetour,
        Func<object, bool> onRemoteNativeUndo,
        Func<object, MethodBase, MethodBase> onRemoteNativeGenerateTrampoline,
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
        NativeDetour.OnDetour += OnRemoteNativeDetour = onRemoteNativeDetour;
        NativeDetour.OnUndo += OnRemoteNativeUndo = onRemoteNativeUndo;
        NativeDetour.OnGenerateTrampoline += OnRemoteNativeGenerateTrampoline = onRemoteNativeGenerateTrampoline;
        HookEndpointManager.OnAdd += OnRemoteAdd = onRemoteAdd;
        HookEndpointManager.OnRemove += OnRemoteRemove = onRemoteRemove;
        HookEndpointManager.OnModify += OnRemoteModify = onRemoteModify;
        HookEndpointManager.OnUnmodify += OnRemoteUnmodify = onRemoteUnmodify;
        HookEndpointManager.OnRemoveAllOwnedBy += OnRemoteRemoveAllOwnedBy = onRemoteRemoveAllOwnedBy;
    }

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
                    OnLocalNativeDetour, OnLocalNativeUndo, OnLocalNativeGenerateTrampoline,
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

}
