namespace StyloMail.Desktop.Models;

/// <summary>One risk category as the console presents it.</summary>
/// <remarks>
/// This is a presentation mirror, not a second policy engine. The Host is authoritative for the
/// category ids and for the effective level; the local copy exists because the Host serves category
/// levels but not their operator-facing names, descriptions or hard-floor status. A test pins the
/// ids, labels and hard-floor flags against the Host's <c>PolicyCategories</c> contract so a backend
/// change cannot silently diverge.
/// </remarks>
public sealed record PolicyCategoryInfo(string Id, string Label, string Description, bool IsHardFloor);

/// <summary>The closed category catalogue, in the Host's stable display order.</summary>
public static class PolicyCategoryCatalog
{
    public const string HardFloorNote =
        "Hard floor: never below Strict. Lowering it needs a code change and a recorded operator decision, not a configuration edit.";

    public static IReadOnlyList<PolicyCategoryInfo> All { get; } =
    [
        new("child_safety_grooming", "Child safety and grooming",
            "Sexual content involving minors, grooming, and attempts to isolate a young person.", true),
        new("credible_threats_of_violence", "Credible threats of violence",
            "A specific threat to harm a person, with the means or intent to carry it out.", true),
        new("illegal_content", "Illegal content",
            "Material whose possession or distribution is unlawful in the declared jurisdiction.", true),
        new("coercion_and_extortion", "Coercion and extortion",
            "Pressure, blackmail or threats used to force a decision or a payment.", false),
        new("adult_content", "Adult content",
            "Sexual content between adults, where the audience and jurisdiction permit it.", false),
        new("harassment", "Harassment",
            "Targeted abuse, repeated unwanted contact, or intimidating language.", false),
        new("impersonation", "Impersonation",
            "Pretending to be a person, brand or service to gain trust.", false),
        new("credential_request", "Credential requests",
            "Asking for passwords, one-time codes or account recovery details.", false),
        new("payment_redirection", "Payment redirection",
            "Changing bank details or pushing a payment to a new destination.", false),
        new("malware_and_attachments", "Malware and dangerous attachments",
            "Executable or exploit-bearing files, or links to them.", false),
        new("promotion", "Promotion and marketing",
            "Unsolicited commercial content, offers and campaigns.", false),
        new("unsolicited_mail", "Unsolicited mail",
            "Bulk or unwanted mail that is not otherwise harmful.", false),
    ];

    public static PolicyCategoryInfo? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(category => string.Equals(category.Id, id, StringComparison.Ordinal));

    public static string LabelFor(string? id) => Find(id)?.Label ?? "Unknown category";

    public static string DescriptionFor(string? id) =>
        Find(id)?.Description ?? "The Host reported a category this build does not describe. Treat it as unknown, not safe.";

    public static bool IsHardFloor(string? id) => Find(id)?.IsHardFloor == true;
}

/// <summary>A wire id with the words an operator reads instead of it.</summary>
public sealed record PolicyChoice(string Id, string Label, string Detail = "")
{
    public override string ToString() => Label;
}

/// <summary>The plain-language option sets the policy forms use.</summary>
public static class PolicyChoices
{
    public static PolicyChoice NotDeclared { get; } =
        new("", "Not declared", "The pack does not set a level for this category.");

    public static IReadOnlyList<PolicyChoice> Categories { get; } =
        [.. PolicyCategoryCatalog.All.Select(category =>
            new PolicyChoice(category.Id, category.Label, category.Description))];

    public static IReadOnlyList<PolicyChoice> Levels { get; } =
    [
        new("Off", "Off", "Not scored and not shown. Unavailable for hard floors."),
        new("Observe", "Observe", "Scored and shown, with no action taken."),
        new("Standard", "Standard", "Automatic low-tier actions; more serious cases go to a person."),
        new("Strict", "Strict", "Lower thresholds, automatic hold and fast human review."),
        new("ZeroTolerance", "Zero tolerance",
            "Every candidate goes to a person immediately, with the evidence preserved."),
    ];

    public static IReadOnlyList<PolicyChoice> HardFloorLevels { get; } =
    [
        new("Strict", "Strict", "The lowest level a hard floor may carry."),
        new("ZeroTolerance", "Zero tolerance",
            "Every candidate goes to a person immediately, with the evidence preserved."),
    ];

    public static IReadOnlyList<PolicyChoice> ActionCeilings { get; } =
    [
        new("Review", "Send to a person", "The furthest automatic step is human review."),
        new("Hold", "Hold and re-check", "The furthest automatic step is a bounded hold."),
        new("Quarantine", "Stop before delivery", "The furthest automatic step is containment."),
    ];

    public static IReadOnlyList<PolicyChoice> CaptureModes { get; } =
    [
        new("None", "No content", "No message content is retained."),
        new("MetadataOnly", "Metadata only", "Envelope and header metadata, never the body."),
        new("Excerpt", "Bounded excerpt", "A bounded excerpt for human review."),
        new("Full", "Full body", "The whole body. Needs a second administrator at approval."),
    ];

