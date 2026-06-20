# Feishu Markdown Reply Rendering and Markdown Import Design

Date: 2026-06-09

## Goal

Extend the existing Feishu reply-document system in two coordinated ways:

1. Upgrade reply-document body generation from plain text append behavior to Feishu-native Markdown rendering through Feishu's official document-convert capability.
2. Add a new help-card toggle that automatically imports referenced local Markdown files as Feishu online documents after a streaming reply completes.

The system must continue to send document links back into the same Feishu chat and must preserve the existing session-scoped shared-folder workflow where possible.

## Product Definition

### Existing document outputs remain

The current four reply-document outputs remain valid and independent:

- `完整回复文档`
- `结论回复文档`
- `听完整文档`
- `听结论文档`

All four toggles may still be enabled together. Each enabled output produces its own Feishu document and its own link message in chat.

### New help-card toggle

Add a fifth independent toggle:

- `MD转在线文档`

When enabled, the system inspects the completed reply content after a streaming reply finishes. If the reply references one or more local `.md` files, the system imports those files into Feishu online documents and sends the online-document links back into the current chat.

This toggle is independent from the four reply-document toggles. Any combination of the five toggles is allowed.

### Completion boundary

All outputs remain scoped to one completed assistant turn:

- one normal streaming completion
- one card-action completion when that path already produces completed-reply side effects
- one goal-runtime app-server turn boundary when that path already produces completed-reply side effects

No output aggregates across multiple turns.

### Reply-document body behavior

The visible product behavior for the four reply-document variants stays the same at the content-source level:

- `完整回复文档` uses the current turn full completed assistant reply
- `结论回复文档` uses the current turn final-only assistant reply
- `听完整文档` starts from the full completed assistant reply
- `听结论文档` starts from the final-only assistant reply

The change is only in how the document body is written:

- prefer Feishu official Markdown-to-doc conversion instead of plain text append
- keep a safe fallback to plain text append when Feishu conversion fails

### Listening-document formatting rule remains

`听完整文档` and `听结论文档` must continue to run the existing listening formatter before document generation.

That formatter remains responsible for:

- replacing file-like references with `文件内容N`
- replacing command-like content with `命令内容N`
- appending the mapping appendix at the end

After formatting completes, the resulting Markdown text is passed into the reply-document rendering pipeline.

### Markdown file import behavior

When `MD转在线文档` is enabled:

1. inspect the completed reply text
2. detect referenced local `.md` files
3. normalize and deduplicate the references
4. check whether the session shared folder already contains an online document with the normalized relative path as its title
5. if it exists, reuse it and send the existing link
6. if it does not exist, upload and import the Markdown file into a Feishu online document
7. send the resulting online-document link back into the same chat

This feature must not modify the main reply body or the streaming card body.

## Non-Goals

- build or maintain a custom Markdown AST renderer for Feishu documents
- replace the current reply-document toggle family with a single combined mode
- import remote `http://` or `https://` Markdown links
- import non-Markdown local files through the new toggle
- change the current reply-document title scheme outside the new Markdown-import document titles
- block main assistant completion when document generation or Markdown import fails
- upload and render native images in the first version of the new rendering path

## Existing Constraints

### Existing completion pipeline should be reused

The current system already has a completed-reply pipeline with:

- current turn full output
- current turn final-only output
- per-chat serialization for reply-document side effects
- chat-link delivery after document creation

The new design should attach to that pipeline rather than create a separate completion subsystem.

### Existing shared-folder workflow should be reused

The current reply-document flow already resolves a session-scoped shared folder and prefers direct in-folder document creation when possible.

The new design should reuse the same shared-folder resolution policy for:

- reply documents
- imported Markdown online documents

### Existing failure behavior should be preserved

Reply-document generation currently avoids breaking the main reply when Feishu document operations fail. Folder-placement failures can already degrade into:

- document still created
- link still sent
- warning sent about placement failure

The new design should preserve this non-fatal behavior.

## Recommended Architecture

Use one unified completed-reply document pipeline with two Feishu-native writing strategies.

### Strategy A: reply documents

For the four reply-document variants:

1. select the variant content
2. apply optional listening formatting
3. create a Feishu `docx` document
4. try to convert the Markdown body through Feishu's official document-convert capability
5. if conversion succeeds, insert the returned blocks into the document
6. if conversion fails, fall back to the current plain-text append path
7. set access and send the link back to chat

### Strategy B: Markdown file import

For the new `MD转在线文档` toggle:

1. inspect the completed reply text for referenced local `.md` files
2. resolve them against the current session workspace
3. normalize to relative paths
4. deduplicate by normalized relative path
5. reuse an existing shared-folder online document with the same title when available
6. otherwise upload the source `.md` file and create a Feishu import task that turns it into an online document
7. send the resulting link back to chat

