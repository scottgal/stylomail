namespace StyloMail.Desktop.Tests;

/// <summary>
/// Bodies as the Host actually puts them on the wire.
/// </summary>
/// <remarks>
/// Written out by hand rather than produced by serialising the client's own
/// DTOs, and that is the entire value of the file. A round trip through the
/// client's types would prove only that the client agrees with itself: it would
/// still pass if the Host renamed a field, changed an enum member, or started
/// emitting an ordinal instead of a name.
///
/// These strings are the contract, transcribed from
/// <c>src/StyloMail.Host/Contracts/</c> and from <c>HostJson.Options</c>, which
/// sets <see cref="System.Text.Json.JsonSerializerDefaults.Web"/> plus
/// <c>JsonStringEnumConverter</c>. Two properties follow from that and both are
/// load-bearing: keys are camelCase, and enums are names rather than numbers.
///
/// The enums are expected to drift loudly. If the Host renames one, these
/// fixtures fail here, in a test named after the contract, rather than at
/// runtime in front of an operator.
/// </remarks>
internal static class Wire
{
    /// <summary>
    /// A full decision record: every field of <c>DecisionResponse</c> populated,
    /// as <c>GET /v1/decisions/{id}</c> returns it.
    /// </summary>
    public const string Decision = """
        {
          "assessmentId": "asm_0f4d2a",
          "internalMessageId": "msg_9c1b7e",
          "action": "Quarantine",
          "proposedActionInShadow": null,
          "riskIndex": 0.82,
          "reasons": [
            {
              "code": "credential_request_high",
              "message": "Message requests credentials and the sender has no trusted history.",
              "evidenceSignalIds": ["sig_cred", "sig_hist"]
            },
            {
              "code": "link_display_mismatch",
              "message": "Displayed link text names one host and the target names another.",
              "evidenceSignalIds": ["sig_link"]
            }
          ],
          "riskDimensions": [
            {
              "name": "semantic",
              "score": 0.91,
              "availability": "Available",
              "evidenceSignalIds": ["sig_cred"]
            },
            {
              "name": "behavioural",
              "score": 0.42,
              "availability": "ReducedCoverage",
              "evidenceSignalIds": ["sig_hist"]
            }
          ],
          "evidence": [
            {
              "signalId": "sig_cred",
              "origin": "Semantic",
              "availability": "Available",
              "value": 0.99,
              "confidence": null,
              "sampleSupport": null,
              "sourceVersion": "jev-1.13.0",
              "observedAt": "2026-09-22T10:00:00+00:00",
              "observedScope": null
            },
            {
              "signalId": "sig_hist",
              "origin": "Behavioural",
              "availability": "ReducedCoverage",
              "value": 0.42,
              "confidence": 0.6,
              "sampleSupport": 3,
              "sourceVersion": "adaptive-1",
              "observedAt": "2026-09-22T09:59:58+00:00",
              "observedScope": "sender"
            },
            {
              "signalId": "sig_link",
              "origin": "Deterministic",
              "availability": "Available",
              "value": 1.0,
              "confidence": null,
              "sampleSupport": null,
              "sourceVersion": "mime-1",
              "observedAt": "2026-09-22T09:59:57+00:00",
              "observedScope": null
            }
          ],
          "versions": {
            "policyVersion": "policy-7",
            "classifierModelVersion": "jev-1.13.0",
            "questionSchemaVersion": "questions-3",
            "preprocessingVersion": "preprocess-2",
            "regimeId": null
          },
          "coverage": {
            "bodyParsed": true,
            "htmlPresent": true,
            "hasAttachments": false,
            "htmlTextDisagreement": true,
            "parserLimitExceeded": false,
            "contentEncrypted": false,
            "truncated": false,
            "conversationContextMissing": true
          },
          "cache": {
            "hit": true,
            "keyDigest": "sha256:6b1f0c",
            "cachedAt": "2026-09-22T09:12:00+00:00",
            "modelVersion": "jev-1.13.0",
            "stale": false
          },
          "recipients": [
            {
              "recipient": "alice@example.test",
              "recipientRisk": 0.8,
              "action": "Quarantine",
              "deliveryState": "Quarantined",
              "reEvaluateBy": "2026-09-23T10:00:00+00:00"
            }
          ],
          "assessedAt": "2026-09-22T10:00:00+00:00"
        }
        """;

    /// <summary>
    /// The same decision with the optional members absent, which is how a cache
    /// miss and a never-held message are represented.
    /// </summary>
    public const string DecisionWithoutOptionalMembers = """
        {
          "assessmentId": "asm_0f4d2b",
          "internalMessageId": "msg_9c1b7f",
          "action": "Allow",
          "proposedActionInShadow": null,
          "riskIndex": 0.04,
          "reasons": [],
          "riskDimensions": [],
          "evidence": [],
          "versions": {
            "policyVersion": "policy-7",
            "classifierModelVersion": null,
            "questionSchemaVersion": "questions-3",
            "preprocessingVersion": "preprocess-2",
            "regimeId": null
          },
          "coverage": {
            "bodyParsed": true,
            "htmlPresent": false,
            "hasAttachments": false,
            "htmlTextDisagreement": false,
            "parserLimitExceeded": false,
            "contentEncrypted": false,
            "truncated": false,
            "conversationContextMissing": false
          },
          "cache": null,
          "recipients": [],
          "assessedAt": "2026-09-22T10:00:00+00:00"
        }
        """;

