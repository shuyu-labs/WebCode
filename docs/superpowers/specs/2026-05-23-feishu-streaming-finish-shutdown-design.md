# Feishu Streaming Finish Shutdown Design

## Goal

Reduce Feishu streaming-card tail-race cancellations during normal completion by stopping background update loops first, waiting for them to exit, and only then performing the final card completion write.

## Scope

This change is limited to the normal channel submission path in [FeishuChannelService.cs](/D:/VSWorkshop/WebCode/WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs:1). It does not change `FeishuCardActionService` behavior in this pass.

## Design

The current channel flow cancels update work before the final `FinishAsync(...)` write. Background status-pulse and external-history-backfill tasks use the same cancellation source for loop control and replacement-handle creation, so a replacement handle created by a background task can inherit a token that is canceled immediately before final completion.

The fix is:

1. Introduce a dedicated background-update cancellation source for the channel submission path.
2. Run status-pulse and external-history-backfill loops off that background token instead of the execution-wide update token.
3. On normal completion, cancel and await those background tasks first.
4. After they exit, perform the final completion write.
5. Keep the existing final cleanup path to cancel remaining update work and dispose execution state.

## Testing

Add a regression in `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs` that:

- lets the main stream produce one successful update
- forces a later background status-pulse update to stop the original card and create a replacement handle
- makes the replacement handle fail its finish if the creation token has already been canceled
- verifies the channel path still completes the replacement card and sends the normal completion notification
