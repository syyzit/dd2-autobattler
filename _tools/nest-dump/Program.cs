using System;
using System.Linq;
using Mono.Cecil;

class Program
{
    static void Main(string[] args)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(args[0]));
        var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
        void Walk(TypeDefinition t)
        {
            if (t.Name.IndexOf("Preview", StringComparison.OrdinalIgnoreCase) >= 0
                || t.FullName.IndexOf("SkillCalculation/ActorResult", StringComparison.Ordinal) >= 0)
            {
                Console.WriteLine("==== " + t.FullName + " ====");
                foreach (var f in t.Fields)
                    Console.WriteLine("  F " + f.FieldType.FullName + " " + f.Name);
                foreach (var p in t.Properties)
                    Console.WriteLine("  P " + p.PropertyType.FullName + " " + p.Name);
            }
            foreach (var n in t.NestedTypes)
                Walk(n);
        }
        foreach (var t in asm.MainModule.Types)
            Walk(t);
    }
}
