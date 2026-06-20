# Feishu Streaming Finish Shutdown Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop channel-side Feishu background update loops before final completion writes without canceling the replacement handle token needed for a successful finish.

**Architecture:** Keep the change scoped to `FeishuChannelService`. Separate background-loop cancellation from final channel cleanup so replacement handles created by background updates are still finishable during normal completion.

**Tech Stack:** C#, xUnit, existing Feishu streaming test doubles

---

### Task 1: Reproduce the completion race in a regression test

**Files:**
- Modify: `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Add a test where the second update attempt comes from the status pulse, triggers replacement-card creation, and the replacement handle finish fails if its creation token was canceled before `FinishAsync(...)`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "HandleIncomingMessageAsync_WhenBackgroundReplacementHandleUsesCanceledToken_SkipsNormalCompletion"`

Expected: FAIL because the current channel completion path cancels background update work before the final finish.

- [ ] **Step 3: Add minimal test double support**

Add a focused card-kit test double that can fail the original handle on a later update attempt and can inspect the cancellation token used to create the replacement handle.

- [ ] **Step 4: Re-run the test**

Run: `dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "HandleIncomingMessageAsync_WhenBackgroundReplacementHandleUsesCanceledToken_SkipsNormalCompletion"`

Expected: still FAIL, now for the intended behavior assertion.

### Task 2: Separate background-loop shutdown from final card completion

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`

- [ ] **Step 1: Add a dedicated background-update cancellation source**

Keep status-pulse and external-history-backfill loop cancellation separate from the existing update-work cancellation.

- [ ] **Step 2: Stop and await background tasks before final finish**

Cancel the background-loop token, await both tasks, then perform the final `cardSession.FinishAsync(...)` write.

- [ ] **Step 3: Preserve existing final cleanup**

Keep `finally` cleanup safe and idempotent so shutdown still works on success, error, and cancellation paths.

- [ ] **Step 4: Run the targeted regression test**

Run: `dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "HandleIncomingMessageAsync_WhenBackgroundReplacementHandleUsesCanceledToken_SkipsNormalCompletion"`

Expected: PASS

### Task 3: Verify channel-side regressions

**Files:**
- Modify: `docs/agent-notes/2026-05-23.md`

- [ ] **Step 1: Update the daily note with the confirmed fix pattern**

Record that channel-side normal completion now waits for background update tasks to exit before final card completion.

- [ ] **Step 2: Run focused Feishu channel tests**

Run: `dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "FeishuChannelServiceTests"`

Expected: PASS
