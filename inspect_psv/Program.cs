using System; using System.Reflection; using System.Linq;

// Inspect Civil 3D types for style override APIs on pipes/structures in profile views
var asmPath = @"D:\AutoCAD 2026\AeccDbMgd.dll";
var presPath = @"D:\AutoCAD 2026\AeccPressurePipesMgd.dll";

void InspectType(Assembly asm, string typeName)
{
    var t = asm.GetType(typeName);
    if (t == null) { Console.WriteLine($"TYPE NOT FOUND: {typeName}"); return; }
    Console.WriteLine($"\n=== {typeName} ===");
    var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    var members = t.GetMembers(bf)
        .Where(m => {
            string n = m.Name.ToLower();
            return n.Contains("style") || n.Contains("profile") || n.Contains("override") || n.Contains("display");
        })
        .OrderBy(m => m.Name);
    foreach (var m in members)
        Console.WriteLine($"  [{m.MemberType}] {m.Name}");
}

void InspectMethods(Assembly asm, string typeName, string filter = "")
{
    var t = asm.GetType(typeName);
    if (t == null) { Console.WriteLine($"TYPE NOT FOUND: {typeName}"); return; }
    Console.WriteLine($"\n=== {typeName} methods ===");
    var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    foreach (var m in t.GetMethods(bf).Where(m => filter == "" || m.Name.ToLower().Contains(filter.ToLower())).OrderBy(x => x.Name))
    {
        var parms = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({parms})");
    }
}

var civAsm = Assembly.LoadFrom(asmPath);
var presAsm = Assembly.LoadFrom(presPath);

// Find style-related members on Pipe and Structure
InspectType(civAsm, "Autodesk.Civil.DatabaseServices.Pipe");
InspectType(civAsm, "Autodesk.Civil.DatabaseServices.Structure");

// Search for profile view related methods
Console.WriteLine("\n\n=== Pipe methods containing 'profile' ===");
InspectMethods(civAsm, "Autodesk.Civil.DatabaseServices.Pipe", "profile");

Console.WriteLine("\n=== Structure methods containing 'profile' ===");
InspectMethods(civAsm, "Autodesk.Civil.DatabaseServices.Structure", "profile");

// Look for Part base class
Console.WriteLine("\n=== Part members (style/profile/override/display) ===");
InspectType(civAsm, "Autodesk.Civil.DatabaseServices.Part");

// Look for NetworkPartDisplayStyle types
Console.WriteLine("\n\n=== Types containing 'StyleOverride' or 'DisplayStyle' ===");
foreach (var t in civAsm.GetTypes().Where(t => t.Name.ToLower().Contains("styleoverride") || t.Name.ToLower().Contains("displaystyle") || (t.Name.ToLower().Contains("pipe") && t.Name.ToLower().Contains("style"))))
    Console.WriteLine("  " + t.FullName);
