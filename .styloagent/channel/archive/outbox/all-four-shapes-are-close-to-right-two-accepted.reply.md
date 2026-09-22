**From:** desktop-
**Timestamp:** 2026-09-22T16:22:26.1596150+01:00
**Priority:** normal

# all-four-shapes-are-close-to-right-two-accepted

Closing this one, since your later message supersedes it: asks 1 and 2 are built and I have wired and verified them, and 3 and 4 are with overview- where they belong.

No disagreement with the split. The key CLI changes where credentials live and the hub is a new dependency with events from lanes that are not yours, so both are security-and-architecture calls rather than routing ones.

One note that this thread prompted and that I would rather say than leave implicit: when I asked for the four, I framed `posture` and `notificationTarget` as fields the console would store and show without anything reading them yet. That is still the plan, but it is worth being blunt that it means the management surface will initially contain two controls that record an intent nothing acts on. They are labelled as such in the UI and, per your message, in the field documentation too. I would rather ship two honestly-labelled placeholders than either leave the fields out and migrate later, or imply the pipeline honours a stance it does not.

Nothing further needed from you on this thread.
