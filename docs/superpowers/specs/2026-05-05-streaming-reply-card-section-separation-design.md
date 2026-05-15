# Streaming Reply Card Section Separation Design

Date: 2026-05-05
Status: approved in discussion, pending implementation planning

## Goal

Make the Web streaming assistant reply card visually separate its main content area, thinking/status area, and bottom Superpowers workflow area so users can distinguish reading content from operating controls at a glance.

The change must:

- affect only the in-flight streaming reply card
- keep all existing behaviors, events, and prompt routing unchanged
- add clear visual separation between content and control areas
- use a noticeable red horizontal divider between sections
- keep desktop Web and mobile Web visually aligned

This is a presentation refinement. It must not introduce new workflow logic, new toggles, or new state transitions.

## Scope

In scope:

- streaming assistant reply card in the shared desktop message-list component
- streaming assistant reply block in the mobile page implementation
- spacing, borders, section wrappers, and light background treatment
- red divider styling between sections

Out of scope:

- completed assistant message cards
- Feishu streaming cards
- session launch override dialog behavior
- model or reasoning switch logic
- Superpowers quick-action behavior, text, or eligibility rules

## Problem

The current streaming reply card visually reads as one continuous block:

- the live reply content
- the thinking/status area
- the bottom Superpowers workflow controls

Because these areas sit close together and share similar card treatment, the card does not provide a strong visual boundary between "read the reply" and "operate on the reply."

The result is avoidable ambiguity:

- users can read the workflow block as part of the reply body
- the thinking/status area does not feel like a distinct band
- the workflow controls appear attached to the text rather than clearly separated from it

The requested change is specifically to make these areas obviously distinct without redesigning the interaction model.

## User Experience

### Target Sections

The streaming reply card should be perceived as three stacked sections:

1. live reply content
2. thinking/status band
3. Superpowers workflow block

The user should be able to scan the card and immediately tell which area is content and which area is operational UI.

### Divider Treatment

Between adjacent sections, render a visible red divider using a top border treatment such as:

- `border-t`
- `border-red-300` or `border-red-400`
- matching top padding and top margin to create a real break rather than a hairline only

The divider should be obvious but not alarm-like. The intent is structural separation, not error signaling.

### Content Area

The main streaming markdown output remains the primary reading surface.

It should:

- stay visually neutral
- preserve current typography and overflow behavior
- remain the first section in the card

### Thinking/Status Area

The thinking/status area remains in the same card and keeps its current semantic role.

It should:

- stay between content and workflow controls
- receive distinct spacing from both neighboring sections
- retain a light neutral or light blue presentation

This area is not being redesigned into a new control surface. The goal is only to make it feel separate from the reply body and from the workflow controls below it.

### Superpowers Workflow Area

The Superpowers block remains the bottom operational area.

It should:

- keep its existing blue semantic styling
- remain functionally identical
- feel clearly downstream of the content and status areas

## Functional Requirements

### Behavior Preservation

The implementation must not change:

- streaming text updates
- loading-state behavior
- button enabled and disabled rules
- input enabled and disabled rules
- existing quick-action prompts
- existing event handlers or callback wiring

### Placement Preservation

The implementation must preserve the current relative order:

1. live reply content
2. thinking/status band
3. Superpowers block

### Responsive Consistency

Desktop and mobile should follow the same section-separation idea:

- same three conceptual sections
- same red divider treatment
- same no-behavior-change rule

Layout can still adapt to viewport size, but the separation pattern should remain consistent.

## Design Principles

1. Prefer visual structure over new interaction.
2. Keep the change local to the streaming reply surface.
3. Preserve current semantics and workflow behavior.
4. Make separation obvious enough to be noticed immediately.
5. Avoid expanding scope into completed-message redesign.

## Architecture

### 1. Shared Desktop Streaming Card

Update the loading-state assistant card in:

- `WebCodeCli/Components/ChatMessageListPanel.razor`

Recommended structural treatment:

- wrap the live markdown area in its own section container
- wrap the thinking/status strip in its own section container
- keep the existing Superpowers block in its own bottom section
- insert red divider boundaries between section wrappers

The existing card container remains the same overall component boundary.

### 2. Mobile Streaming Reply Block

Update the streaming reply block in:

- `WebCodeCli/Pages/CodeAssistantMobile.razor`

The mobile view already splits some blocks spatially, but it should still use the same clear divider logic so the relationship between content, status, and workflow remains consistent with desktop.

### 3. Styling Strategy

Preferred implementation strategy:

- use existing utility classes in Razor markup
- avoid adding new dependencies
- avoid introducing broad global CSS when local utility-class changes are sufficient

Recommended styling ingredients:

- `mt-*`
- `pt-*`
- `border-t`
- `border-red-300` or `border-red-400`
- subtle section background contrast where useful

## Implementation Notes

### Desktop

For the shared streaming card:

- keep the header row unchanged
- make the live reply content a dedicated first body section
- add a red divider before the thinking/status section
- add another red divider before the Superpowers block

### Mobile

For the mobile streaming reply:

- keep the streaming message bubble behavior unchanged
- add matching red-divider separation before the bottom workflow block
- if the thinking/status affordance is represented inline, separate it with spacing and section treatment rather than moving its behavior

## Verification

Verify the following after implementation:

- desktop streaming reply content and Superpowers block no longer read as a single visual area
- desktop card shows obvious red section dividers
- mobile streaming reply follows the same separation pattern
- action buttons still render correctly on narrow widths
- disabled states remain unchanged during streaming
- live markdown still updates without layout breakage

## Risks

- if the red divider is too saturated, it may read like an error state instead of a structural divider
- if spacing increases too much, the streaming card may become overly tall on mobile
- if scope expands into completed-message cards, the diff becomes larger than intended

## Recommendation

Implement the smallest possible markup and utility-class changes that create unmistakable section boundaries in the streaming reply card, and stop there. This should remain a focused visual refinement rather than a broader card redesign.
