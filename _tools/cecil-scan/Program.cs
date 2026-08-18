using System;
using System.Linq;
using Mono.Cecil;

class Program
{
    static void Main(string[] args)
    {
        var path = args[0];
        var mode = args.Length > 1 ? args[1] : "types";
        var filter = args.Length > 2 ? args[2] : "";
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(path));
        var rp = new ReaderParameters { AssemblyResolver = resolver };
        var asm = AssemblyDefinition.ReadAssembly(path, rp);

        if (mode == "types")
        {
            foreach (var t in asm.MainModule.Types.OrderBy(t => t.FullName))
            {
                if (filter.Length > 0 && t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Console.WriteLine("T " + t.FullName);
            }
            return;
        }

        foreach (var t in asm.MainModule.Types)
        {
            DumpType(t, filter, 0);
            foreach (var n in t.NestedTypes)
                DumpType(n, filter, 1);
        }
    }

    static void DumpType(TypeDefinition t, string filter, int indent)
    {
        if (filter.Length > 0 && t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            return;
        var pad = new string(' ', indent * 2);
        Console.WriteLine(pad + "==== " + t.FullName + " : " + (t.BaseType != null ? t.BaseType.FullName : "") + " ====");
        foreach (var f in t.Fields.Where(f => !f.Name.Contains("k__BackingField")))
            Console.WriteLine(pad + "  F " + (f.IsStatic ? "static " : "") + f.FieldType.Name + " " + f.Name);
        foreach (var p in t.Properties)
            Console.WriteLine(pad + "  P " + p.PropertyType.Name + " " + p.Name);
        foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter && !m.IsConstructor && !m.Name.StartsWith("<")))
        {
            var ps = string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name));
            Console.WriteLine(pad + "  M " + (m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" + ps + ")");
        }
    }
}
