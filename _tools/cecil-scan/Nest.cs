using System;
using System.Linq;
using Mono.Cecil;
class P {
  static void Main(string[] args) {
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(args[0]));
    var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
    void Walk(TypeDefinition t, int d) {
      if (t.FullName.IndexOf("SkillCalculation", StringComparison.OrdinalIgnoreCase) >= 0 && t.FullName.IndexOf("Preview", StringComparison.OrdinalIgnoreCase) >= 0) {
        Console.WriteLine(t.FullName);
        foreach (var f in t.Fields) Console.WriteLine("  F " + f.FieldType.Name + " " + f.Name);
        foreach (var p in t.Properties) Console.WriteLine("  P " + p.PropertyType.Name + " " + p.Name);
      }
      foreach (var n in t.NestedTypes) Walk(n, d+1);
    }
    foreach (var t in asm.MainModule.Types) Walk(t, 0);
  }
}
