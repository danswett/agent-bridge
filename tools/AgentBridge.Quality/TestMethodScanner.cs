using System.Reflection;
using System.Runtime.InteropServices;

namespace AgentBridge.Quality;

/// <summary>
/// The test methods of a built test assembly, named the way VSTest names them (<c>Ns.Outer+Inner.Method</c>: the type's FullName, nested types with '+', no generic arity
/// or arguments), read from METADATA only: <see cref="MetadataLoadContext"/> loads no type, runs no static initializer and no test code. A test method is one carrying an
/// attribute that derives from xunit's <c>FactAttribute</c> (Fact, Theory and any custom subclass: a scan by attribute name dropped 73 of Host.Tests' 4860 tests).
/// Abstract and open generic classes are not tests themselves; their methods count under each concrete class that inherits them, as xunit reports them.
/// </summary>
internal static class TestMethodScanner
{
    const string FactAttributeName = "Xunit.FactAttribute";

    public static IReadOnlyList<string> Scan(string dll)
    {
        var resolver = new PathAssemblyResolver(Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(dll))!, "*.dll").Concat(Directory.EnumerateFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")));
        using var context = new MetadataLoadContext(resolver);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters && t.FullName is not null))
        {
            // Public instance methods, own and inherited, as xunit finds them; an overridden or hidden name is one test.
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(IsTest)) { found.Add($"{type.FullName}.{method.Name}"); }
        }
        return [.. found];
    }

    static bool IsTest(MethodInfo method)
    {
        foreach (var attribute in method.GetCustomAttributesData())
        {
            for (var type = attribute.AttributeType; type is not null; type = type.BaseType)
            {
                if (type.FullName == FactAttributeName) { return true; }
            }
        }
        return false;
    }
}
