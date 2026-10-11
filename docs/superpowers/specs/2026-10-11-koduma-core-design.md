# Koduma — Motor de calidad de código multi-lenguaje (v1: CLI + MCP)

## Contexto y objetivo

Construir "Koduma": un sistema de análisis de calidad de código similar a SonarQube,
escrito en .NET. La v1 es un binario único que funciona como:
1. **CLI** (`koduma analyze ./repo`), para uso interactivo y en CI.
2. **Servidor MCP** (`koduma mcp-serve`), para que agentes (Claude Code, etc.) invoquen
   el análisis como herramienta.

Un futuro servicio web (persistencia, histórico, quality gates, UI) queda fuera de
alcance de este spec; el diseño no debe impedirlo pero no se construye ahora.

## Alcance de v1

**Incluido:**
- Arquitectura multi-lenguaje basada en plugins desde el día 1.
- Reglas estáticas (code smells), métricas básicas (LOC, complejidad ciclomática,
  duplicación) y reglas de seguridad (SAST básico: secrets hardcodeados, inyección
  SQL evidente, etc.).
- Análisis de C# nativo in-proc vía Roslyn.
- Análisis de otros lenguajes (empezando por JS/TS) orquestando linters externos
  existentes (ESLint) como subproceso, normalizando su salida.
- Reporte por ejecución, sin estado ni histórico entre corridas.
- Salidas: tabla en consola, JSON propio, HTML standalone, SARIF.
- Distribución como ejecutable Native AOT self-contained.

**Explícitamente fuera de alcance de v1:**
- Persistencia, histórico de análisis, quality gates, comparación entre ejecuciones.
- Servicio web / API HTTP / UI.
- Más lenguajes que C# y JS/TS (la arquitectura los soporta; no se implementan).

## Riesgo conocido: Native AOT + Roslyn

Roslyn (`Microsoft.CodeAnalysis.CSharp`) usa reflection en partes de su
infraestructura; su compatibilidad con trimming/NativeAOT es parcial. Compilar
`SyntaxTree`/`Compilation`/modelo semántico básico probablemente funciona, pero
puede haber warnings de trimming o fallos en funcionalidad menos común.

**Mitigación:** el plan de implementación debe incluir, como primer paso, un spike
que publique un analizador Roslyn mínimo con `PublishAot=true` y confirme que
detecta issues reales sobre un fixture de prueba, antes de construir el resto del
analizador de C# sobre ese supuesto. Si el spike falla, se cae a self-contained
sin trimming agresivo solo para el componente que referencia Roslyn, manteniendo
AOT para el resto del binario si es viable, o reevaluando la estrategia de
distribución para todo el binario.

## Arquitectura

```
koduma.sln
├── src/
│   ├── Koduma.Core/                # Dominio + orquestación. Sin dependencias externas.
│   ├── Koduma.Analyzers.CSharp/    # Plugin Roslyn (in-proc)
│   ├── Koduma.Analyzers.External/  # Plugin genérico: invoca linters externos (ESLint, ...) y normaliza su JSON
│   ├── Koduma.Reporting/           # Generadores: Table, Json, Html, Sarif
│   ├── Koduma.Cli/                 # Entry point: comandos `analyze`, `mcp-serve`, `rules list`
│   └── Koduma.Mcp/                 # Capa MCP (tools) sobre Koduma.Core, embebida en el mismo binario
└── tests/
    ├── Koduma.Core.Tests/
    ├── Koduma.Analyzers.CSharp.Tests/
    └── Koduma.Cli.Tests/
```

- `Koduma.Core` define `ILanguageAnalyzer`, `IIssue`, `AnalysisRequest/Result`, y el
  motor de orquestación (descubre archivos, agrupa por lenguaje, invoca el plugin
  correspondiente, agrega resultados). No sabe nada de Roslyn ni de procesos externos.
- Cada plugin de lenguaje implementa:
  `ILanguageAnalyzer.AnalyzeAsync(files, config) -> IReadOnlyList<IIssue>`.
- El binario único (`koduma`) referencia Core + todos los plugins + Reporting + Mcp.
  `Program.cs` decide el modo según el primer argumento (`analyze` / `mcp-serve` / `rules`).
