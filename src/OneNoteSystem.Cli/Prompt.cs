using System.Text;

namespace OneNoteSystem.Cli;

/// <summary>Interactive input for <c>configure</c>.</summary>
public interface IPrompt
{
    string Ask(string question);

    /// <summary>Reads a secret without echoing it, so an API key never appears on screen.</summary>
    string AskSecret(string question);
}

/// <summary>
/// Reads keystrokes from the console directly. <see cref="Console.ReadLine"/> cannot hide
/// input, and a redirected stdin has nobody to answer -- say so instead of blocking forever,
/// and name the flags that avoid prompting.
/// </summary>
public sealed class ConsolePrompt : IPrompt
{
    public ConsolePrompt()
    {
        if (Console.IsInputRedirected)
        {
            throw new CliException(
                "cannot ask for input because stdin is not a terminal. Pass --url and --api-key instead.",
                ExitCode.Usage);
        }
    }

    public string Ask(string question) => Read(question, echo: true);

    public string AskSecret(string question) => Read(question, echo: false);

    private static string Read(string question, bool echo)
    {
        Console.Error.Write(question);
        var value = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.Error.WriteLine();
                    return value.ToString().Trim();

                case ConsoleKey.Backspace:
                    if (value.Length > 0)
                    {
                        value.Length--;
                        if (echo) Console.Error.Write("\b \b");
                    }
                    continue;

                case ConsoleKey.Escape:
                    Clear();
                    continue;
            }

            // Ctrl-C and Ctrl-D end the answer rather than leaving a half-typed key behind.
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.C or ConsoleKey.D)
            {
                Console.Error.WriteLine();
                throw new CliException("input was cancelled", ExitCode.Usage);
            }

            // Ctrl-U clears the line, as it does in a shell.
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.U)
            {
                Clear();
                continue;
            }

            // Ignore the remaining control characters; keep everything printable,
            // which includes non-ASCII text.
            if (key.KeyChar == '\0' || char.IsControl(key.KeyChar)) continue;

            value.Append(key.KeyChar);
            if (echo) Console.Error.Write(key.KeyChar);
        }

        void Clear()
        {
            if (echo) for (var i = 0; i < value.Length; i++) Console.Error.Write("\b \b");
            value.Clear();
        }
    }
}
