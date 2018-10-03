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
using Terraria;
using Terraria.ModLoader;

namespace TerrariaHooks {
    class TerrariaHooksMod : Mod {

        public TerrariaHooksMod() {
            TerrariaHooksContext.Init(this);
            ModCompilerHook.Init(typeof(Mod).Assembly);

            Properties = new ModProperties() {
            };

        }

    }
}
