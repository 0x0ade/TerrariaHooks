using ILRepacking;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod;
using MonoMod.RuntimeDetour.HookGen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace TerrariaHookGen {
    class Program {

        static void Main(string[] args) {
            // Required for the relative paths in extras.dll to work properly.
            if (!File.Exists("MonoMod.RuntimeDetour.dll"))
                Environment.CurrentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string inputDir, outputDir;
            if (args.Length != 2 ||
                !Directory.Exists(inputDir = args[0]) ||
                !Directory.Exists(outputDir = args[1])) {
                Console.Error.WriteLine("Usage: inputdir outputdir");
                return;
            }

            // Check that the files exist.
            if (!VerifyFile(out string inputXNA, inputDir, "Terraria.XNA.exe"))
                return;
            if (!VerifyFile(out string inputFNA, inputDir, "Terraria.FNA.exe"))
                return;

            // Strip or copy.
            foreach (string path in Directory.GetFiles(inputDir)) {
                if (!path.EndsWith(".exe") && !path.EndsWith(".dll")) {
                    Console.WriteLine($"Copying: {path}");
                    File.Copy(path, Path.Combine(outputDir, Path.GetFileName(path)));
                    continue;
                }

                Console.WriteLine($"Stripping: {path}");
                Stripper.Strip(path);
            }

            // Generate hooks.
            string hooksXNA = Path.Combine(outputDir, "Windows.Pre.dll");
            string hooksFNA = Path.Combine(outputDir, "Mono.Pre.dll");
            GenHooks(inputXNA, hooksXNA);
            GenHooks(inputFNA, hooksFNA);

            // Merge generated .dlls and MonoMod into one .dll per environment.
            string[] extrasMod = {
                "TerrariaHooks.dll",
                "MonoMod.exe",
                "MonoMod.RuntimeDetour.dll",
                "MonoMod.Utils.dll"
            };
            Repack(hooksXNA, extrasMod, Path.Combine(outputDir, "Windows.dll"), "TerrariaHooks.dll");
            File.Delete(hooksXNA);
            Repack(hooksFNA, extrasMod, Path.Combine(outputDir, "Mono.dll"), "TerrariaHooks.dll");
            File.Delete(hooksFNA);

            Repack("tModLoaderServer_TerrariaHooks.exe", new string[] {
                "MonoMod.RuntimeDetour.dll",
                "MonoMod.Utils.dll"
            }, Path.Combine(outputDir, "tModLoaderServer_TerrariaHooks.exe"));
        }

        static bool VerifyFile(out string path, params string[] paths) {
            path = Path.Combine(paths);
            if (!File.Exists(path) && !Directory.Exists(path)) {
                Console.Error.WriteLine($"Missing: {path}");
                return false;
            }
            return true;
        }

        static void GenHooks(string input, string output) {
            Console.WriteLine($"Hooking: {input} -> {output}");

            using (MonoModder mm = new MonoModder() {
                InputPath = input,
                OutputPath = output,
                ReadingMode = ReadingMode.Deferred,

                MissingDependencyThrow = false,
            }) {
                mm.Read();
                mm.MapDependencies();

                if (File.Exists(output))
                    File.Delete(output);

                HookGenerator gen = new HookGenerator(mm, Path.GetFileName(output)) {
                    HookPrivate = true,
                };
                gen.Generate();
                gen.OutputModule.Write(output);
            }
        }

        static void Repack(string input, string[] extras, string output, string name = null) {
            Console.Error.WriteLine($"Repacking: {input} -> {output}");
            if (name == null)
                name = Path.GetFileName(output);

            string outputTmp = Path.Combine(Path.GetDirectoryName(output), name);
            if (File.Exists(outputTmp))
                File.Delete(outputTmp);

            List<string> args = new List<string>();
            args.Add($"/out:{outputTmp}");
            args.Add(input);
            foreach (string dep in extras)
                if (!string.IsNullOrWhiteSpace(dep))
                    args.Add(dep.Trim());

            RepackOptions options = new RepackOptions(args);
            ILRepack repack = new ILRepack(options);
            repack.Repack();

            if (output != outputTmp) {
                if (File.Exists(output))
                    File.Delete(output);
                File.Move(outputTmp, output);
            }
        }

    }
}
