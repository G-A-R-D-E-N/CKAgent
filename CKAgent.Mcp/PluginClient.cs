using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace Garden.CkAgent;

/// Only the plugin reaches editor internals, so failures must return a code, not an exception.
public sealed class PluginClient : IPluginCaller
{
    private const string PipeName = "ckagent";
    private const string HeloPrefix = "HELO ";
    // Constraint: a blocked read must not outlive the caller's patience, so every call has a deadline.
    private const int CallDeadlineMs = 15000;

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

                int sequence = ++_sequence;

                // Constraint: the handshake is answered from the editor thread too, so it shares the same deadline.
                string? helo;
                string? reply;
                try
                {
                    var exchange = System.Threading.Tasks.Task.Run(() =>
                    {
                        string? greeting = reader.ReadLine();
                        if (greeting == null || !greeting.StartsWith(HeloPrefix, StringComparison.Ordinal))
                            return (Greeting: (string?)null, Reply: (string?)null);

                        writer.WriteLine($"CALL {sequence} {command}");
                        return (Greeting: (string?)greeting, Reply: reader.ReadLine());
                    });

                    if (!exchange.Wait(CallDeadlineMs))
                    {
                        code = "plugin_busy";
                        message = $"the editor did not answer '{command}' within {CallDeadlineMs / 1000}s; it is " +
                                  "still working, so retry when it is idle";
                        return false;
                    }

                    helo = exchange.Result.Greeting;
                    reply = exchange.Result.Reply;
                }
                catch (AggregateException aggregate) when (aggregate.InnerException is IOException)
                {
                    code = "plugin_io";
                    message = aggregate.InnerException.Message;
                    return false;
                }

                if (helo == null)
                {
                    code = "plugin_not_ready";
                    message = "the CKAgent plugin did not announce itself on the pipe";
                    return false;
                }

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
                message = $"the pipe refused the connection: {exception.Message} (0x{exception.HResult:X8})";
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
