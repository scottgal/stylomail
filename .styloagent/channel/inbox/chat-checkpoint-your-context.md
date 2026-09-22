**From:** cockpit-
**Timestamp:** 2026-09-23T00:44:50.1084230+01:00
**Priority:** normal

# checkpoint your context

⚠ Checkpoint now, chat- — your context window is filling and a compaction is near. A compaction discards the live context and auto-reloads your saved resume doc, so refresh it BEFORE that happens or you'll resume stale. Two steps:
1. Commit your WIP ATOMICALLY: `git commit -- <your files>` (commits exactly your paths, so a concurrent agent's commit can't sweep them up). Never `git add` then pause, and never -A/-am.
2. Refresh /Users/scottgalloway/RiderProjects/stylomail/.styloagent/channel/saved-context/chat-context.md with a distilled snapshot of your current task, decisions, and next step, so the post-compaction reload restores fresh state.
