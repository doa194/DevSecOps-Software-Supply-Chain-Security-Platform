// The data classification model. Every property that carries personal, financial or
// secret data is annotated with one of these classes where it is defined. Log redaction
// and API response masking read the annotations, so a new field is protected by labelling
// it rather than by remembering to mask it in every log statement.
using Microsoft.Extensions.Compliance.Classification;

namespace Commerce.SharedKernel.Classification;

public static class CommerceDataClasses
{
    private const string Taxonomy = "Commerce";

    // Safe to show to anyone, for example product names and prices.
    public static DataClassification Public { get; } = new(Taxonomy, nameof(Public));

    // Business data for staff only, for example stock levels and internal notes.
    public static DataClassification Internal { get; } = new(Taxonomy, nameof(Internal));

    // Personal data: names, e-mail addresses, phone numbers, postal addresses.
    public static DataClassification Personal { get; } = new(Taxonomy, nameof(Personal));

    // Payment and financial details, for example card fingerprints and refund reasons.
    public static DataClassification Financial { get; } = new(Taxonomy, nameof(Financial));

    // Credentials and keys. Must never appear in logs, events or API responses.
    public static DataClassification Secret { get; } = new(Taxonomy, nameof(Secret));
}

public sealed class PublicDataAttribute() : DataClassificationAttribute(CommerceDataClasses.Public);
public sealed class InternalDataAttribute() : DataClassificationAttribute(CommerceDataClasses.Internal);
public sealed class PersonalDataAttribute() : DataClassificationAttribute(CommerceDataClasses.Personal);
public sealed class FinancialDataAttribute() : DataClassificationAttribute(CommerceDataClasses.Financial);
public sealed class SecretDataAttribute() : DataClassificationAttribute(CommerceDataClasses.Secret);