    public static IReadOnlyList<PolicyChoice> Verticals { get; } =
    [
        new("Commerce", "Commerce"),
        new("Dating", "Dating"),
        new("Education", "Education"),
        new("Healthcare", "Healthcare"),
        new("Enterprise", "Enterprise"),
        new("Unknown", "Unknown or mixed"),
    ];

    public static IReadOnlyList<PolicyChoice> Audiences { get; } =
    [
        new("Adults", "Adults"),
        new("Minors", "Minors"),
        new("Mixed", "Mixed"),
        new("Unknown", "Unknown"),
    ];

    public static IReadOnlyList<PolicyChoice> ScopeKinds { get; } =
    [
        new("Tenant", "Whole service", "Every conversation in this tenant."),
        new("Company", "Company", "Every person filed under one company."),
        new("Domain", "Domain", "Every person at one email domain."),
        new("Entity", "Person", "One person, by their opaque entity id."),
        new("Conversation", "Conversation", "One conversation thread."),
    ];

    public static IReadOnlyList<PolicyChoice> BindingActions { get; } =
    [
        new("Permit", "Permit", "Allow this scope where the pack would otherwise hold or review."),
        new("Observe", "Observe only", "Score and show this scope, but take no automatic action."),
        new("CaptureText", "Capture content", "Allow content capture within the pack's declared mode."),
        new("RaiseTolerance", "Raise tolerance", "Make this scope's category more tolerant. Always safe."),
        new("LowerTolerance", "Lower tolerance", "Make this scope's category less tolerant. Needs a second administrator."),
    ];

    public static IReadOnlyList<PolicyChoice> OnExpiry { get; } =
    [
        new("RevertToPack", "Return to the pack", "The exception ends and the pack applies again."),
        new("HoldForReview", "Hold for review", "The scope stays held until a person reviews it."),
    ];

    public static string LevelLabel(string? id) =>
        Levels.FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.Ordinal))?.Label
        ?? (string.IsNullOrWhiteSpace(id) ? "Not reported" : id);

    public static string Label(IReadOnlyList<PolicyChoice> choices, string? id) =>
        choices.FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.Ordinal))?.Label
        ?? (string.IsNullOrWhiteSpace(id) ? "Not reported" : id);

    public static PolicyChoice Find(IReadOnlyList<PolicyChoice> choices, string? id, string fallback) =>
        choices.FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.Ordinal))
        ?? choices.First(choice => string.Equals(choice.Id, fallback, StringComparison.Ordinal));
}

/// <summary>One starting point for a draft. Not a calibrated profile and not an approval.</summary>
public sealed record PolicyTemplate(
    string Id,
    string Label,
    string Description,
    IReadOnlyDictionary<string, string> Levels,
    string ActionCeiling);

/// <summary>
/// The two client-side starting points the draft form offers.
/// </summary>
/// <remarks>
/// The levels are illustrative defaults, not a claim that a Host was calibrated for them. The form
/// says so and requires the operator to review every level before approval. Jurisdiction, audience,
/// rule-set version and scorer version are never inferred from a template.
/// </remarks>
public static class PolicyTemplates
{
    public static IReadOnlyList<PolicyTemplate> All { get; } =
    [
        new(
            "balanced",
            "Balanced starting point",
            "A starting point for adult traffic with moderate commercial risk. Review every level before approval.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["child_safety_grooming"] = "Strict",
                ["credible_threats_of_violence"] = "Strict",
                ["illegal_content"] = "Strict",
                ["coercion_and_extortion"] = "Strict",
                ["adult_content"] = "Standard",
                ["harassment"] = "Strict",
                ["impersonation"] = "Standard",
                ["credential_request"] = "Strict",
                ["payment_redirection"] = "Strict",
                ["malware_and_attachments"] = "Strict",
                ["promotion"] = "Standard",
                ["unsolicited_mail"] = "Observe",
            },
            "Review"),
        new(
            "high-scrutiny",
            "High scrutiny starting point",
            "A starting point for mixed or unknown audiences and higher-risk traffic. Review every level before approval.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["child_safety_grooming"] = "ZeroTolerance",
                ["credible_threats_of_violence"] = "ZeroTolerance",
                ["illegal_content"] = "ZeroTolerance",
                ["coercion_and_extortion"] = "Strict",
                ["adult_content"] = "Strict",
                ["harassment"] = "Strict",
                ["impersonation"] = "Strict",
                ["credential_request"] = "Strict",
                ["payment_redirection"] = "Strict",
                ["malware_and_attachments"] = "Strict",
                ["promotion"] = "Strict",
                ["unsolicited_mail"] = "Strict",
            },
            "Quarantine"),
    ];
}
