using System.Text.Json.Nodes;

namespace WebViewDriver.Selenium.Actions;

/// <summary>
/// Translates WebDriver's private-use-area key codepoints (\uE000-\uE05D, the
/// values behind OpenQA.Selenium.Keys.*) into KeyboardEvent key names. Applied
/// client-side: each key action gains a <c>wvdKey</c> field the in-page
/// interpreter consumes.
/// </summary>
internal static class KeyMapping
{
    private static readonly Dictionary<char, string> PuaToKey = new()
    {
        ['\uE000'] = "Unidentified",
        ['\uE001'] = "Cancel",
        ['\uE002'] = "Help",
        ['\uE003'] = "Backspace",
        ['\uE004'] = "Tab",
        ['\uE005'] = "Clear",
        ['\uE006'] = "Enter", // Keys.Return
        ['\uE007'] = "Enter",
        ['\uE008'] = "Shift",
        ['\uE009'] = "Control",
        ['\uE00A'] = "Alt",
        ['\uE00B'] = "Pause",
        ['\uE00C'] = "Escape",
        ['\uE00D'] = " ",
        ['\uE00E'] = "PageUp",
        ['\uE00F'] = "PageDown",
        ['\uE010'] = "End",
        ['\uE011'] = "Home",
        ['\uE012'] = "ArrowLeft",
        ['\uE013'] = "ArrowUp",
        ['\uE014'] = "ArrowRight",
        ['\uE015'] = "ArrowDown",
        ['\uE016'] = "Insert",
        ['\uE017'] = "Delete",
        ['\uE018'] = ";",
        ['\uE019'] = "=",
        ['\uE01A'] = "0",
        ['\uE01B'] = "1",
        ['\uE01C'] = "2",
        ['\uE01D'] = "3",
        ['\uE01E'] = "4",
        ['\uE01F'] = "5",
        ['\uE020'] = "6",
        ['\uE021'] = "7",
        ['\uE022'] = "8",
        ['\uE023'] = "9",
        ['\uE024'] = "*",
        ['\uE025'] = "+",
        ['\uE026'] = ",",
        ['\uE027'] = "-",
        ['\uE028'] = ".",
        ['\uE029'] = "/",
        ['\uE031'] = "F1",
        ['\uE032'] = "F2",
        ['\uE033'] = "F3",
        ['\uE034'] = "F4",
        ['\uE035'] = "F5",
        ['\uE036'] = "F6",
        ['\uE037'] = "F7",
        ['\uE038'] = "F8",
        ['\uE039'] = "F9",
        ['\uE03A'] = "F10",
        ['\uE03B'] = "F11",
        ['\uE03C'] = "F12",
        ['\uE03D'] = "Meta",
        ['\uE040'] = "ZenkakuHankaku",
        ['\uE050'] = "Shift",   // right-hand variants
        ['\uE051'] = "Control",
        ['\uE052'] = "Alt",
        ['\uE053'] = "Meta",
    };

    public static string ToKeyName(string value)
    {
        if (value.Length == 1 && PuaToKey.TryGetValue(value[0], out var mapped))
        {
            return mapped;
        }

        return value;
    }

    /// <summary>Adds a <c>wvdKey</c> field to every keyDown/keyUp action in place.</summary>
    public static void AnnotateKeyActions(JsonArray sequences)
    {
        foreach (var sequence in sequences)
        {
            if ((string?)sequence?["type"] != "key")
            {
                continue;
            }

            if (sequence?["actions"] is not JsonArray actions)
            {
                continue;
            }

            foreach (var action in actions)
            {
                var type = (string?)action?["type"];
                if (type is "keyDown" or "keyUp" && (string?)action?["value"] is { } value)
                {
                    action!["wvdKey"] = ToKeyName(value);
                }
            }
        }
    }
}
