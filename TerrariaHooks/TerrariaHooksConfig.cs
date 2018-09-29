using Mono.Cecil;
using MonoMod.RuntimeDetour;
using MonoMod.RuntimeDetour.HookGen;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaHooks {
    class TerrariaHooksConfig {

        static bool PatchOverhaul { get; set; } = true;

    }
}
