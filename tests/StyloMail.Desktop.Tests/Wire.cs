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
          "internalMessageId": "msg_9c1b7e",
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

    /// <summary>
    /// <c>GET /v1/decisions</c>, transcribed from <c>DecisionListingResponse</c>.
    /// Rows are summaries: reasons and coverage, no evidence.
    /// </summary>
    public const string DecisionListing = """
        {
          "tenantId": "smoke",
          "action": null,
          "decisions": [
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
                "conversationContextMissing": false
              },
              "assessedAt": "2026-09-22T10:00:00+00:00"
            }
          ],
          "nextCursor": null,
          "hasMore": false
        }
        """;

    /// <summary>
    /// The ledger filtered to a message that has never been assessed. An empty
    /// page, not a 404: "no decisions" and "no such message" are different
    /// facts and the ledger can only answer the first.
    /// </summary>
    public const string EmptyDecisionListing = """
        {
          "tenantId": "smoke",
          "action": null,
          "decisions": [],
          "nextCursor": null,
          "hasMore": false
        }
        """;

    /// <summary>
    /// <c>GET /v1/senders/{id}/settings</c>, transcribed from
    /// <c>SenderSettingsResponse</c>.
    /// </summary>
    public const string SenderSettings = """
        {
          "principalId": "acme-outbound",
          "label": "Acme outbound",
          "companyId": "co_7f3a",
          "notes": "Primary marketing account",
          "externalRef": "crm-99213",
          "notificationTarget": "ops@acme.test",
          "posture": "watch",
          "updatedBy": "ops@acme.test",
          "updatedAt": "2026-09-22T15:04:11.1234567+00:00"
        }
        """;

    /// <summary>
    /// A principal nobody has described: 200 with nulls, not a 404. The
    /// principal exists and is in the listing, so a 404 would read as "no such
    /// sender" and send an operator looking for something that is right there.
    /// </summary>
    public const string SenderSettingsUndescribed = """
        {
          "principalId": "quiet@example.test",
          "label": null,
          "companyId": null,
          "notes": null,
          "externalRef": null,
          "notificationTarget": null,
          "posture": null,
          "updatedBy": null,
          "updatedAt": null
        }
        """;

    /// <summary><c>GET /v1/companies</c>.</summary>
    public const string CompanyListing = """
        {
          "tenantId": "smoke",
          "companies": [
            {
              "companyId": "co_7f3a",
              "name": "Acme",
              "notes": "Everything under the Acme brand",
              "updatedBy": "ops@acme.test",
              "updatedAt": "2026-09-22T15:00:00+00:00"
            }
          ]
        }
        """;

    /// <summary>One company, as a create or update answers.</summary>
    public const string Company = """
        {
          "companyId": "co_7f3a",
          "name": "Acme",
          "notes": null,
          "updatedBy": "ops@acme.test",
          "updatedAt": "2026-09-22T15:00:00+00:00"
        }
        """;

    /// <summary><c>GET /v1/senders</c>, transcribed from <c>SenderListingResponse</c>.</summary>
    public const string SenderListing = """
        {
          "tenantId": "smoke",
          "senders": [
            {
              "principalId": "compromised@example.test",
              "label": "Acme outbound",
              "companyId": "co_7f3a",
              "control": {
                "paused": true,
                "pausedAt": "2026-09-22T09:30:00+00:00",
                "reason": "credential stuffing from this account",
                "resumedAt": null,
                "resumedBy": null,
                "resumeReason": null,
                "updatedBy": "admin@example.test",
                "updatedAt": "2026-09-22T09:30:00+00:00"
              }
            },
            {
              "principalId": "quiet@example.test",
              "label": null,
              "companyId": null,
              "control": {
                "paused": false,
                "pausedAt": "2026-09-20T09:00:00+00:00",
                "reason": "held for review",
                "resumedAt": "2026-09-20T11:00:00+00:00",
                "resumedBy": "admin@example.test",
                "resumeReason": "cleared by review",
                "updatedBy": "admin@example.test",
                "updatedAt": "2026-09-20T11:00:00+00:00"
              }
            },
            {
              "principalId": "untouched@example.test",
              "label": null,
              "companyId": null,
              "control": {
                "paused": false,
                "pausedAt": null,
                "reason": null,
                "resumedAt": null,
                "resumedBy": null,
                "resumeReason": null,
                "updatedBy": null,
                "updatedAt": null
              }
            }
          ]
        }
        """;

    /// <summary>
    /// <c>GET /v1/messages</c>, transcribed from <c>MessageListingResponse</c>.
    /// Rows are <c>SubmissionStatusResponse</c>, the same projection
    /// <c>GET /v1/submissions/{id}</c> serves.
    /// </summary>
    public const string MessageListing = """
        {
          "tenantId": "smoke",
          "state": "held",
          "messages": [
            {
              "queueId": "q_5a2f",
              "internalMessageId": "msg_9c1b7e",
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
            },
            {
              "queueId": "q_6b31",
              "internalMessageId": "msg_7d2c",
              "state": "Quarantined",
              "attempts": 1,
              "nextAttemptAt": null,
              "expiresAt": null,
              "createdAt": "2026-09-22T09:40:00+00:00",
              "updatedAt": "2026-09-22T09:40:05+00:00",
              "purgedAt": null,
              "recipients": []
            }
          ],
          "nextCursor": "cursor_page_2",
          "hasMore": true
        }
        """;

    /// <summary>The same listing on its last page: no cursor, nothing more to fetch.</summary>
    public const string MessageListingLastPage = """
        {
          "tenantId": "smoke",
          "state": "held",
          "messages": [],
          "nextCursor": null,
          "hasMore": false
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
