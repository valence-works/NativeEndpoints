using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// Guards the two packaging promises the README makes: a single current target framework, and a
/// dependency graph that ends at the ASP.NET Core shared framework.
/// </summary>
public class PackagingTests
{
    private static readonly string[] ShippedAssemblies = ["NativeEndpoints", "NativeEndpoints.Testing"];

    [Theory]
    [InlineData("NativeEndpoints")]
    [InlineData("NativeEndpoints.Testing")]
    public void Shipped_assembly_targets_net10(string name)
    {
        var framework = Load(name).GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.NotNull(framework);
        Assert.Equal(".NETCoreApp,Version=v10.0", framework.FrameworkName);
    }

    [Fact]
    public void Core_assembly_depends_on_nothing_outside_the_shared_framework()
    {
        // The core package takes a FrameworkReference and no PackageReference, so every assembly it
        // references must come from the runtime or the ASP.NET Core shared framework. A third-party
        // name appearing here means a PackageReference was added and the README's "no dependencies"
        // claim stopped being true.
        var referenced = Load("NativeEndpoints")
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name!)
            .Where(name => !name.StartsWith("System.", StringComparison.Ordinal)
                        && !name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
                        && !name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
                        && name is not ("System" or "netstandard" or "mscorlib"))
            .ToArray();

        Assert.Empty(referenced);
    }

    [Fact]
    public void Test_kit_does_not_reference_the_framework_it_tests()
    {
        // The unload harness has to work against any endpoint framework, including the one a user is
        // migrating away from. A reference here would make it useless for measuring anything else.
        var referenced = Load("NativeEndpoints.Testing")
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name!);

        Assert.DoesNotContain("NativeEndpoints", referenced);
    }

    private static Assembly Load(string name)
    {
        Assert.Contains(name, ShippedAssemblies);
        return Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, name + ".dll"));
    }
}
