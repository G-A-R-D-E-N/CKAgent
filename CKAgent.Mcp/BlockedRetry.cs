using System.Globalization;
using System.Text.Json;

namespace Garden.CkAgent;

/// Constraint: a hidden modal leaves the editor refusing work with no reason attached, so a failed call
/// must dismiss that dialog itself, retry, and only report it by name if the block survives.
public static class BlockedEditor
{
    private const int RetryPauseMs = 250;
    private const int RetryAttempts = 2;

    // Constraint: the still-blocked branch must stay reachable, so CKAGENT_TEST_DISMISS_FAILS=1 forces it.
    private static bool DismissSkipped =>
        Environment.GetEnvironmentVariable("CKAGENT_TEST_DISMISS_FAILS") == "1";

    public static bool TryCall(IPluginCaller plugin, string command,
        out string payload, out string code, out string message)
    {
        if (plugin.TryCall(command, out payload, out code, out message))
            return true;

        string? blocked = BlockedBy(plugin, out long? handle);
        if (string.IsNullOrEmpty(blocked))
            return false;

        // Invariant: only the dialog that is blocking is touched, and status names the hidden one alone.
        string? dismissedBy = null;
        if (handle is long target && !DismissSkipped &&
            plugin.TryCall($"ui.dismiss 0x{target:X}", out string dismissal, out _, out _))
            dismissedBy = ReadString(dismissal, "by") ?? "a button";

        for (int attempt = 0; attempt < RetryAttempts; attempt++)
        {
            if (dismissedBy is not null) Thread.Sleep(RetryPauseMs);

            if (plugin.TryCall(command, out payload, out code, out message))
                return true;

            if (dismissedBy is null) break;
        }

        // Constraint: a second queued dialog keeps the editor blocked, so its own handle must be reported.
        string? still = BlockedBy(plugin, out long? remaining);
        if (string.IsNullOrEmpty(still))
            return false;

        // Constraint: the original failure code must survive, so only the message gains the block reason.
        string handleText = (remaining ?? handle) is long h ? $"0x{h:X}" : "the dialog";
        const string byHand = "close it with ck.ui method=ui.click args=\"{0} <button>\" and retry";

        // Invariant: the reply must never claim a dismissal that did not happen.
        string blockText = dismissedBy is not null
            ? $"it was dismissed with {dismissedBy} but the block persists, so {string.Format(byHand, handleText)}"
            : $"it could not be dismissed automatically, so {string.Format(byHand, handleText)}";
        message = $"{message} (the editor is blocked by the modal dialog \"{still}\" at {handleText}; " +
                  $"{blockText})";
        return false;
    }

    public static string? BlockingTitle(IPluginCaller plugin)
    {
        if (!plugin.TryCall("status", out string status, out _, out _))
            return null;

        return ReadString(status, "blockedBy");
    }

    /// Reads the block from status, then requires the modal list for the dialog's hwnd, because a
    /// dismissal requires a handle.
    private static string? BlockedBy(IPluginCaller plugin, out long? handle)
    {
        handle = null;

        if (!plugin.TryCall("status", out string status, out _, out _))
            return null;

        string? title = ReadString(status, "blockedBy");
        if (string.IsNullOrEmpty(title))
            return null;

        if (!plugin.TryCall("ui.modals", out string modals, out _, out _))
            return title;

        try
        {
            using JsonDocument document = JsonDocument.Parse(modals);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return title;

            foreach (JsonElement modal in document.RootElement.EnumerateArray())
            {
                if (!modal.TryGetProperty("blocking", out JsonElement blocking) ||
                    blocking.ValueKind != JsonValueKind.True)
                    continue;

                if (modal.TryGetProperty("hwnd", out JsonElement hwnd) && hwnd.ValueKind == JsonValueKind.String)
                    handle = ParseHandle(hwnd.GetString());

                break;
            }
        }
        catch (JsonException)
        {
            return title;
        }

        return title;
    }

    private static string? ReadString(string json, string name)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty(name, out JsonElement value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The plugin writes handles as 0x-prefixed hex, so the prefix must be dropped before parsing.
    private static long? ParseHandle(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)
            ? value
            : null;
    }
}
