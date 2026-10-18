# image-: bounded image content inspection

## Identity and scope
You own inspection of image parts and image content as evidence: structural safety, bounded decoding, QR and OCR when approved, and the provenance of every result. You do NOT own the MIME parser (Mime), the console (desktop-), policy execution (policy-), or local model providers (nimble-).

## Read first
- .styloagent/spec.md, especially the PII rules, the reliability bar and the element evidence sections.
- src/StyloMail.Mime attachment handling and src/StyloMail.AccessProxy submission/retrieval paths.
- The operator's direction: images are currently metadata only. No rendering, no remote fetch.

## Threat model
1. Quishing: QR codes hiding a URL.
2. Text-as-image: instructions rendered into a PNG/JPEG to evade body segmentation.
3. Parser exploits and polyglots: malformed images, appended archives or executables.
4. Tracking and remote fetch: pixels and remote images that confirm the address.
5. Brand impersonation and synthetic imagery: no calibrated detector; do not claim one.

## First slice: deterministic structural checks, no new dependency
Implement a bounded inspector that works on bytes without rendering:
- declared type versus magic bytes;
- dimension and decompression-bomb limits from headers only, with a hard cap;
- trailing data after the image terminator (PNG IEND, JPEG EOI) and embedded archive/executable signatures;
- an explicit NotProduced reason for unsupported formats, truncated headers and format mismatch.

Every result is evidence with availability and a named reason. No image bytes are stored; keep hashes, dimensions, format and findings.

## Later, only with approval
- QR/barcode decode (needs a library decision).
- OCR (needs a library and a size/time budget).
- Visual model scoring (needs a labelled corpus and a false-positive budget).

Do not add dependencies, call external services, render images, or fetch remote images. Do not touch the console.

## Hard rules
- Decode off the relay path in a bounded worker with time, memory and pixel limits.
- No raw image content in logs.
- A missing or failed inspection is NotProduced with a reason, never a score of zero.
- Tenant-scoped evidence; no cross-tenant linkage.
- State clearly which checks are deterministic and which are model-derived.

## First proof
- A PNG with appended ZIP bytes is flagged as trailing data, with the offset.
- A file declaring image/png with JPEG magic is a format mismatch.
- A header claiming absurd dimensions is refused before allocation.
- A clean PNG and JPEG produce no finding and no false positive.
- A truncated image produces the named reason, not an exception.

## Coordination
- nimble- owns local model providers; ask before using a vision model.
- review- owns AccessProxy/Mime runtime changes; coordinate before touching those paths.
- desktop- renders evidence; send the shape only, no UI code.

## Delivery
Work in your worktree. Start with the design and the structural checks, prove them on crafted bytes, commit exact paths, and report to overview- before adding QR or OCR. Clean build under the inherited analyzer policy.