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
    abstract class MethodSwapUpgrader {

        public abstract void Load(Mod mod, bool late);
        public abstract void Unload(Mod mod);

    }
}
