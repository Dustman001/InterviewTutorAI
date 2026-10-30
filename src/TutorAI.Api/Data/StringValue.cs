using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;

namespace TutorAI.Api.Data;

/// <summary>
/// A closed set of string values stored as text (a status, a role). Unlike a constant string, a typo doesn't compile,
/// and unlike an enum, Dapper writes the text itself: it sends enums as their number and ignores handlers for them.
/// </summary>
public interface IStringValue<TSelf> where TSelf : struct, IStringValue<TSelf>
{
    string Value { get; }

    /// <summary>Every valid value.</summary>
    static abstract IReadOnlyList<TSelf> All { get; }
}

public static class StringValue
{
    /// <summary>Exact, case-sensitive match against the valid values.</summary>
    public static bool TryParse<T>(string? text, out T value) where T : struct, IStringValue<T>
    {
        foreach (var candidate in T.All)
        {
            if (string.Equals(candidate.Value, text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static T Parse<T>(string text) where T : struct, IStringValue<T> =>
        TryParse<T>(text, out var value) ? value : throw new FormatException($"'{text}' is not a valid {typeof(T).Name}.");

    /// <summary>"'A' or 'B'", for error messages.</summary>
    public static string Describe<T>() where T : struct, IStringValue<T> =>
        string.Join(" or ", T.All.Select(v => $"'{v.Value}'"));

    internal static void AddDapperHandler<T>() where T : struct, IStringValue<T> =>
        SqlMapper.AddTypeHandler(new DapperHandler<T>());

    private sealed class DapperHandler<T> : SqlMapper.TypeHandler<T> where T : struct, IStringValue<T>
    {
        public override void SetValue(IDbDataParameter parameter, T value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.Value ?? throw new InvalidOperationException($"An uninitialised {typeof(T).Name} can't be stored.");
        }

        // An unknown value in the database fails the read rather than turning into something else.
        public override T Parse(object value) => Parse<T>((string)value);
    }
}

/// <summary>Writes the value as a JSON string and reads only valid values.</summary>
public sealed class StringValueJsonConverter<T> : JsonConverter<T> where T : struct, IStringValue<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        StringValue.TryParse<T>(reader.GetString(), out var value)
            ? value
            : throw new JsonException($"Expected {StringValue.Describe<T>()}.");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
