using System.Collections;
using System.Numerics;
using System.Text.Json.Nodes;

namespace DriftBuster.Backend.Json;

/// <summary>Plain CLR values (detection metadata: strings, numbers, booleans, lists, string-keyed maps) as JSON nodes.</summary>
internal static class JsonNodes
{
    public static JsonNode? From(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string text => text,
        bool flag => flag,
        int number => number,
        long number => number,
        double number => number,
        BigInteger number => JsonNode.Parse(number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        IEnumerable<KeyValuePair<string, object?>> map => new JsonObject(map.Select(entry => KeyValuePair.Create(entry.Key, From(entry.Value)))),
        IDictionary map => new JsonObject(Entries(map).Select(entry => KeyValuePair.Create(Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture)!, From(entry.Value)))),
        IEnumerable items => new JsonArray([.. items.Cast<object?>().Select(From)]),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static IEnumerable<DictionaryEntry> Entries(IDictionary map)
    {
        var enumerator = map.GetEnumerator();
        while (enumerator.MoveNext())
        {
            yield return enumerator.Entry;
        }
    }
}
