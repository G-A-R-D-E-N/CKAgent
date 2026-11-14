using System.Globalization;
using System.Text.Json;

namespace Garden.CkAgent;

/// Constraint: a hidden modal leaves the editor refusing work with no reason attached, so a failed call
/// must be retried once after that dialog is shown and then reported by name if the block persists.
public static class BlockedEditor
{
    private const int RetryPauseMs = 250;
    private const int RetryAttempts = 2;

    // Constraint: the still-blocked branch must stay reachable, so CKAGENT_TEST_REVEAL_FAILS=1 forces it.
    private static bool RevealRefused =>
        Environment.GetEnvironmentVariable("CKAGENT_TEST_REVEAL_FAILS") == "1";

    public static bool TryCall(PluginClient plugin, string command,
        out string payload, out string code, out string message)
    {
        if (plugin.TryCall(command, out payload, out code, out message))
            return true;

        string? blocked = BlockedBy(plugin, out long? handle, out bool visible);
        if (string.IsNullOrEmpty(blocked))
            return false;

        // Invariant: the dialog is shown at most once per call; one that is already visible needs no reveal.
        bool shouldReveal = !visible && handle is long;
        bool revealed = false;
        if (shouldReveal && handle is long shown && !RevealRefused)
            revealed = plugin.TryCall($"ui.modals reveal 0x{shown:X}", out _, out _, out _);

        for (int attempt = 0; attempt < RetryAttempts; attempt++)
        {
            if (revealed) Thread.Sleep(RetryPauseMs);

            if (plugin.TryCall(command, out payload, out code, out message))
                return true;

            if (!revealed) break;
        }

        // Constraint: a second queued dialog keeps the editor blocked, so its own handle must be reported.
        string? still = BlockedBy(plugin, out long? remaining, out _);
        if (string.IsNullOrEmpty(still))
            return false;

        // Constraint: the original failure code must survive, so only the message gains the block reason.
        string handleText = (remaining ?? handle) is long h ? $"0x{h:X}" : "the dialog";
        const string dismiss = "dismiss it with ck.ui method=ui.click args=\"{0} <button>\"";

        // Invariant: three states must stay distinguishable, because a failed reveal is not a visible dialog.
        string shownText = !shouldReveal
            ? "it is already on screen"
            : revealed
                ? $"it was revealed, so {string.Format(dismiss, handleText)}"
                : $"its reveal was refused, so it may still be hidden; {string.Format(dismiss, handleText)}";
        message = $"{message} (the editor is blocked by the modal dialog \"{still}\" at {handleText}; " +
                  $"{shownText} and retry)";
        return false;
    }

    /// Reads the block from status, then requires the modal list for the dialog's hwnd, because a
    /// reveal requires a handle.
    private static string? BlockedBy(PluginClient plugin, out long? handle, out bool visible)
    {
        handle = null;
        visible = false;

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

                if (modal.TryGetProperty("visible", out JsonElement v) && v.ValueKind == JsonValueKind.True)
                    visible = true;

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
