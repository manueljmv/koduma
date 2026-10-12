# Koduma — v1 scope completion (SQL injection, duplication, exit threshold, MCP params, golden tests, MCP root restriction)

## Contexto y objetivo

El plan original de Koduma (`2026-10-11-koduma-core-design.md`) dejó sin implementar, y sin descartar explícitamente, varios ítems que el spec original listaba como incluidos en v1. La revisión final de esa rama también identificó que el servidor MCP acepta cualquier ruta del sistema de archivos sin restricción. Este documento cubre ambos: completar el alcance v1 pendiente, y acotar la superficie de `analyze_project`.

## Alcance

**Incluido:**
1. Regla de inyección SQL básica (`SEC-SQLI-001`), agregada a `SecretsAnalyzer`.
2. Métrica de duplicación de código (`AnalysisMetrics.DuplicatedLines`), calculada en `AnalysisOrchestrator` sobre todos los archivos descubiertos.
3. Umbral de exit configurable (`--fail-on <severity>`) en el comando `analyze` del CLI.
4. Parámetros `format` y `rules_config` opcionales en la herramienta MCP `analyze_project`.
5. Tests de snapshot/golden para los cuatro generadores de reporte.
6. Restricción de raíz (`--root`) para el servidor MCP (`koduma mcp-serve`), aplicada a `analyze_project`.

**Fuera de alcance:** cualquier otra funcionalidad nueva no listada arriba. No se reabre ninguna decisión arquitectónica ya tomada en el plan original (motor compartido in-proc, AOT, orquestar linters externos, sin persistencia).

## 1. Regla de inyección SQL

`SecretsAnalyzer` (ya `AppliesToAllFiles = true`, agnóstico de lenguaje) gana un tercer patrón junto a los de secretos:

```csharp
("Possible SQL injection", new Regex(
    """(SELECT|INSERT|UPDATE|DELETE|DROP)\b[^"'\n]*["'][^"'\n]*["']\s*(\+|\$\{|#\{)""",
    RegexOptions.Compiled | RegexOptions.IgnoreCase))
```

Produce `Issue` con `RuleId: "SEC-SQLI-001"`, `Severity.Critical`, `Category.Security`. Mismo mecanismo de detección línea por línea que los patrones existentes — ningún cambio estructural a `SecretsAnalyzer`, solo una entrada más en su tabla de patrones y su `Rules` metadata.

**Nota de diseño:** esto es un heurístico de texto (igual que la detección de secretos), no análisis de flujo de datos. Detecta el patrón "SQL + concatenación/interpolación en la misma línea", que es el caso más común de inyección SQL evidente. No detecta construcción de queries a través de múltiples líneas/variables — eso queda fuera de "SAST básico".

## 2. Métrica de duplicación

Calculada una sola vez en `AnalysisOrchestrator.AnalyzeAsync`, sobre `allFiles` (todos los archivos descubiertos, sin distinguir lenguaje):

```csharp
private static int ComputeDuplicatedLines(IReadOnlyList<string> files)
{
    const int WindowSize = 6;
    var windowOccurrences = new Dictionary<string, List<(string File, int StartLine)>>();

    foreach (var file in files)
    {
        var lines = File.ReadAllLines(file)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        for (var i = 0; i + WindowSize <= lines.Count; i++)
        {
            var window = string.Join("\n", lines.Skip(i).Take(WindowSize));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(window)));
            if (!windowOccurrences.TryGetValue(hash, out var occurrences))
                windowOccurrences[hash] = occurrences = new();
            occurrences.Add((file, i));
        }
    }

    var duplicatedLineKeys = new HashSet<(string File, int Line)>();
    foreach (var occurrences in windowOccurrences.Values.Where(o => o.Count > 1))
    {
        foreach (var (file, startLine) in occurrences)
            for (var offset = 0; offset < WindowSize; offset++)
                duplicatedLineKeys.Add((file, startLine + offset));
    }

    return duplicatedLineKeys.Count;
}
```

Esto reemplaza el `totalDuplicated` que hoy siempre suma 0 (porque ningún analizador lo reporta). Los analizadores siguen reportando `DuplicatedLines: 0` en su propio `AnalysisMetrics` — el orquestador calcula el valor real y lo usa en el resultado final en vez de sumar los ceros de cada analizador.

**Nota de diseño:** esto cuenta líneas en archivos de texto, no solo en lenguajes analizados — es una métrica simple y honesta ("hay bloques de 6+ líneas repetidos"), no un detector de clones estructurales. `File.ReadAllLines` no está envuelto en try/catch aquí tampoco — un archivo ilegible entre discovery y este cálculo haría que el cálculo de duplicación (no el análisis completo, gracias al fix de C1) falle; se envuelve igual que los analizadores, con un try/catch que ignora ese archivo específico y continúa.

## 3. Umbral de exit configurable

`AnalyzeCommand.cs` gana un parámetro `--fail-on <severity>` (valores: `info|minor|major|critical|blocker`, case-insensitive, default `critical` — idéntico al comportamiento actual, que hoy comprueba `Severity.Blocker or Severity.Critical`, equivalente a "≥ Critical" dado el orden del enum `Severity { Info, Minor, Major, Critical, Blocker }`).

