using System; using System.Reflection;
var asm = Assembly.LoadFrom(@"D:\AutoCAD 2026\AcDbMgd.dll");
var t = asm.GetType("Autodesk.AutoCAD.DatabaseServices.LayerViewportProperties");
Console.WriteLine("=== ALL constructors (public+nonpublic) ===");
foreach (var c in t!.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
{
    string access = c.IsPublic ? "public" : c.IsAssembly ? "internal" : c.IsFamily ? "protected" : c.IsFamilyOrAssembly ? "protected internal" : "private";
    Console.WriteLine($"  [{access}] ({string.Join(", ", Array.ConvertAll(c.GetParameters(), p => p.ParameterType.Name+" "+p.Name))})");
}
