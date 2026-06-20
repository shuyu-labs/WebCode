# Feishu Reply Document Folder Design

## Context

Feishu completed-reply delivery already creates cloud documents and sends plain-text links back into chat. Today the pipeline controls document titles, but not the folder where those documents are stored. Users want reply documents grouped under a folder derived from the current session title so a session's documents stay organized in Feishu Drive.

The requested naming rule is:

- Prefer the current session title as the folder name.
- If the session title is blank or effectively unnamed, fall back to the CLI thread id.
- If the CLI thread id is also missing, fall back to the session id.

## Goals

- Place each generated Feishu reply document into a deterministic folder instead of leaving it in the default location.
- Reuse the same folder for later documents from the same session naming source.
- Use the session title as the first-choice folder name.
- Treat `未命名` as unnamed and fall back to thread id.
- Keep existing reply-document titles unchanged.

## Non-Goals

- No change to the visible plain-text link message format.
- No change to the full/final/audio document title suffix rules.
- No attempt to mirror local workspace directory structure into Feishu Drive.
- No user-configurable custom folder root in this change.

## Constraints

- The current `CreateCloudDocumentAsync(...)` flow only creates a document by title; folder placement is not modeled in the existing client abstraction.
- Folder lookup and creation must be safe for repeated runs. The pipeline should not create duplicate folders every time if an appropriate folder already exists.
- The reply-document pipeline already has access to `SessionId`, optional `CliThreadId`, and can resolve `ChatSessionEntity` through the repository.

## Recommended Approach

Use a two-phase document placement flow:

1. Resolve the target folder name from session metadata.
2. Ensure a matching Feishu folder exists.
3. Create the cloud document normally.
4. Move the created document into the ensured folder.

This is preferred over forcing folder data into the existing create-document call because the current code is already structured around a plain document-create operation and a separate follow-up write/permission pipeline.

## Folder Naming Rules

Given one completed reply request:

1. Load the chat session when `SessionId` is present.
2. Inspect `ChatSessionEntity.Title`.
3. Normalize the title by trimming whitespace and collapsing obvious empty values.
4. Treat the following as unnamed:
   - null
   - empty or whitespace-only
   - `未命名`
5. If the normalized title is named, use it as the folder name.
6. Otherwise, use `CliThreadId` when present.
7. Otherwise, use `SessionId`.
8. If none of the above exist, skip folder placement rather than throwing.

## Folder Name Sanitization

Before sending the folder name to Feishu:

- Trim leading and trailing whitespace.
- Replace filesystem-style reserved separators and unsafe punctuation such as `/`, `\`, `:`, `*`, `?`, `"`, `<`, `>`, `|` with spaces or a safe separator.
- Collapse repeated internal whitespace.
- Truncate to a reasonable maximum length to avoid downstream API issues.

This sanitization is only for the folder name. Document titles remain on the existing title path.

## Service Boundaries

### ReplyDocumentOrchestrator

Add orchestration logic that:

- resolves the effective folder name from the current session/request,
- asks the Feishu client to ensure the folder exists,
- asks the Feishu client to move the newly created document into that folder.

The orchestrator remains the place where session-aware naming decisions live.

### IFeishuCardKitClient / FeishuCardKitClient

Extend the Feishu client abstraction with the minimal operations needed by the orchestrator:

- ensure a cloud folder exists by name,
- move a document into a folder.

The concrete HTTP details stay inside the Feishu client layer rather than leaking into the orchestrator.

## Error Handling

- If folder-name resolution fails because the session cannot be loaded, continue with the fallback chain instead of failing document generation.
- If folder creation or folder move fails, treat it as a document-generation failure for that document and surface the same failure-notification path already used by reply-document creation failures.
- If folder placement is skipped because no usable title/thread/session id exists, continue creating the document in the default location.

## Testing Strategy

Add focused coverage for:

- session title folder name wins over thread id,
- `未命名` title falls back to thread id,
- missing thread id falls back to session id,
- orchestrator asks the Feishu client to ensure a folder and then move the created document,
- blank naming inputs skip folder placement without blocking document creation.

## Design Summary

The change should keep the existing reply-document pipeline shape intact while adding a deterministic folder-placement step between document creation and link delivery. Naming policy belongs in `ReplyDocumentOrchestrator`, Feishu API details belong in `IFeishuCardKitClient`/`FeishuCardKitClient`, and existing document title behavior should remain untouched.
