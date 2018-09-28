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

            string inputDir, extraTxt, outputDir;
            if (args.Length != 3 || !Directory.Exists(inputDir = args[0]) || !File.Exists(extraTxt = args[1])) {
                Console.Error.WriteLine("Usage: inputdir extras.txt outputdir");
                return;
            }

            // Check that the files exist.
            if (!VerifyFile(out string inputXNA, inputDir, "Terraria.XNA.exe"))
                return;
            if (!VerifyFile(out string inputFNA, inputDir, "Terraria.FNA.exe"))
                return;

            // Clean up the output dir.
            if (Directory.Exists(outputDir = args[2]))
                Directory.Delete(outputDir, true);
            Directory.CreateDirectory(outputDir);

            // Strip.
            foreach (string path in Directory.GetFiles(inputDir)) {
                Console.WriteLine($"Stripping: {path}");
                Stripper.Strip(path);
            }

            // Generate hooks.
            string hooksXNA = Path.Combine(outputDir, "TerrariaHooks.Windows.Pre.dll");
            string hooksFNA = Path.Combine(outputDir, "TerrariaHooks.Mono.Pre.dll");
            GenHooks(inputXNA, hooksXNA);
            GenHooks(inputFNA, hooksFNA);

            // Merge generated .dlls and MonoMod into one .dll per environment.
            string[] extraFiles = File.ReadAllLines(extraTxt);
            Repack(hooksXNA, extraFiles, Path.Combine(outputDir, "TerrariaHooks.Windows.dll"));
            File.Delete(hooksXNA);
            Repack(hooksFNA, extraFiles, Path.Combine(outputDir, "TerrariaHooks.Mono.dll"));
            File.Delete(hooksFNA);
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

        static void Repack(string input, string[] extras, string output) {
            Console.Error.WriteLine($"Repacking: {input} -> {output}");

            List<string> args = new List<string>();
            args.Add($"/out:{output}");
            args.Add(input);
            foreach (string dep in extras)
                if (!string.IsNullOrWhiteSpace(dep))
                    args.Add(dep.Trim());

            RepackOptions options = new RepackOptions(args);
            ILRepack repack = new ILRepack(options);
            repack.Repack();
        }

    }
}
