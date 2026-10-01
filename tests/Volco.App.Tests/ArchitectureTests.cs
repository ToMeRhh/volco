using System.Reflection;
using System.Runtime.InteropServices;
using Volco.Core.Runtime;

namespace Volco.App.Tests;

/// <summary>
/// Enforces "Volco.App and Volco.Core are platform-free". P/Invoke, LibraryImport and CsWin32 all
/// compile fine on Linux, so compiling there proves nothing about them; reflection does.
/// (WinRT and Windows SDK projections do fail the Linux compile, which is the other half.)
/// </summary>
public class ArchitectureTests
{
    private const BindingFlags AllDeclared =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance;

    private static readonly string[] ForbiddenAssemblyPrefixes =
    {
        "Microsoft.Windows", "Microsoft.WinUI", "Microsoft.WindowsAppRuntime", "Microsoft.UI", "Windows.",
    };

    private static IEnumerable<Type> AllTypes(Assembly assembly)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
        return types; // GetTypes includes nested types.
    }

    /// <summary>Returns one message per P/Invoke method (by flag or by attribute) in the assembly.</summary>
    internal static List<string> FindPInvokeViolations(Assembly assembly)
    {
        var problems = new List<string>();
        foreach (var type in AllTypes(assembly))
        {
            foreach (var method in type.GetMethods(AllDeclared))
            {
                var where = $"{assembly.GetName().Name}: {type.FullName}.{method.Name}";
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                    problems.Add($"{where} has MethodAttributes.PinvokeImpl");
                if (method.GetCustomAttributes(false).Any(a => a is DllImportAttribute) ||
                    method.CustomAttributes.Any(a =>
                        a.AttributeType.FullName == typeof(DllImportAttribute).FullName ||
                        a.AttributeType.FullName == "System.Runtime.InteropServices.LibraryImportAttribute"))
                    problems.Add($"{where} carries DllImport/LibraryImport");
            }
        }
        return problems;
    }

    /// <summary>Returns one message per forbidden assembly reference.</summary>
    internal static List<string> FindForbiddenReferences(Assembly assembly)
        => assembly.GetReferencedAssemblies()
            .Where(r => ForbiddenAssemblyPrefixes.Any(p =>
                r.Name!.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(r => $"{assembly.GetName().Name} references forbidden assembly {r.Name}")
            .ToList();

    private static readonly Assembly[] PlatformFree =
    {
        typeof(VolcoApplication).Assembly, // Volco.App
        typeof(VolcoRuntime).Assembly,     // Volco.Core
    };

    [Fact]
    public void PlatformFreeAssemblies_HaveNoPInvoke()
    {
        var problems = PlatformFree.SelectMany(FindPInvokeViolations).ToList();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void PlatformFreeAssemblies_ReferenceNoWindowsAssemblies()
    {
        var problems = PlatformFree.SelectMany(FindForbiddenReferences).ToList();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // Self-test: Volco.Windows is not visible from here, so prove the detector works on a
    // private nested P/Invoke declared in this (test) assembly.
    private static class NativeProbe
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
    }

    [Fact]
    public void Detector_FindsPInvokeInThisAssembly()
    {
        var problems = FindPInvokeViolations(typeof(ArchitectureTests).Assembly);
        Assert.Contains(problems, p => p.Contains("NativeProbe") && p.Contains("GetCurrentThreadId"));
    }

    [Fact]
    public void Detector_FlagsForbiddenReferencePrefixes()
    {
        // Reference-check helper is prefix based; make sure the prefixes cover the intended names.
        Assert.Contains(ForbiddenAssemblyPrefixes, p => "Microsoft.WindowsAppRuntime.Bootstrap".StartsWith(p));
        Assert.Contains(ForbiddenAssemblyPrefixes, p => "Windows.Foundation".StartsWith(p));
        Assert.Empty(FindForbiddenReferences(typeof(ArchitectureTests).Assembly));
    }
}
