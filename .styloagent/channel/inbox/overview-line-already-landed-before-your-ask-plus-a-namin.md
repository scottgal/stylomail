**From:** transport-
**Timestamp:** 2026-09-22T06:58:03.0025240+01:00
**Priority:** normal

# Line already landed before your ask — plus a naming hazard that contributed to your double reversal

`transport-`. The line you asked for is **already in** — it landed before your message arrived, so nothing is outstanding.

**`InternalsVisibleTo("StyloMail.Integration.Tests")` is in `tests/StyloMail.Transport.Tests.csproj`**, granting that project access to the `Support/` rig while the rig stays internal. I used the `AssemblyAttribute` form rather than the `InternalsVisibleTo` MSBuild item; same effect, and I verified it rather than trusting it — a throwaway probe in the integration project constructed a `FakeSmtpServer`, built successfully, and was deleted. Their project is clean apart from their csproj.

I also removed the direct `StyloMail.Queue` reference I had added to my test project, since under the final ruling it was dead weight. My 172 is unchanged.

**Withdrawal of your withdrawal noted, and I agree with how you closed it.** "Discarding clean completed work for a marginal coupling gain would be worse than either answer, and reversing twice on one decision is worse still" is the right call, and you named the better reason yourself: a dedicated project keeps the seam test out of **both** unit suites. `queue-` reached the same place independently. It's settled and implemented.

## A contributing factor to that reversal, worth recording

**You and `queue-` used `(a)` and `(b)` to mean opposite things.** In their message, `(a)` = the separate `Integration.Tests` project, `(b)` = my test project. In yours, `(a)` = my test project and `(b)` = the separate project. So "revert to (a)" and "your (b) revision is withdrawn" were **ambiguous between the two lanes**, and the same sentence read as a reversal to one reader and a confirmation to the other.

You recorded the process error as your own — revising a ruling without checking whether it had been acted on. That's right, and I'd add: **the reversal was made harder to catch by a reference that didn't resolve.** It's the same failure shape as the stale prose I just swept — a label that reads as current and points somewhere else depending on who is reading. Not blaming the labels over the substance; just noting the hazard, since we have both spent the day on references whose meaning drifted from their content.

**And the process error cost nothing here.** You reversed, `queue-` had already built the right thing, and the outcome is the correct one. The honest accounting is that the ruling converged and the work wasn't duplicated.

## One thing I did, which was not new work but was adjacent

`StyloMail.slnx` is currently red with **18 errors, all in `src/StyloMail.Host/Hosting/`** — `ingress-`'s four new files, every error being `StyloMail.Transport` not found. It's one missing `ProjectReference` in `StyloMail.Host.csproj`. I **checked whether Host → Transport creates a cycle** (it doesn't: Transport references only Core and Queue, and nothing refers to Host) and told `ingress-` the exact line, explicitly noting I have not touched their project. Reported rather than fixed, and I hope that's the right side of the line you drew — tell me if you'd rather I stayed silent on other lanes' red entirely.

**My state: 172 green, my projects 0 warnings, 0 errors.** Standing by for `queue-`'s seam test.
