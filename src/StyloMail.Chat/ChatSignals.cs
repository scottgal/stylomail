namespace StyloMail.Chat;

/// <summary>
/// The deterministic signals this connector produces, and the version it stamps them with.
/// </summary>
/// <remarks>
/// <b>The signal ids are the same strings the MIME adapter uses, deliberately.</b> A link whose label
/// disagrees with its destination means the same thing on a chat channel as it does in mail, and a
/// policy asking about it should not have to know which channel produced the answer. What differs
/// between the two is <see cref="SourceVersion"/>, which names the producer, so a reader can still
/// tell where a signal came from and which version of the rules produced it.
/// </remarks>
public static class ChatSignals
{
    /// <summary>Link label versus link destination. Value: ratio of labelled links that disagree.</summary>
    public const string LinkDisplayMismatch = "deterministic.link_display_mismatch";

    /// <summary>Links whose host is an internationalised domain. Value: count.</summary>
    public const string LinkIdn = "deterministic.link_idn";

    /// <summary>Links whose host carries confusables or mixed scripts. Value: count.</summary>
    public const string LinkIdnHomograph = "deterministic.link_idn_homograph";

    /// <summary>
    /// Stamped on every piece of evidence this connector produces.
    /// </summary>
    /// <remarks>
    /// Part of the decision ledger's contract: bumping it says the rules changed, which is what lets
    /// a replayed decision be recognised as having been made by an older rule set.
    /// </remarks>
    public const string SourceVersion = "stylomail-chat/1";
}
