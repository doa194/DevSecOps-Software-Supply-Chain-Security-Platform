// Masks classified fields in JSON responses for callers without clearance.
//
// A response DTO property marked [PersonalData] or [FinancialData] is written unmasked
// only when the caller either owns the record (the endpoint says so) or holds the
// matching clearance permission. Otherwise the value is replaced, e.g. "c***" for
// "carol@example.test". Secret-classified properties are never serialised at all.
// Because masking hooks into serialisation, a new classified field is protected the moment
// it is annotated; no endpoint can forget to mask it.
using System.Text.Json.Serialization.Metadata;
using Commerce.BuildingBlocks.Security;
using Commerce.SharedKernel.Classification;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Compliance.Classification;

namespace Commerce.BuildingBlocks.Classification;

public static class DataClearance
{
    private const string OwnerViewKey = "sscp.clearance.owner-view";

    // Called by endpoints that return the caller's own data (their profile, their order).
    public static void GrantOwnerView(HttpContext context) => context.Items[OwnerViewKey] = true;

    public static bool CanSeeUnmasked(HttpContext? context, DataClassification classification)
    {
        if (context is null)
        {
            return false;
        }

        if (context.Items.TryGetValue(OwnerViewKey, out var owner) && owner is true)
        {
            return true;
        }

        var roles = context.User.FindAll(KeycloakClaims.Role).Select(c => c.Value);
        if (classification == CommerceDataClasses.Personal)
        {
            return PermissionMap.Grants(roles, Permissions.PersonalDataRead);
        }

        return classification == CommerceDataClasses.Financial && PermissionMap.Grants(roles, Permissions.FinancialDataRead);
    }

    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : $"{value[0]}***";
}

public sealed class ClassificationMasking(IHttpContextAccessor accessor)
{
    public void Apply(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var property in typeInfo.Properties.ToList())
        {
            var classification = (property.AttributeProvider?
                .GetCustomAttributes(typeof(DataClassificationAttribute), inherit: true)
                .OfType<DataClassificationAttribute>()
                .FirstOrDefault())?.Classification;
            if (classification is null || classification == CommerceDataClasses.Public || classification == CommerceDataClasses.Internal)
            {
                continue;
            }

            if (classification == CommerceDataClasses.Secret)
            {
                typeInfo.Properties.Remove(property);
                continue;
            }

            var getter = property.Get;
            if (getter is null)
            {
                continue;
            }

            var dataClass = classification.Value;
            property.Get = target =>
            {
                var value = getter(target);
                if (DataClearance.CanSeeUnmasked(accessor.HttpContext, dataClass))
                {
                    return value;
                }

                return value is string text ? DataClearance.Mask(text) : null;
            };
        }
    }
}