This architecture is preferred because it delegates Markdown interpretation to Feishu's supported platform capabilities instead of duplicating Markdown parsing rules in application code.

## Alternatives Considered

### Alternative 1: custom Markdown parser and Feishu block renderer

Rejected.

This would create a large, fragile maintenance surface for headings, lists, task lists, code blocks, links, tables, and future Markdown edge cases. It is unnecessary because Feishu already provides official conversion capabilities for reply-document text and official import capabilities for source `.md` files.

### Alternative 2: reuse plain text for reply documents and only add Markdown import

Rejected.

This would leave reply-document quality behind the new imported-document quality and would preserve the current low-fidelity plain-text rendering even when the reply already contains well-structured Markdown.

### Alternative 3: treat reply content as pseudo-files and route everything through Markdown file import

Rejected.

Reply text is not naturally persisted as a local `.md` file at the right abstraction boundary, and forcing that shape would complicate title handling, fallback behavior, and folder placement for no product gain.

## Data Model Changes

### User Feishu bot config

Add one new independent boolean field:

- `ReferencedMarkdownDocImportEnabled`

This field belongs next to the existing reply-document toggles in the same user config model, DTOs, admin endpoints, and help-card rendering paths.

### Help-card actions

Add one new help-card action constant for toggling Markdown import mode.

The toggle behavior mirrors the existing document toggles:

- load current config
- invert the target field
- persist the config
- rebuild the card
- return success feedback

## Reply-Document Rendering Design

### Content selection

Continue using the existing sources:

- full reply document uses the completed turn full output
- final reply document uses the completed turn final-only output
- listening full reply document uses listening-formatted full output
- listening final reply document uses listening-formatted final-only output

Existing Codex-specific final-answer fallback rules remain unchanged for final-only content resolution.

### Rendering path

The reply-document writer should support this sequence:

1. create the target Feishu document
2. transform the chosen content into a Markdown string
3. call Feishu's official document-convert capability with that Markdown string
4. if conversion returns renderable blocks, append those blocks into the document
5. if conversion fails or returns an unusable payload, fall back to plain text append

### First-version rendering scope

The first version should target good fidelity for common reply content:

- headings
- paragraphs
- bold and italic
- inline code
- blockquotes
- ordered and unordered lists
- nested lists
- task lists
- fenced code blocks
- divider
- links
- tables when Feishu conversion accepts them

Images do not need first-version native rendering support. When images appear in the Markdown body, the system may degrade them into plain text, link text, or the plain-text append fallback.

### Fallback rule

If official Markdown conversion fails for a reply document:

- do not fail the whole completed-reply pipeline
- write the same body through the current plain-text append path
- still send the document link
- log the Markdown-rendering downgrade for diagnostics, but do not send an extra chat warning for this fallback alone

The fallback must be per-document, not global. One failed variant must not block the remaining enabled variants.

## Markdown Reference Detection Design

### Detection sources

The Markdown import feature inspects reply text in this order:

1. completed full reply content
2. if full reply content is empty, final-only reply content

It does not inspect user prompts, rollout files directly, or unrelated session history.

### Detection rule

Use the approved broad detection rule:

- detect bare local `.md` paths in prose
- detect Markdown links whose target path ends in `.md`

Examples that should be detected:

- `docs/agent-notes/2026-06-09.md`
- `MMIS-Server/docs/plan.md`
- `[设计文档](docs/superpowers/specs/2026-06-09-example.md)`
- `D:\Work\Repo\docs\spec.md`

Examples that should not be imported:

- remote `https://.../readme.md`
- malformed paths that do not resolve to a local file
- non-Markdown extensions

### Path normalization

For each detected candidate:

1. trim surrounding punctuation and wrapper syntax
2. normalize separators to `/`
3. remove leading `./` segments
4. resolve against the session workspace root when the path is relative
5. keep absolute local paths only if they are under the current workspace root
6. reject paths that escape outside the workspace root
7. convert accepted paths into normalized relative paths from the workspace root

The normalized relative path becomes both:

- the uniqueness key
- the preferred Feishu online-document title

Example:

- local path `D:\VSWorkshop\WebCode\docs\agent-notes\2026-06-09.md`
- normalized relative title `docs/agent-notes/2026-06-09.md`

### Deduplication

If the same normalized relative path appears multiple times in one reply:

- import it only once
- send only one link for that document in that completed-reply cycle

## Shared-Folder Reuse and Import Design

### Folder target

Markdown-imported online documents should target the same session-scoped shared folder used by reply documents.

The folder naming and resolution rules stay owned by the existing reply-document folder policy.

### Existing-document reuse

Before importing a detected Markdown file:

1. list the current shared-folder document entries that are relevant for online documents
2. look for an existing entry whose title exactly matches the normalized relative path
3. if found, reuse that document instead of creating a duplicate

