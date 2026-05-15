# Streaming Reply Card Section Separation Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the in-flight streaming reply card visually separate the live reply content, thinking/status band, and bottom Superpowers workflow area with obvious red horizontal dividers, without changing behavior.

**Architecture:** Keep the change local to the two existing Razor surfaces that render streaming replies. Reuse existing Tailwind-style utility classes in markup instead of adding global CSS or changing component logic. Preserve all current event handlers, disabled-state rules, and prompt-routing behavior while wrapping the affected sections with stronger spacing and divider treatment.

**Tech Stack:** ASP.NET Core Blazor Razor components, Tailwind utility classes, .NET 10 build/test tooling

---

Spec reference: `docs/superpowers/specs/2026-05-05-streaming-reply-card-section-separation-design.md`

## File Map

- `WebCodeCli/Components/ChatMessageListPanel.razor`
  - Shared desktop streaming assistant card.
  - Needs section wrappers and red divider treatment inside the `IsLoading` branch only.
- `WebCodeCli/Pages/CodeAssistantMobile.razor`
  - Mobile streaming reply surface.
  - Needs matching separation treatment around the live reply block and the bottom Superpowers workflow block.
- No new dependencies.
- No new global stylesheet expected.
- No dedicated component-test harness currently exists in `tests/WebCodeCli.Tests` for these Razor surfaces, so verification stays at build/test/manual UI level.

## Chunk 1: Shared Desktop Streaming Card

### Task 1: Add explicit content and control sections to the shared loading card

**Files:**
- Modify: `WebCodeCli/Components/ChatMessageListPanel.razor`
- Test: manual verification in the running Web UI

- [ ] **Step 1: Isolate the current desktop streaming card body sections**

Read the `@if (IsLoading)` branch and identify the current order:

1. header row
2. live markdown block
3. Superpowers quick-action block
4. goal quick-action block

Confirm the change stays inside this branch only.

- [ ] **Step 2: Wrap the live reply content in a dedicated first section**

Adjust the markup so the current live markdown block sits inside its own neutral section container.

Target shape:

```razor
<div class="rounded-lg bg-white/70 px-3 py-3">
    <div class="text-xs sm:text-sm max-h-60 sm:max-h-96 overflow-y-auto overflow-x-hidden text-gray-700 leading-relaxed break-words">
        @RenderMarkdown(CurrentAssistantMessage)
    </div>
</div>
```

Keep the existing text sizing, overflow behavior, and markdown rendering.

- [ ] **Step 3: Add a distinct thinking/status divider band before workflow controls**

Insert a section break immediately after the live reply content using utility classes such as:

```razor
<div class="mt-3 border-t-2 border-red-300 pt-3">
    ...
</div>
```

The separator must be visually obvious. Do not attach any new behavior to it.

- [ ] **Step 4: Keep the existing Superpowers block but place it behind its own red divider**

Move the existing `ShouldShowStreamingSuperpowersQuickActions()` block under a second section boundary so it no longer reads as a continuation of the markdown body.

Target pattern:

```razor
<div class="mt-3 border-t-2 border-red-300 pt-3">
    <div class="rounded-lg border border-blue-100 bg-blue-50/60 p-3">
        ...
    </div>
</div>
```

Do not change button text, disabled states, placeholders, or callback wiring.

- [ ] **Step 5: Keep the goal quick-action block visually aligned with the new section treatment**

If `ShouldShowStreamingGoalQuickActions()` remains in the same card, apply the same section-separation approach so the green goal block also sits behind a clear divider instead of attaching directly to the previous section.

- [ ] **Step 6: Build the solution to confirm the shared component still compiles**

Run:

```powershell
dotnet build WebCodeCli.sln
```

Expected:

- build succeeds
- no Razor parse errors
- no component parameter or markup nesting errors

- [ ] **Step 7: Commit the desktop card slice**

```bash
git add WebCodeCli/Components/ChatMessageListPanel.razor
git commit -m "Separate streaming reply sections in the shared desktop card"
```

Use the repository Lore commit format when creating the real commit.

## Chunk 2: Mobile Streaming Reply Alignment

### Task 2: Mirror the separation pattern in the mobile streaming surface

**Files:**
- Modify: `WebCodeCli/Pages/CodeAssistantMobile.razor`
- Test: manual verification in the running mobile Web UI

- [ ] **Step 1: Isolate the current mobile streaming reply structure**

Read the `_isLoading` streaming branch and confirm the current structure:

1. reply bubble with live markdown or animated dots
2. optional view-details strip inside the bubble
3. separate bottom Superpowers workflow panel

Keep this order.

- [ ] **Step 2: Add a red-divider break between the reply bubble content and the workflow panel**

Update the wrapper spacing so the mobile workflow panel is visually separated from the live reply area by a clear red horizontal divider treatment.

Target pattern:

```razor
<div class="mt-3 w-full max-w-[85%] border-t-2 border-red-300 pt-3">
    <div class="rounded-2xl border border-blue-100 bg-blue-50/70 p-3">
        ...
    </div>
</div>
```

Keep the existing width constraints and button wrapping behavior.

- [ ] **Step 3: Preserve the inline thinking presentation inside the mobile reply bubble**

Do not move the existing animated dots or thinking text out of the bubble. This plan only strengthens the boundary between reply content and workflow controls.

- [ ] **Step 4: Build again after the mobile update**

Run:

```powershell
dotnet build WebCodeCli.sln
```

Expected:

- build succeeds again
- no malformed Razor markup

- [ ] **Step 5: Commit the mobile alignment slice**

```bash
git add WebCodeCli/Pages/CodeAssistantMobile.razor
git commit -m "Match mobile streaming reply section separators"
```

Use the repository Lore commit format when creating the real commit.

## Chunk 3: Verification and Cleanup

### Task 3: Verify behavior did not change

**Files:**
- Verify: `WebCodeCli/Components/ChatMessageListPanel.razor`
- Verify: `WebCodeCli/Pages/CodeAssistantMobile.razor`
- Verify: `WebCodeCli.sln`

- [ ] **Step 1: Run the fast build + test baseline**

Run:

```powershell
dotnet build WebCodeCli.sln
dotnet test tests/WebCodeCli.Tests/WebCodeCli.Tests.csproj --no-build
```

Expected:

- solution builds successfully
- existing test project still passes

Note:

- this does not provide visual coverage
- there is no existing dedicated component test harness for these streaming Razor surfaces

- [ ] **Step 2: Manually verify the desktop streaming card**

Manual checklist:

- start a streaming assistant reply in desktop Web
- confirm the markdown body remains readable
- confirm a visible red divider separates content from the next section
- confirm the Superpowers block no longer reads as part of the reply body
- confirm disabled buttons and input still look disabled while streaming

- [ ] **Step 3: Manually verify the mobile streaming card**

Manual checklist:

- open the mobile page
- start a streaming assistant reply
- confirm the reply bubble still renders correctly
- confirm the bottom workflow panel is separated by a visible red divider
- confirm buttons still wrap cleanly on narrow widths

- [ ] **Step 4: Check the final diff stays narrow**

Run:

```powershell
git diff -- WebCodeCli/Components/ChatMessageListPanel.razor WebCodeCli/Pages/CodeAssistantMobile.razor
```

Expected:

- diff is limited to markup/class changes
- no event-handler or prompt-construction logic changed

- [ ] **Step 5: Commit the verified implementation**

```bash
git add WebCodeCli/Components/ChatMessageListPanel.razor WebCodeCli/Pages/CodeAssistantMobile.razor
git commit -m "Clarify streaming reply sections with divider styling"
```

Use the repository Lore commit format when creating the real commit.
