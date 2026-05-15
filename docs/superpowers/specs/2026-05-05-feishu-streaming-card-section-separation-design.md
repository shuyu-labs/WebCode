# Feishu Streaming Card Section Separation Design

Date: 2026-05-05
Status: approved in discussion, pending implementation planning

## Goal

Make the Feishu streaming reply card clearly separate its thinking-level area, reply-content area, and bottom Superpowers workflow area.

The change must:

- target the Feishu CardKit streaming card, not the Web Razor UI
- preserve existing Feishu actions, prompt submission, and card update behavior
- create obvious section boundaries even if CardKit does not support custom `hr` colors
- simulate the requested red divider effect using card content structure instead of Web CSS

## Scope

In scope:

- Feishu streaming card element construction
- Feishu streaming card refresh-card construction
- tests covering Feishu card JSON structure

Out of scope:

- Web desktop and mobile cards
- Feishu session launch logic
- Superpowers prompt/action semantics
- provider/model/reasoning switch behavior

## Problem

The current Feishu streaming card only inserts default `hr` modules between status, top chips, markdown content, and bottom actions.

That means:

- the thinking-level controls do not read as a separate section strongly enough
- the reply body and bottom workflow area still feel visually adjacent
- the requested red divider effect cannot appear because the current card JSON never emits any explicit red visual element

## Design

Use structural section markers instead of CSS dividers.

Each visible major section gets a dedicated marker row rendered as card content:

- `🟥🟥🟥 思考等级`
- `🟥🟥🟥 回复内容`
- `🟥🟥🟥 Superpowers 工作流`

These markers act as the red visual divider simulation. They are stable across Feishu rendering because they rely on plain card content, not unsupported CSS classes.

## Architecture

Create one shared streaming-card element builder used by:

- `FeishuCardKitClient` create/update rendering path
- `FeishuCardActionService` refresh-card rendering path

The shared builder should:

- render status area first
- render the thinking-level section marker only when top chip groups exist
- render the reply-content section marker before the markdown body when chrome exists
- render the workflow section marker only when bottom prompt or bottom actions exist

## Verification

Verify by tests that:

- section markers are present in the generated Feishu card JSON
- top chip rows remain before reply content
- reply content remains before bottom prompt/actions
- prompt and action payloads remain unchanged
