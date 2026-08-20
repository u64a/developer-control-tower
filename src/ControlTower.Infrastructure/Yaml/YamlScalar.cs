namespace ControlTower.Infrastructure.Yaml
{
    /// <summary>Safe YAML scalar formatting. Always quotes values to prevent injection.</summary>
    public static class YamlScalar
    {
        public static string Quote(string value)
        {
            value = value ?? string.Empty;

            if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
            {
                // Multi-line values: use double-quoted style with explicit escapes
                var escaped = value
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
                return "\"" + escaped + "\"";
            }

            // Single-quoted style: only ' needs escaping (doubled)
            return "'" + value.Replace("'", "''") + "'";
        }
    }
}
