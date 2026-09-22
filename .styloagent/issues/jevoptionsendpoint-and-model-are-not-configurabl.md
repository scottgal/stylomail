**From:** desktop-
**Timestamp:** 2026-09-22T15:00:50.2372400+01:00
**Severity:** low
**Status:** resolved
**Source:** internal

# JevOptions.Endpoint and Model are not configurable: HostServices constructs JevOptions ignoring configuration

Found by desktop- while trying to point a local smoke Host at an unreachable semantic provider so the pipeline would degrade to unavailable evidence. Not my lane; reporting rather than patching.

src/StyloMail.Host/Hosting/HostServices.cs, BuildAssessor:

    var classifier = new JevSemanticMailClassifier(
        services.GetRequiredService<HttpClient>(),
        new JevOptions { ApiKey = jevApiKey! },
        services.GetRequiredService<TimeProvider>());

JevOptions has Endpoint and Model properties, both configurable in shape, and the Model property carries a substantial comment about deliberately pinning jev-1.13.0 rather than using the moving jev-latest alias. Neither is read from IConfiguration here, and the method has IConfiguration in scope as a parameter. So:

- Endpoint is always https://api.typesafe.ai/v1/systemone. A deployment cannot point at a proxy, a gateway, or a regional endpoint, and I could not redirect a local smoke run either.
- Model is always the compiled default. The pin is correct today, but changing it is a code change and a redeploy rather than configuration, which is worth knowing given the pin is the thing the cash-invalidation reasoning depends on.

The ApiKey arriving from the environment is right and should stay that way; this is only about the two non-secret options.

Two consequences worth weighing:

1. It is a latent drift risk rather than a live bug. The defaults are correct, so nothing is broken today. It becomes a bug the first time someone needs a different endpoint or a different model pin in a deployment, and the natural attempt (set StyloMail__Jev__Endpoint) will appear to be ignored with no error anywhere, because configuration binding is simply absent rather than failing.

2. It is currently load-bearing for me. Pointing the endpoint at an address that cannot answer is how I would have exercised the "semantic unavailable" path end to end on a Host with no real provider key. I could not, so the console's decision pane is photographed against the transcribed wire body instead of a real decision, and I have said so rather than implying otherwise.

Suggested shape, for whoever owns it: bind the section and take the key from the environment on top, so the secret never gains a config key. HostCredentials already argues at length that a secret in a config key ends up in an appsettings file, then a repository, then an image layer; the non-secret options have no such argument against them.

Discovered 2026-09-22. I have not changed anything.

---

**RESOLVED (verified by `overview-`, 2026-09-22).** HostServices.BuildJevOptions now takes IConfiguration and reads the section, so the endpoint and model are configurable rather than constructed from defaults.
