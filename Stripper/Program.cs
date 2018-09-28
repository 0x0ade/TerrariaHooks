using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TerrariaHookGen {
    public static class Stripper {

        static void Main(string[] args) {
            string inputDir;
            if (args.Length != 1 || !Directory.Exists(inputDir = args[0])) {
                Console.Error.WriteLine("Usage: inputdir");
                return;
            }

            foreach (string path in Directory.GetFiles(inputDir)) {
                Console.WriteLine($"Stripping: {path}");
                Strip(path);
            }
        }

        public static void Strip(string path, ReaderParameters readerParams = null) {
            if (readerParams == null) {
                DefaultAssemblyResolver asmResolver = new DefaultAssemblyResolver();
                asmResolver.AddSearchDirectory(Path.GetDirectoryName(path));
                readerParams = new ReaderParameters() {
                    AssemblyResolver = asmResolver
                };
            }

            ModuleDefinition module = ModuleDefinition.ReadModule(path, readerParams);

            module.Resources.Clear();

            foreach (TypeDefinition type in module.Types)
                Strip(type);

            module.Write(path);
        }

        static void Strip(TypeDefinition type) {
            foreach (MethodDefinition method in type.Methods)
                if (method.HasBody)
                    method.Body = new MethodBody(method);

            foreach (TypeDefinition nested in type.NestedTypes)
                Strip(nested);
        }

    }
}