- El servidor MCP y el CLI comparten el mismo proceso y el mismo `Koduma.Core`:
  no hay comunicación por subprocesos entre ellos.

## Modelo de dominio

```
IIssue
├── RuleId (string, ej. "CS-NAMING-001", "SEC-SECRET-001")
├── Severity (Info | Minor | Major | Critical | Blocker)
├── Category (CodeSmell | Bug | Security | Duplication)
├── FilePath, Line, Column, EndLine, EndColumn
├── Message
└── Language

Rule
├── Id, Name, Category, DefaultSeverity
├── Language ("csharp" | "javascript" | ...)
└── EngineRuleId (mapeo al id nativo del motor subyacente, ej. id de ESLint)

AnalysisResult
├── ProjectPath, Timestamp, DurationMs
├── Issues: IReadOnlyList<IIssue>
└── Metrics: { LOC, CyclomaticComplexity, DuplicatedLines, FilesAnalyzed }
```

## Flujo de una ejecución (`koduma analyze ./repo`)

1. **Discovery**: escanea el repo, detecta lenguajes presentes por extensión
   (`.cs`, `.ts`/`.js`, ...), respeta `.gitignore` y un `.kodumaignore` opcional.
2. **Dispatch**: agrupa archivos por lenguaje y lanza el `ILanguageAnalyzer`
   correspondiente en paralelo (uno por lenguaje). C# se analiza con Roslyn
   in-proc; JS/TS invoca `eslint --format json` como subproceso.
3. **Normalización**: cada analizador traduce su salida nativa (diagnósticos de
   Roslyn, JSON de ESLint, etc.) a `IIssue` usando un mapeo de reglas por plugin,
   calculando métricas básicas cuando estén disponibles.
4. **Agregación**: el core combina todos los `IIssue` + métricas en un único
   `AnalysisResult`.
5. **Reporting**: el `AnalysisResult` se pasa al/los generador(es) de salida
   pedido(s) (`--format table|json|html|sarif`, se puede pedir más de uno a la vez).

## Configuración de reglas (`koduma.json`, opcional, en la raíz del repo)

```json
{
  "rules": {
    "CS-NAMING-001": "off",
    "SEC-SECRET-001": { "severity": "blocker" }
  },
  "exclude": ["**/bin/**", "**/obj/**", "**/node_modules/**"]
}
```

Si no existe el archivo, se usa un set de reglas por defecto razonable por lenguaje.

## Superficie MCP (`koduma mcp-serve`, stdio)

- `analyze_project(path, format?, rules_config?)` → ejecuta el análisis completo,
  devuelve el `AnalysisResult` serializado (igual que `--format json`).
- `list_rules(language?)` → lista las reglas disponibles.
- `get_capabilities()` → lenguajes y categorías soportadas.

Sin estado: cada llamada es autocontenida, coherente con la decisión de no
persistir nada entre ejecuciones.

## Manejo de errores

- Si un plugin externo falla (ej. ESLint no instalado o el proceso crashea), ese
  lenguaje se marca como `AnalysisError` en el resultado con el mensaje de causa,
  pero **no aborta** el análisis de los demás lenguajes — degradación parcial.
- Errores de configuración (`koduma.json` inválido) abortan antes de empezar, con
  mensaje claro y exit code `2`.
- Exit codes para CI: `0` sin issues bloqueantes, `1` hay issues de severidad
  `Blocker`/`Critical` (umbral configurable), `2` error de ejecución/config.

## Testing

- **Unit tests** por plugin: fixtures de código fuente con issues conocidos,
  verificando detección exacta (sin falsos positivos/negativos en los fixtures).
- **Golden tests** de reporting: snapshot del HTML/JSON/SARIF generado a partir de
  un `AnalysisResult` fijo, para detectar regresiones de formato.
- **Integration test** end-to-end: repo de ejemplo pequeño (multi-lenguaje)
  analizado por el CLI real, verificando exit code y conteo de issues.
- **Spike de Native AOT + Roslyn**: primer paso del plan de implementación (ver
  sección de riesgo arriba).