When an existing document is reused, the system should still send the link back into chat so the user can open it immediately.

### Import path

When no matching shared-folder online document exists:

1. upload the source `.md` file to Feishu Drive
2. create a Feishu import task that converts the uploaded Markdown file into a Feishu online document
3. place the result into the target shared folder directly when supported
4. if direct placement is not supported or fails after creation, preserve the existing fallback pattern:
   - keep the created resource
   - send the document link
   - send a placement warning when needed

## Title and Link Message Rules

### Existing reply-document titles remain unchanged

This design does not change the title-prefix logic or the suffix rules for:

- `完整回复文档`
- `结论回复文档`
- `听完整文档`
- `听结论文档`

### Imported Markdown document titles

For Markdown imports, use the normalized relative path as the Feishu online-document title.

Examples:

- `docs/agent-notes/2026-06-09.md`
- `docs/superpowers/specs/2026-06-09-feishu-markdown-doc-import-and-rendering-design.md`

### Link messages

Each produced or reused Markdown online document sends a separate plain-text link message into the same chat.

Recommended message shape:

- `已生成Markdown在线文档：[title](url)`
- `已复用Markdown在线文档：[title](url)`

Reply-document link messages keep their existing message family and do not merge with Markdown-import link messages.

## Error Handling

### General rule

No reply-document rendering failure or Markdown import failure may break the main assistant reply completion flow.

### Reply-document rendering failures

If Markdown conversion fails:

- log the failure with enough context to distinguish convert failure from append or permission failure
- fall back to plain-text append for that same document
- continue processing the remaining enabled outputs

If later Feishu document operations fail after document creation:

- preserve the existing stage-aware warning behavior
- still send the created link whenever the document resource exists

### Markdown import failures

If Markdown detection finds a candidate that cannot be resolved safely:

- skip that candidate silently

If upload or import fails for a valid candidate:

- send a Chinese warning message to chat for that candidate
- continue processing the remaining Markdown candidates
- continue processing the four normal reply-document outputs

If shared-folder lookup fails after an online document already exists:

- preserve the created or existing document when possible
- send the link when available
- send a folder-placement warning consistent with the current reply-document warning style

## Permission Model

### Reply-document rendering permissions

Reply documents continue to require the existing Feishu document capabilities already used for:

- document creation
- document writing
- permission updates

### Markdown import permissions

Markdown import additionally depends on Feishu Drive capabilities for:

- file upload
- import task creation
- shared-folder listing or lookup

The application should not hardcode operator-facing scope lists in design logic. Instead it should preserve and extend the existing friendly error summarization path so Feishu's returned missing-scope information is surfaced back to chat in Chinese when permission errors occur.

## Service Boundaries

### ReplyDocumentOrchestrator

The orchestrator remains the completed-reply coordination entry point.

Its responsibilities expand to:

- resolve enabled reply-document outputs
- render reply documents through the new Markdown-convert-first path
- trigger Markdown reference scanning when the new toggle is enabled
- coordinate existing-document reuse or import-task creation for Markdown files
- continue sending link messages and placement warnings

### FeishuCardKitClient

The Feishu client abstraction should own the HTTP details for:

- document creation
- Markdown-to-document conversion calls
- block append operations
- file upload
- import-task creation and polling
- shared-folder listing or lookup
- permission updates
- move or placement fallback operations

Business rules such as workspace path normalization, deduplication, variant selection, and title policy remain outside the raw client layer.

## Testing Strategy

Add focused automated coverage for the following cases:

- help-card rendering shows the new `MD转在线文档` toggle state
- card action handling toggles `ReferencedMarkdownDocImportEnabled` and persists it
- reply-document rendering prefers official Markdown conversion when it succeeds
- reply-document rendering falls back to plain text append when conversion fails
- listening document variants still run the existing formatter before rendering
- bare local `.md` paths are detected correctly
- Markdown links that target `.md` files are detected correctly
- duplicate references to the same normalized relative path are imported only once
- paths outside the workspace root are rejected
- an existing shared-folder online document with the same normalized relative path title is reused instead of re-imported
- a missing shared-folder document triggers upload and import
- one Markdown import failure does not block other Markdown imports
- Markdown import failures do not block the four normal reply-document outputs
- stage-aware folder-placement warnings still work for created resources

## Design Summary

The system should evolve from plain-text-only reply documents into a Feishu-native Markdown document flow while adding a separate Markdown-file import capability for referenced local `.md` files. The design deliberately avoids a custom Markdown renderer and instead relies on Feishu's official document-convert and import-task capabilities. Existing reply-document toggles, listening formatting, shared-folder placement, and non-fatal failure behavior remain in place, while one new independent `MD转在线文档` toggle extends the completed-reply pipeline with reusable imported online documents.
