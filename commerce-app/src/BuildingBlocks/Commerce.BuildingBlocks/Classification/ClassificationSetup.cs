// Enforcement of the data classification model (see Commerce.SharedKernel.Classification).
//
// Logs: when a log parameter or logged property is classified, the redaction framework
// replaces it before it leaves the process.
//   - Personal  -> HMAC-SHA256 (the same e-mail always gives the same token, so incidents
//                  can still be correlated, but the value itself is not in the logs)
//   - Financial -> erased
//   - Secret    -> erased
// API responses: see ClassificationMasking.
using Commerce.SharedKernel.Classification;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Commerce.BuildingBlocks.Classification;

public static class ClassificationSetup
{
    public static IHostApplicationBuilder AddCommerceRedaction(this IHostApplicationBuilder builder)
    {
        // The HMAC key is a secret delivered like any other runtime secret; a random key is
        // generated when none is configured (tokens are then only stable per process).
        var key = builder.Configuration["Telemetry:RedactionKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        }

        builder.Services.AddRedaction(redaction =>
        {
            redaction.SetHmacRedactor(options =>
            {
                options.Key = key;
                options.KeyId = 1;
            }, new DataClassificationSet(CommerceDataClasses.Personal));
            redaction.SetRedactor<ErasingRedactor>(
                new DataClassificationSet(CommerceDataClasses.Financial),
                new DataClassificationSet(CommerceDataClasses.Secret));
        });
        builder.Logging.EnableRedaction();
        return builder;
    }
}
