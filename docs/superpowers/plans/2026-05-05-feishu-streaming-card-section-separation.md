# Feishu Streaming Card Section Separation Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Feishu streaming reply card clearly separate thinking-level controls, reply content, and bottom Superpowers workflow controls by inserting explicit red visual section markers into the card JSON.

**Architecture:** Reuse one shared streaming-card element builder for both the CardKit create/update path and the refresh-card path. Preserve all existing action payloads and bottom prompt behavior while replacing weak default section boundaries with explicit marker rows.

**Tech Stack:** .NET 10, Feishu CardKit JSON card rendering, xUnit

---

Spec reference: `docs/superpowers/specs/2026-05-05-feishu-streaming-card-section-separation-design.md`

## File Map

- `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs`
  - Source of streaming-card element construction today.
- `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
  - Contains refresh-card rendering path that must stay aligned.
- `WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs`
  - Best place to lock the generated card JSON shape.

## Task 1: Share the Feishu streaming card section builder

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`

- [ ] Extract or expose one shared streaming-card element builder.
- [ ] Add explicit section marker modules for thinking-level, reply-content, and workflow areas.
- [ ] Ensure the workflow marker only appears when bottom prompt or bottom actions exist.
- [ ] Route the refresh-card path through the same section builder or identical helper.

## Task 2: Lock the generated JSON with tests

**Files:**
- Modify: `WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs`

- [ ] Update index-sensitive tests that currently assume raw `hr` placement.
- [ ] Add a test that asserts the section-marker content appears in the card JSON.
- [ ] Keep prompt and action assertions unchanged so behavior remains covered.

## Task 3: Verify

**Files:**
- Verify: `WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs`
- Verify: `WebCodeCli.sln`

- [ ] Run `dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter FeishuCardKitClientTests`.
- [ ] Run `dotnet build WebCodeCli.sln`.
- [ ] Confirm no action payload logic changed, only card structure.
