using System.Text.Json.Nodes;
using FinSight.Core.Categories;

namespace FinSight.Infrastructure.Gemini;

/// <summary>Gemini structured-output schemas (an OpenAPI subset). They force the JSON shape; validators check meaning.</summary>
internal static class GeminiSchemas
{
    private static JsonObject Str(string? description = null) => Describe(new JsonObject { ["type"] = "STRING" }, description);

    private static JsonObject Num(string? description = null) => Describe(new JsonObject { ["type"] = "NUMBER" }, description);

    private static JsonObject Enum(IEnumerable<string> values) =>
        new() { ["type"] = "STRING", ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()) };

    private static JsonObject Arr(JsonObject items) => new() { ["type"] = "ARRAY", ["items"] = items };

    private static JsonObject Obj(IReadOnlyList<(string Name, JsonObject Schema, bool Required)> properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema, _) in properties)
        {
            props[name] = schema;
        }

        return new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = props,
            ["required"] = new JsonArray(properties.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
            ["propertyOrdering"] = new JsonArray(properties.Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
        };
    }

    private static JsonObject Describe(JsonObject schema, string? description)
    {
        if (description is not null)
        {
            schema["description"] = description;
        }

        return schema;
    }

    public static JsonObject MerchantCategorization() => Obj(
    [
        ("results", Arr(Obj(
        [
            ("ref", Str("The ref of the merchant being categorized, exactly as given."), true),
            ("categoryId", Enum(CategoryTaxonomy.All.Select(c => c.Id)), true),
            ("merchant", Str("Clean, human-readable merchant name, e.g. 'Netflix'."), true),
            ("confidence", Num("0 to 1. Use below 0.5 when unsure."), true),
            ("reason", Str("One short sentence."), true),
        ])), true),
    ]);

    public static JsonObject FinancialAnalysis() => Obj(
    [
        ("summary", Str("2-4 sentences. Plain language. References actual figures."), true),
        ("keyInsights", Arr(Obj(
        [
            ("title", Str(), true),
            ("description", Str(), true),
            ("severity", Enum(["info", "attention", "positive"]), true),
        ])), true),
        ("savingsOpportunities", Arr(Obj(
        [
            ("categoryId", Str("A category id from the facts."), true),
            ("suggestedMonthlyTarget", Num("A realistic monthly amount below the category's monthlyAverage."), false),
            ("estimatedMonthlySavings", Num(), false),
            ("explanation", Str(), true),
        ])), true),
        ("recurringExpenses", Arr(Obj(
        [
            ("merchant", Str("Exactly as named in recurringExpenses."), true),
            ("note", Str("Optional observation, e.g. overlapping services."), false),
        ])), true),
        ("anomalies", Arr(Obj(
        [
            ("ref", Str("The ref of an anomaly candidate, e.g. A1."), true),
            ("description", Str("A short label."), true),
            ("explanation", Str(), true),
        ])), true),
        ("recommendations", Arr(Obj(
        [
            ("title", Str(), true),
            ("description", Str(), true),
            ("potentialImpact", Str(), false),
        ])), true),
        ("caveats", Arr(Str()), true),
    ]);

    public static JsonObject RecurringReview() => Obj(
    [
        ("results", Arr(Obj(
        [
            ("ref", Str(), true),
            ("kind", Enum(["subscription", "bill", "membership", "loan", "habit", "not_recurring"]), true),
            ("reason", Str("One short sentence."), true),
        ])), true),
    ]);
}