    /// <summary>A shadow-mode decision: the action taken is not the action proposed.</summary>
    public const string DecisionInShadowMode = """
        {
          "assessmentId": "asm_0f4d2c",
          "internalMessageId": "msg_9c1b80",
          "action": "Allow",
          "proposedActionInShadow": "Quarantine",
          "riskIndex": 0.77,
          "reasons": [],
          "riskDimensions": [],
          "evidence": [],
          "versions": {
            "policyVersion": "policy-7",
            "classifierModelVersion": "jev-1.13.0",
            "questionSchemaVersion": "questions-3",
            "preprocessingVersion": "preprocess-2",
            "regimeId": null
          },
          "coverage": {
            "bodyParsed": true,
            "htmlPresent": false,
            "hasAttachments": false,
            "htmlTextDisagreement": false,
            "parserLimitExceeded": false,
            "contentEncrypted": false,
            "truncated": false,
            "conversationContextMissing": false
          },
          "cache": null,
          "recipients": [],
          "assessedAt": "2026-09-22T10:00:00+00:00"
        }
        """;

    /// <summary><c>GET /v1/submissions/{id}</c>.</summary>
    public const string SubmissionStatus = """
        {
          "queueId": "q_5a2f",
          "state": "Held",
          "attempts": 2,
          "nextAttemptAt": "2026-09-22T10:05:00+00:00",
          "expiresAt": "2026-09-29T10:00:00+00:00",
          "createdAt": "2026-09-22T09:58:00+00:00",
          "updatedAt": "2026-09-22T10:01:00+00:00",
          "purgedAt": null,
          "recipients": [
            {
              "recipient": "alice@example.test",
              "state": "Held",
              "attempts": 2,
              "lastAttemptAt": "2026-09-22T10:01:00+00:00",
              "deliveredAt": null,
              "reEvaluateBy": "2026-09-23T10:00:00+00:00"
            },
            {
              "recipient": "bob@example.test",
              "state": "Delivered",
              "attempts": 1,
              "lastAttemptAt": "2026-09-22T10:00:30+00:00",
              "deliveredAt": "2026-09-22T10:00:30+00:00",
              "reEvaluateBy": null
            }
          ]
        }
        """;

    /// <summary><c>POST /v1/quarantine/{id}/release</c>.</summary>
    public const string QuarantineRelease = """
        {
          "queueId": "q_5a2f",
          "released": true,
          "releasedBy": "reviewer@example.test"
        }
        """;

    /// <summary><c>POST /v1/controls/senders/{id}/pause</c>.</summary>
    public const string SenderPaused = """
        {
          "principalId": "sender@example.test",
          "paused": true,
          "updatedBy": "admin@example.test"
        }
        """;

    /// <summary><c>POST /v1/controls/senders/{id}/resume</c>.</summary>
    public const string SenderResumed = """
        {
          "principalId": "sender@example.test",
          "paused": false,
          "updatedBy": "admin@example.test"
        }
        """;

    /// <summary><c>POST /v1/feedback</c>.</summary>
    public const string FeedbackRecorded = """
        {
          "feedbackId": "fbk_31c8",
          "decisionId": "asm_0f4d2a",
          "scope": "Recipient",
          "label": "Legitimate",
          "recipient": "alice@example.test",
          "recordedAt": "2026-09-22T10:02:00+00:00"
        }
        """;

    /// <summary><c>GET /health/ready</c> when the Host can durably accept mail.</summary>
    public const string Ready = """
        {
          "status": "ready"
        }
        """;

    /// <summary>
    /// <c>GET /health/ready</c> when it cannot, with the failed checks named.
    /// </summary>
    /// <remarks>
    /// The failed check names are the Host's own strings from
    /// <c>ReadinessProbe</c>. They describe a capability, never a message, an
    /// identity or a tenant, which is the compensation for the route being
    /// unauthenticated.
    /// </remarks>
    public const string NotReady = """
        {
          "status": "not_ready",
          "failedChecks": ["spool_directory_not_writable", "database_unavailable"]
        }
        """;

    /// <summary>The Host's error body, as <c>EndpointResults</c> produces it.</summary>
    public static string Error(string code, string detail) => $$"""
        {
          "error": "{{code}}",
          "detail": "{{detail}}"
        }
        """;

    /// <summary>The 422 body a message that could not be read produces.</summary>
    public static string Unprocessable(string code, string detail, string disposition) => $$"""
        {
          "error": "{{code}}",
          "detail": "{{detail}}",
          "disposition": "{{disposition}}",
          "reason": null,
          "limitName": null
        }
        """;

    /// <summary>A decision body with an enum member this client does not know.</summary>
    public static string DecisionWithAction(string action) => $$"""
        {
          "assessmentId": "asm_0f4d2a",
          "internalMessageId": "msg_9c1b7e",
          "action": "{{action}}",
          "proposedActionInShadow": null,
          "riskIndex": 0.5,
          "reasons": [],
          "riskDimensions": [],
          "evidence": [],
          "versions": {
            "policyVersion": "policy-7",
            "classifierModelVersion": null,
            "questionSchemaVersion": "questions-3",
            "preprocessingVersion": "preprocess-2",
            "regimeId": null
          },
          "coverage": {
            "bodyParsed": true,
            "htmlPresent": false,
            "hasAttachments": false,
            "htmlTextDisagreement": false,
            "parserLimitExceeded": false,
            "contentEncrypted": false,
            "truncated": false,
            "conversationContextMissing": false
          },
          "cache": null,
          "recipients": [],
          "assessedAt": "2026-09-22T10:00:00+00:00"
        }
        """;
}
