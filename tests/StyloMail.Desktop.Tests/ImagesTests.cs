using System.Net;
using System.Text;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Models;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Tests;

public sealed class ImagesTests
{
    [Fact]
    public async Task Image_observations_are_rendered_in_plain_language()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/proxy/conversations" => Json(ConversationsJson()),
            "/v1/proxy/conversations/abcdef123456/images" => Json(ImagesJson(imagesEnabled: true)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        Assert.Single(model.Conversations);
        var message = Assert.Single(model.Messages);
        Assert.Equal("Message msg1", message.Title);
        Assert.Equal("Inspected", message.StateLabel);
        Assert.Contains("1 image parts", message.CountsLabel, StringComparison.Ordinal);
        var observation = Assert.Single(message.Observations);
        Assert.Equal("Inspected", observation.StateLabel);
        var part = Assert.Single(observation.Parts);
        Assert.Contains("10x10", part.Detail, StringComparison.Ordinal);
        Assert.Contains("Declared PNG", part.Detail, StringComparison.Ordinal);
        Assert.Contains("Detected PNG", part.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Image_inspection_off_is_named()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/proxy/conversations" => Json(ConversationsJson()),
            "/v1/proxy/conversations/abcdef123456/images" => Json(ImagesJson(imagesEnabled: false)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        Assert.Empty(model.Messages);
        Assert.Equal("Image inspection is off on this Host.", model.StatusLabel);
    }

    [Fact]
    public async Task A_host_without_conversation_telemetry_is_named()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/proxy/conversations" => Json("""
                {
                  "tenantId": "t1",
                  "window": "last_24_hours",
                  "windowStart": "2026-10-18T10:00:00+00:00",
                  "windowEnd": "2026-10-19T10:00:00+00:00",
                  "availability": "not_produced",
                  "conversations": [],
                  "nextCursor": null,
                  "hasMore": false
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        Assert.Empty(model.Conversations);
        Assert.Empty(model.Messages);
        Assert.Contains("does not record conversation threads", model.ConversationNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_images_destination_opens_the_images_page()
    {
        var model = ShellModel.CreateDefault();
        var images = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Images");

        model.SelectedItem = images;

        Assert.True(model.IsImagesSelected);
        Assert.False(model.ShowQueuePanes);
    }

    private static ImagesViewModel Model(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5271") };
        return new ImagesViewModel(new ProxyConversationClient(http, new TestApiKeyProvider()));
    }

    private static StubHttpMessageHandler Handler(Func<CapturedRequest, HttpResponseMessage> respond) => new(respond);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ConversationsJson()
        => """
        {
          "tenantId": "t1",
          "window": "last_24_hours",
          "windowStart": "2026-10-18T10:00:00+00:00",
          "windowEnd": "2026-10-19T10:00:00+00:00",
          "availability": "available",
          "conversations": [
            {
              "threadToken": "abcdef123456",
              "accountId": "a1",
              "participantTokens": ["p1"],
              "referenceDepth": 1,
              "lastActivityAt": "2026-10-19T09:00:00+00:00",
              "availability": "available"
            }
          ],
          "nextCursor": null,
          "hasMore": false
        }
        """;

    private static string ImagesJson(bool imagesEnabled)
        => $$"""
        {
          "tenantId": "t1",
          "threadToken": "abcdef123456",
          "window": "last_24_hours",
          "windowStart": "2026-10-18T10:00:00+00:00",
          "windowEnd": "2026-10-19T10:00:00+00:00",
          "provenance": "proxy_images",
          "imagesEnabled": {{(imagesEnabled ? "true" : "false")}},
          "retentionDays": 30,
          "messages": [
            {
              "messageToken": "msg1",
              "accountId": "a1",
              "observedAt": "2026-10-19T09:00:00+00:00",
              "trafficKind": "Retrieval",
              "state": "produced",
              "observations": [
                {
                  "observationKey": "obs1",
                  "observedAt": "2026-10-19T09:00:00+00:00",
                  "state": "produced",
                  "availability": "available",
                  "availabilityReason": null,
                  "counts": {
                    "imageParts": 1,
                    "imagesSkippedAsChrome": 0,
                    "imagesAdmitted": 1,
                    "imagesProduced": 1,
                    "imagesNotProduced": 0,
                    "imagesOverBudget": 0
                  },
                  "firstOverBudgetOrdinal": null,
                  "lastOverBudgetOrdinal": null,
                  "sourceVersion": "1",
                  "expiresAt": "2026-11-18T09:00:00+00:00",
                  "parts": [
                    {
                      "ordinal": 1,
                      "disposition": "inline",
                      "availability": "available",
                      "availabilityReason": null,
                      "format": "Png",
                      "declaredFormat": "Png",
                      "width": 10,
                      "height": 10,
                      "pixelCount": 100,
                      "imageEndOffset": 100,
                      "trailingByteCount": 0,
                      "trailingBytesObserved": 0,
                      "trailingScanComplete": true,
                      "transparencyDeclared": false,
                      "sourceVersion": "1",
                      "findings": []
                    }
                  ]
                }
              ],
              "childrenOverflowCount": 0,
              "childrenNext": null
            }
          ],
          "next": null
        }
        """;
}