```csharp
var failOnThreshold = ParseFailOnThreshold(args); // default Severity.Critical
var hasThresholdIssue = result.Issues.Any(i => i.Severity >= failOnThreshold);
return hasThresholdIssue ? 1 : 0;
```

Un valor no reconocido para `--fail-on` produce el mismo tratamiento que un `--format` no reconocido (mensaje claro a stderr, exit 2, sin stack trace) — mismo patrón ya corregido en la revisión final.

## 4. `format`/`rules_config` en MCP `analyze_project`

**`format` (opcional, default `"json"`):** en vez de siempre serializar el `AnalysisResult` crudo vía `KodumaJsonContext`, `analyze_project` busca el `IReportGenerator` cuyo `Format` coincida y devuelve SU salida como el `Text` del `CallToolResult`. Reutiliza el mismo conjunto de generadores (`TableReportGenerator`, `JsonReportGenerator`, `HtmlReportGenerator`, `SarifReportGenerator`) que ya construye `Program.cs` — se pasan a `KodumaMcpServer.RunAsync` igual que los analizadores, no se duplica su construcción.

**`rules_config` (opcional):** objeto JSON con la misma forma que la sección `"rules"` de `koduma.json` (p. ej. `{"CS-NAMING-001": "off"}`). Si se provee, **reemplaza completamente** `config.Rules` del `KodumaConfig` cargado desde archivo para esa llamada (no se mezclan) — `config.Exclude` sigue viniendo del `koduma.json` del proyecto si existe. Para evitar duplicar el parser, `KodumaConfigLoader`'s `ParseRuleOverride` pasa de `private` a `internal static`, y un nuevo método público `KodumaConfigLoader.ParseRulesObject(JsonElement rulesElement) -> Dictionary<string, RuleOverride>` lo envuelve para que `KodumaMcpServer` lo use sobre el `JsonElement` que llega como argumento MCP (no sobre un archivo).

Formato no reconocido o `rules_config` mal formado → el mismo mecanismo de error de herramienta MCP ya existente (`IsError: true` + mensaje), no una excepción sin capturar.

## 5. Tests de snapshot/golden

Nuevos archivos de referencia bajo `tests/Koduma.Reporting.Tests/golden/`: `sample.table.txt`, `sample.json`, `sample.html`, `sample.sarif.json`, generados una vez a partir de un `AnalysisResult` fijo y determinista (`Timestamp` y `DurationMs` fijos, no `DateTimeOffset.UtcNow`/`Stopwatch` — los generadores solo serializan lo que reciben, no generan esos valores ellos mismos, así que el fixture es 100% reproducible).

Cuatro tests nuevos (uno por generador) que: generan la salida fresca a partir del mismo fixture, la comparan byte a byte (normalizando solo saltos de línea `\r\n`→`\n`) contra el archivo golden commiteado, y fallan con un diff legible si no coinciden. Esto es además de los tests funcionales ya existentes (Tasks 9-10) — los golden tests detectan *cualquier* cambio de formato, intencional o no; los funcionales siguen verificando *comportamiento* específico (encoding HTML, estructura SARIF, etc.).

## 6. Restricción de raíz en MCP (`--root`)

`koduma mcp-serve` acepta un argumento posicional opcional `--root <path>` (parseado en `Program.cs`, mismo estilo que `--format` en `AnalyzeCommand`). Si no se provee, el root por defecto es `Directory.GetCurrentDirectory()` en el momento de arrancar `mcp-serve` — protegido por defecto sin configuración.

`KodumaMcpServer.RunAsync` recibe este root y lo usa en `HandleAnalyzeProject`:

```csharp
var requestedFullPath = Path.GetFullPath(path, root);
if (!requestedFullPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
    && requestedFullPath != Path.GetFullPath(root))
{
    throw new InvalidOperationException($"Path '{path}' is outside the configured root '{root}'.");
}
```

Esta excepción es capturada por el manejo de errores de herramienta ya existente (del fix de la revisión final: `catch (Exception ex) { return ToolError(...); }`) — no requiere un nuevo mecanismo de error, solo la validación.

**Nota de diseño:** `Path.GetFullPath(path, root)` resuelve `path` relativo a `root` si `path` no es absoluto, y normaliza `..`/`.` en ambos casos — así `path: "../../etc"` no puede escapar del root vía segmentos relativos.

**Limitación conocida y aceptada:** la comprobación de contención de ruta tiene dos límites residuales que requieren resolución a nivel de kernel (`openat2`/`O_PATH`), no expuesta por .NET: una condición de carrera TOCTOU entre la validación de la ruta y el acceso real al archivo (un symlink podría intercambiarse en medio), y los "magic links" de espacio de nombres de montaje (`/proc/<pid>/root`), que pueden hacer que una ruta apunte a una vista de filesystem distinta de la validada. Ambos se aceptan dado el modelo de amenaza de esta herramienta: un servidor MCP local, de un solo usuario, por stdio — no un servicio multi-tenant expuesto en red.
