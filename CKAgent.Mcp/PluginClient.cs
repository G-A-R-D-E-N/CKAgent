using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace Garden.CkAgent;

/// Only the plugin reaches editor internals, so failures must return a code, not an exception.
public sealed class PluginClient
{
    private const string PipeName = "ckagent";
    private const string HeloPrefix = "HELO ";

    private readonly object _gate = new();
    private int _sequence;

    public bool TryCall(string command, out string payload, out string code, out string message)
    {
        payload = "";
        code = "ok";
        message = "";

        lock (_gate)
        {
            try
            {
                using NamedPipeClientStream pipe = ConnectWithRetry();

                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n",
                };

                string? helo = reader.ReadLine();
                if (helo == null || !helo.StartsWith(HeloPrefix, StringComparison.Ordinal))
                {
                    code = "plugin_not_ready";
                    message = "the CKAgent plugin did not announce itself on the pipe";
                    return false;
                }

                int sequence = ++_sequence;
                writer.WriteLine($"CALL {sequence} {command}");

                string? reply = reader.ReadLine();
                if (reply == null)
                {
                    code = "plugin_closed";
                    message = "the plugin closed the pipe before answering";
                    return false;
                }

                // Constraint: a payload is one JSON value that may contain spaces, so only the prefix may be split.
                string[] parts = reply.Split(' ', 4, StringSplitOptions.None);
                if (parts.Length < 4 || parts[0] != "RPLY")
                {
                    code = "malformed_reply";
                    message = $"unexpected reply: {Truncate(reply, 200)}";
                    return false;
                }

                if (parts[2] == "ERR")
                {
                    string[] error = parts[3].Split(' ', 2, StringSplitOptions.None);
                    code = error[0];
                    message = error.Length > 1 ? error[1] : code;
                    return false;
                }

                payload = parts[3];
                return true;
            }
            catch (TimeoutException)
            {
                code = "CKPEUnavailable";
                message = "no CKAgent plugin is listening on \\\\.\\pipe\\ckagent; install and enable CKPE with " +
                          "CKAgent.dll in <game>\\CKPEPlugins\\";
                return false;
            }
            catch (IOException exception)
            {
                code = "plugin_io";
                message = exception.Message;
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                code = "pipe_denied";
                message = DeniedMessage(exception, WindowsIntegrity.LevelName());
                return false;
            }
            catch (Exception exception)
            {
                code = "pipe_error";
                message = $"{exception.GetType().Name}: {exception.Message}";
                return false;
            }
        }
    }

    // Constraint: the refused message must name the integrity level, because that decides which side to fix.
    internal static string DeniedMessage(Exception exception, string? level)
    {
        string text = $"the pipe refused the connection: {exception.Message} (0x{exception.HResult:X8})";

        if (level == null)
            return text + "; compare the account, the session and the integrity level of the editor process";

        // Constraint: a low client only reaches a pipe the plugin labelled low, so name the plugin as the cause.
        if (level == "low")
            return text + "; this client runs at low integrity, and a low-integrity process cannot attach to a " +
                "pipe opened by a higher one unless that pipe is labelled low - so the loaded CKAgent plugin " +
                "predates the low-labelled pipe, or the pipe belongs to another user; a medium-integrity host " +
                "such as 'dotnet ckagent.dll' connects either way";

        return text + $"; this client runs at {level} integrity, so the pipe belongs to another user or to a " +
            "higher-integrity process - compare the account and session running the editor";
    }

    // Constraint: the plugin rebuilds its pipe after every client, so a connect must retry before it fails.
    private static NamedPipeClientStream ConnectWithRetry()
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var candidate = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
            try
            {
                candidate.Connect(3000);
                return candidate;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                last = exception;
                candidate.Dispose();
                Thread.Sleep(200);
            }
        }

        throw last!;
    }

    /// <summary>Convenience for the camera tool; formats with invariant culture so decimals are configurable-friendly.</summary>
    public static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";
}
