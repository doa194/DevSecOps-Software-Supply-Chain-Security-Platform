// Module boundaries of the modular monolith. A module may use another module only
// through that module's Contracts assembly (integration events and query interfaces).
// Breaking this rule would let modules share tables or internal types, which is exactly
// the coupling a modular monolith exists to prevent.
using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Commerce.ArchitectureTests;

public sealed class ModuleBoundaryTests
{
    // Any module namespace except a Contracts namespace. The lookahead keeps
    // "Commerce.Modules.Catalog.Contracts" out while matching "Commerce.Modules.Catalog.Domain".
    private const string AnyModuleInternals = @"^Commerce\.Modules\.[A-Za-z]+(?!\.Contracts)(\..*)?$";

    public static TheoryData<string> Modules => new(CommerceArchitecture.ModuleNames);

    public static TheoryData<string> Workers => new(CommerceArchitecture.WorkerNames);

    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_internals_do_not_depend_on_other_modules_internals(string module)
    {
        foreach (var other in CommerceArchitecture.ModuleNames.Where(name => name != module))
        {
            var rule = Types().That().ResideInNamespaceMatching($@"^Commerce\.Modules\.{module}(\..*)?$")
                .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching($@"^Commerce\.Modules\.{other}(?!\.Contracts)(\..*)?$")
                .Because($"{module} may only use {other} through Commerce.Modules.{other}.Contracts");

            rule.WithoutRequiringPositiveResults().Check(CommerceArchitecture.Architecture);
        }
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Contracts_depend_only_on_the_shared_kernel(string module)
    {
        var forbidden =
            @"^(Commerce\.BuildingBlocks|Commerce\.Modules\.[A-Za-z]+(?!\.Contracts)|Microsoft\.EntityFrameworkCore|Microsoft\.AspNetCore|RabbitMQ|StackExchange\.Redis|Amazon)(\..*)?$";

        var rule = Types().That().ResideInNamespaceMatching($@"^Commerce\.Modules\.{module}\.Contracts(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(forbidden)
            .Because("contracts are shared with other modules and workers and must not leak implementation details");

        rule.WithoutRequiringPositiveResults().Check(CommerceArchitecture.Architecture);
    }

    [Fact]
    public void Gateway_does_not_depend_on_business_modules()
    {
        var rule = Types().That().ResideInNamespaceMatching(@"^Commerce\.Gateway(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^Commerce\.Modules\..*$")
            .Because("the gateway is a security boundary and routes traffic; it holds no business logic");

        rule.WithoutRequiringPositiveResults().Check(CommerceArchitecture.Architecture);
    }

    [Theory]
    [MemberData(nameof(Workers))]
    public void Workers_use_modules_only_through_contracts(string worker)
    {
        var rule = Types().That().ResideInNamespaceMatching($@"^Commerce\.Workers\.{worker}(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(AnyModuleInternals)
            .Because("workers are deployed separately and must not share a module's internal model or tables");

        rule.WithoutRequiringPositiveResults().Check(CommerceArchitecture.Architecture);
    }

    [Fact]
    public void Shared_kernel_depends_on_nothing_else_in_the_workload()
    {
        var rule = Types().That().ResideInNamespaceMatching(@"^Commerce\.SharedKernel(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^Commerce\.(BuildingBlocks|Modules|Workers|Api|Gateway)(\..*)?$");

        rule.WithoutRequiringPositiveResults().Check(CommerceArchitecture.Architecture);
    }
}
