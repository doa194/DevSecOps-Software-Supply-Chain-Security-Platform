// Loads every production assembly of the commerce workload once, so all architecture
// rules check the same compiled code. Rules work on real type dependencies found in the
// IL, not on project files, so a forbidden call is caught even if a reference sneaks in.
using System.Reflection;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;

namespace Commerce.ArchitectureTests;

internal static class CommerceArchitecture
{
    public static readonly string[] ModuleNames =
        ["Identity", "Customers", "Catalog", "Inventory", "Orders", "Payments", "Documents", "Administration"];

    public static readonly string[] WorkerNames = ["Notification", "Audit", "Documents", "Reporting"];

    public static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(LoadCommerceAssemblies())
        .Build();

    private static System.Reflection.Assembly[] LoadCommerceAssemblies()
    {
        // Every Commerce.* assembly copied next to the tests is production code under test.
        var directory = AppContext.BaseDirectory;
        return Directory.GetFiles(directory, "Commerce.*.dll")
            .Where(path => !Path.GetFileName(path).Contains("Tests", StringComparison.Ordinal))
            .Select(System.Reflection.Assembly.LoadFrom)
            .ToArray();
    }
}
