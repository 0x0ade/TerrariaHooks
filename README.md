# TerrariaHookGen

### License: MIT

----

HookGen "recipe" for Terraria, resulting in a TML mod that other mods can depend on.

Using [MonoMod](https://github.com/0x0ade/MonoMod), an open-source C# modding utility.

- `git clone --recursive https://github.com/0x0ade/TerrariaHookGen.git`
    - Alternatively, pull in the MonoMod submodule manually
- Update the files in `_input`
	- The files redistributed in this repo are stripped.
	- The files in that directory will be stripped automatically on each run.
- Build the solution.
- Run `./TerrariaHookGen/bin/Debug/TerrariaHookGen.exe _input extras.txt _output`

### Note

This repository overcomplicates the entire procedure. It boils down to:
```bash
./MonoMod.RuntimeDetour.HookGen.exe --private Terraria.exe TerrariaHooksPre.dll
./ILRepack.exe /out:TerrariaHooks.dll TerrariaHooksPre.dll MonoMod.*.dll MonoMod.exe
```

When running the above two lines in the Terraria directory (with all dependencies present), it generates `TerrariaHooks.dll` for your `Terraria.exe`.

The sole purpose of this repository is to automate the process entirely, and to allow publishing `TerrariaHooks.dll` as a TML mod.
