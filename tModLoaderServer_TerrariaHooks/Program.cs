using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace TerrariaHooks.ServerWrapper {
    class Program {
        static void Main(string[] args) {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string serverPath = Path.Combine(dir, "tModLoaderServer.exe");
            if (!File.Exists(serverPath)) {
                Console.Error.WriteLine("Could not find tModLoaderServer.exe - aborting.");
                return;
            }

            // Load the server assembly.
            Assembly asm = Assembly.LoadFrom(serverPath);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
                string suffix = new AssemblyName(e.Name).Name + ".dll";
                string resourceName = Array.Find(asm.GetManifestResourceNames(), res => res.EndsWith(suffix));
                if (resourceName == null)
                    return null;

                using (Stream stream = asm.GetManifestResourceStream(resourceName)) {
                    byte[] array = new byte[stream.Length];
                    stream.Read(array, 0, array.Length);
                    return Assembly.Load(array);
                }
            };

            // Hook the server's compilation method to support TerrariaHooks.Windows.dll and TerrariaHooks.Mono.dll
            ModCompilerHook.Init(asm);

            // Run the server.
            asm.EntryPoint.Invoke(null, new object[] { args });
        }
    }
}
