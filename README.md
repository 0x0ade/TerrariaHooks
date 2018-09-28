# TerrariaHookGen

### License: MIT

----

HookGen "recipe" for Terraria, resulting in a TML mod that other mods can depend on.

Using [MonoMod](https://github.com/0x0ade/MonoMod), an open-source C# modding utility.

**Players:** This mod is needed by other mods. Just install it, it doesn't change Terraria on its own.

**Modders:** Please read the MonoMod RuntimeDetour README, which also explains HookGen:
https://github.com/0x0ade/MonoMod/blob/master/README-RuntimeDetour.md


## Features

- "Hook" any arbitrary method you want easily:
```cs
On.Terraria.Player.CheckMana += (orig, player, amount, pay, blockQuickMana) => {
	// We can either make this act as a method replacement
	// or just call our code around the original code.

	// Let's double the mana cost of everything.
	// We can pass on custom values.
	bool spendable = orig(player, amount * 2, pay, blockQuickMana);

	// ... but give back half of it if it was spent.
	if (spendable && pay) {
		player.statMana += amount / 2;
	}

	// We can return custom values.
	return spendable;
}
```

- Use "hooks" to detect when a method runs, change how it runs or even replace it.  
"Hooks" are automatically undone whenever your mod unloads. This is handled by TerrariaHooks for you.

- Manipulate any method at runtime via IL... += (...) => {...} using Cecil and many helpers.  
Special thanks to Chicken-Bones for the great ideas and feedback along the way!

- Use RuntimeDetour to quickly port your existing "method swapping" code.  
Note that this requires you to undo your detours on unload.

## Instructions

### Integrating TerrariaHooks

```cs
// TODO: Figure out how to add a strong dependency with per-platform .dlls
// TODO: Write this section.
```

### Custom TerrariaHooks.*.dll

- `git clone --recursive https://github.com/0x0ade/TerrariaHookGen.git`
    - Alternatively, pull in the MonoMod submodule manually
- Update the files in `_input`
	- The files redistributed in this repo are stripped.
	- The files in that directory will be stripped automatically on each run.
- Build the solution.
- Run `./TerrariaHookGen/bin/Debug/TerrariaHookGen.exe _input extras.txt _output`

This repository overcomplicates the entire procedure. It boils down to:
```bash
./MonoMod.RuntimeDetour.HookGen.exe --private Terraria.exe TerrariaHooksPre.dll
./ILRepack.exe /out:TerrariaHooks.dll TerrariaHooksPre.dll MonoMod.*.dll MonoMod.exe
```

When running the above two lines in the Terraria directory (with all dependencies present), it generates `TerrariaHooks.dll` for your `Terraria.exe`.

The sole purpose of this repository is to automate the process entirely, and to allow publishing `TerrariaHooks.dll` as a TML mod.
