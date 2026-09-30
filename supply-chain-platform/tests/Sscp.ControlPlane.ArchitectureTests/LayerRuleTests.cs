// Layering of the Security Control Plane. Trust policy and the artifact state machine
// live in the domain and must stay free of I/O so they can be tested exhaustively; the
// application layer reaches the outside world only through ports it defines itself.
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using Sscp.ControlPlane.Application;
using Sscp.ControlPlane.Domain;
using Sscp.ControlPlane.Infrastructure;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Sscp.ControlPlane.ArchitectureTests;

public sealed class LayerRuleTests
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(DomainAssembly).Assembly,
            typeof(ApplicationAssembly).Assembly,
            typeof(InfrastructureAssembly).Assembly,
            typeof(Program).Assembly)
        .Build();

    private const string IoNamespaces =
        @"Microsoft\.EntityFrameworkCore|Npgsql|Amazon|Microsoft\.AspNetCore|System\.Net\.Http|YamlDotNet";

    [Fact]
    public void Domain_depends_on_no_other_layer_and_no_io()
    {
        var rule = Types().That().ResideInNamespaceMatching(@"^Sscp\.ControlPlane\.Domain(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(
                $@"^(Sscp\.ControlPlane\.(Application|Infrastructure|Api)|{IoNamespaces})(\..*)?$")
            .Because("trust decisions must be pure functions of their inputs so they can be tested exhaustively");

        rule.WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Application_depends_only_on_the_domain_and_its_own_ports()
    {
        var rule = Types().That().ResideInNamespaceMatching(@"^Sscp\.ControlPlane\.Application(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(
                @"^(Sscp\.ControlPlane\.(Infrastructure|Api)|Microsoft\.EntityFrameworkCore|Npgsql|Amazon|Microsoft\.AspNetCore)(\..*)?$");

        rule.WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_api()
    {
        var rule = Types().That().ResideInNamespaceMatching(@"^Sscp\.ControlPlane\.Infrastructure(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^Sscp\.ControlPlane\.Api(\..*)?$");

        rule.WithoutRequiringPositiveResults().Check(Architecture);
    }
}
