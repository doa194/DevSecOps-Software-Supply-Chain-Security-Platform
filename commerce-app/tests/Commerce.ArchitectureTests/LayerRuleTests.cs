// Clean Architecture rules inside each module. The domain model sits at the centre and
// knows nothing about persistence, HTTP, messaging or the module's own feature slices.
// Persistence code may serve features but never call them.
using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Commerce.ArchitectureTests;

public sealed class LayerRuleTests
{
    public static TheoryData<string> Modules => new(CommerceArchitecture.ModuleNames);

    [Theory]
    [MemberData(nameof(Modules))]
    public void Domain_is_free_of_infrastructure_and_application_code(string module)
    {
        var forbidden =
            $@"^(Commerce\.BuildingBlocks|Commerce\.Modules\.{module}\.(Features|Data|Integration)|Microsoft\.EntityFrameworkCore|Microsoft\.AspNetCore|RabbitMQ|StackExchange\.Redis|Amazon|Npgsql)(\..*)?$";

        var rule = Types().That().ResideInNamespaceMatching($@"^Commerce\.Modules\.{module}\.Domain(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(forbidden)
            .Because("the domain model must stay testable without a database, broker or web server");

        rule.WithoutRequiringPositiveResults().Check(CommerceArchitecture.Architecture);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Persistence_does_not_depend_on_features(string module)
    {
        var rule = Types().That().ResideInNamespaceMatching($@"^Commerce\.Modules\.{module}\.Data(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching($@"^Commerce\.Modules\.{module}\.Features(\..*)?$")
            .Because("features use persistence, never the other way round");

        rule.WithoutRequiringPositiveResults().Check(CommerceArchitecture.Architecture);
    }
}
