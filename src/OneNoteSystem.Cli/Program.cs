using OneNoteSystem.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await CliRunner.RunAsync(args, cancellation.Token).ConfigureAwait(false);
