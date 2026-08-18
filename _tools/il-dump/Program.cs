using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Program
{
    static void Main(string[] args)
    {
        var path = args[0];
        var typeName = args[1];
        var methodName = args.Length > 2 ? args[2] : "";
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(path));
        var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
        foreach (var t in asm.MainModule.Types)
        {
            Dump(t, typeName, methodName);
            foreach (var n in t.NestedTypes) Dump(n, typeName, methodName);
        }
    }

    static void Dump(TypeDefinition t, string typeName, string methodName)
    {
        if (t.FullName.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) < 0) return;
        foreach (var m in t.Methods)
        {
            if (methodName.Length > 0 && m.Name.IndexOf(methodName, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Console.WriteLine("==== " + t.FullName + "::" + m.Name + " ====");
            if (m.Body == null) { Console.WriteLine("  (no body)"); continue; }
            foreach (var i in m.Body.Instructions)
                Console.WriteLine("  " + i);
        }
    }
}
