using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebViewDriver.Selenium;

/// <summary>
/// Converts between the JSON the bridge speaks and the CLR shapes the Selenium
/// client expects (Dictionary&lt;string, object&gt;, List&lt;object&gt;, long/double/bool/string).
/// </summary>
internal static class JsonValueConversion
{
    public static object? ToClr(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var dictionary = new Dictionary<string, object?>();
                foreach (var (key, value) in obj)
                {
                    dictionary[key] = ToClr(value);
                }

                return dictionary;
            }

            case JsonArray array:
                // Selenium's response parsing pattern-matches on object?[] (not List<object>),
                // e.g. in FindElements, WindowHandles and ParseJavaScriptReturnValue.
                return array.Select(ToClr).ToArray();

            case JsonValue value:
            {
                var element = value.GetValue<JsonElement>();
                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    // Cast to object on both arms: the ternary would otherwise unify long/double to double.
                    JsonValueKind.Number => element.TryGetInt64(out var integer) ? (object)integer : (object)element.GetDouble(),
                    _ => element.GetRawText(),
                };
            }

            default:
                return null;
        }
    }

    public static JsonNode? FromClr(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode node:
                return node.DeepClone();
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case int i:
                return JsonValue.Create(i);
            case long l:
                return JsonValue.Create(l);
            case double d:
                return JsonValue.Create(d);
            case float f:
                return JsonValue.Create(f);
            case decimal m:
                return JsonValue.Create(m);
            case System.Collections.IDictionary dictionary:
            {
                var obj = new JsonObject();
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    obj[entry.Key.ToString()!] = FromClr(entry.Value);
                }

                return obj;
            }

            case System.Collections.IEnumerable enumerable:
            {
                var array = new JsonArray();
                foreach (var item in enumerable)
                {
                    array.Add(FromClr(item));
                }

                return array;
            }

            default:
                return JsonValue.Create(value.ToString());
        }
    }
}
